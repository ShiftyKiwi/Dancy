using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dancy.Domain;
using Dancy.Pap;
using Dancy.Persistence;
#if DEBUG
using Dancy.Diagnostics;
#endif

namespace Dancy.Services;

public enum TargetInspectionState
{
    Pending,
    Ready,
    Failed,
}

/// <summary>Source-independent target identity. It is safe to retain across source changes.</summary>
public sealed record TargetInspectionRequest(string Id, string TimelineKey, string DisplayName, bool IsStandingIdle);

public sealed record TargetStructureStatus(
    bool IsSupported,
    string Summary,
    string Detail,
    int SupportedVariantCount = 0,
    int TotalVariantCount = 0,
    IReadOnlyList<string>? SupportedGamePaths = null,
    bool UsesStandingIdleWriter = false);

public sealed record TargetStructureSnapshot(
    TargetInspectionState State,
    IReadOnlyList<string> Paths,
    IReadOnlyList<PapTargetInspection> Inspections,
    TargetStructureStatus? Status = null);

public sealed record TargetSourceSelectionPreview(int AnimationCount, SourceAnimationSelection Selection);

public sealed record TargetCompatibilitySnapshot(
    TargetInspectionState State,
    PapCompatibilityResult? Compatibility = null,
    IReadOnlyDictionary<string, TargetSourceSelectionPreview>? SourceSelections = null);

/// <summary>One framework-thread step of game-data path acquisition.</summary>
public interface IIncrementalTargetPathResolution
{
    bool IsCompleted { get; }
    IReadOnlyList<string> Results { get; }
    bool TryAdvance();
}

/// <summary>Framework-thread-only game-data access used by target inspection.</summary>
public interface ITargetInspectionDataSource
{
    IIncrementalTargetPathResolution BeginResolution(string timelineKey);
    byte[] ReadTargetPapBytes(string gamePath);
}

/// <summary>
/// Owns source-independent target acquisition and source-specific compatibility
/// analysis. Game-data acquisition is budgeted on the framework thread; all
/// PAP parsing and compatibility analysis consume immutable bytes off-thread.
/// </summary>
public sealed class TargetInspectionService : IDisposable
{
    private static readonly TimeSpan FrameworkBudget = TimeSpan.FromMilliseconds(2);
    private const int MaximumFrameworkStepsPerUpdate = 2;

    private readonly object gate = new();
    private readonly Queue<TargetWorkItem> pendingTargetWork = new();
    private readonly HashSet<string> pendingTargetIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TargetStructureSnapshot> targetStructures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TargetCompatibilitySnapshot> compatibilityByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<CompletedTargetWork> completedTargetWork = new();
    private readonly ConcurrentQueue<CompletedCompatibilityWork> completedCompatibilityWork = new();
    private readonly ITargetInspectionDataSource dataSource;
    private readonly Func<Action, CancellationToken, Task> startWorker;
    private CancellationTokenSource targetCancellation = new();
    private CancellationTokenSource compatibilityCancellation = new();
    private long targetGeneration;
    private long sourceGeneration;
    private bool disposed;

    public TargetInspectionService(ITargetInspectionDataSource dataSource, Func<Action, CancellationToken, Task>? startWorker = null)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.startWorker = startWorker ?? ((action, cancellationToken) => Task.Run(action, cancellationToken));
    }

    public int PendingTargetCount
    {
        get
        {
            lock (gate)
                return targetStructures.Values.Count(snapshot => snapshot.State == TargetInspectionState.Pending);
        }
    }

    public void RequestTarget(TargetInspectionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TimelineKey);

        lock (gate)
        {
            ThrowIfDisposed();
            if (targetStructures.ContainsKey(request.Id) || !pendingTargetIds.Add(request.Id))
            {
#if DEBUG
                DancyStep3PerformanceTelemetry.RecordTargetInspectionDeduplicated();
#endif
                return;
            }

            targetStructures[request.Id] = new TargetStructureSnapshot(TargetInspectionState.Pending, Array.Empty<string>(), Array.Empty<PapTargetInspection>());
            pendingTargetWork.Enqueue(new TargetWorkItem(request, targetGeneration, targetCancellation.Token));
#if DEBUG
            DancyStep3PerformanceTelemetry.RecordTargetInspectionRequested();
#endif
        }
    }

    public bool TryGetTarget(string id, out TargetStructureSnapshot? snapshot)
    {
        lock (gate)
            return targetStructures.TryGetValue(id, out snapshot);
    }

    public void InvalidateTargetCatalog()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            targetCancellation.Cancel();
            targetCancellation.Dispose();
            targetCancellation = new CancellationTokenSource();
            targetGeneration++;
            pendingTargetWork.Clear();
            pendingTargetIds.Clear();
            targetStructures.Clear();
        }
    }

    public void InvalidateSourceCompatibility()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            compatibilityCancellation.Cancel();
            compatibilityCancellation.Dispose();
            compatibilityCancellation = new CancellationTokenSource();
            sourceGeneration++;
            compatibilityByKey.Clear();
        }
    }

    public void RequestCompatibility(string key, string modFolder, OverridePlan plan, TargetStructureSnapshot target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(target);

        lock (gate)
        {
            ThrowIfDisposed();
            if (compatibilityByKey.ContainsKey(key))
                return;

            compatibilityByKey[key] = new TargetCompatibilitySnapshot(TargetInspectionState.Pending);
            var generation = sourceGeneration;
            var cancellationToken = compatibilityCancellation.Token;
            var immutableTargetInspections = target.Inspections.ToArray();
            _ = startWorker(() => AnalyzeCompatibility(key, generation, cancellationToken, modFolder, plan, immutableTargetInspections), cancellationToken);
        }
    }

    public bool TryGetCompatibility(string key, out TargetCompatibilitySnapshot? snapshot)
    {
        lock (gate)
            return compatibilityByKey.TryGetValue(key, out snapshot);
    }

    /// <summary>Called from IFramework.Update, never from ImGui Draw.</summary>
    public void Update()
    {
        if (disposed)
            return;

        PublishCompletedWork();
        var started = Stopwatch.GetTimestamp();
        var processedSteps = 0;
        while (processedSteps < MaximumFrameworkStepsPerUpdate && Elapsed(started) < FrameworkBudget)
        {
            TargetWorkItem? work;
            lock (gate)
            {
                if (!pendingTargetWork.TryDequeue(out work))
                    break;
                pendingTargetIds.Remove(work.Request.Id);
            }

            if (work.CancellationToken.IsCancellationRequested || work.Generation != Volatile.Read(ref targetGeneration))
                continue;

            var completed = AdvanceTargetWork(work);
            processedSteps++;
            if (completed)
                StartTargetAnalysis(work);
            else
            {
                lock (gate)
                {
                    if (!work.CancellationToken.IsCancellationRequested && work.Generation == targetGeneration && pendingTargetIds.Add(work.Request.Id))
                        pendingTargetWork.Enqueue(work);
                }
            }
        }

        PublishCompletedWork();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            targetCancellation.Cancel();
            compatibilityCancellation.Cancel();
            targetCancellation.Dispose();
            compatibilityCancellation.Dispose();
            pendingTargetWork.Clear();
            pendingTargetIds.Clear();
            targetStructures.Clear();
            compatibilityByKey.Clear();
        }
    }

    private bool AdvanceTargetWork(TargetWorkItem work)
    {
        work.Resolver ??= dataSource.BeginResolution(work.Request.TimelineKey);
        if (!work.Resolver.IsCompleted)
        {
            work.Resolver.TryAdvance();
            return false;
        }

        if (work.Paths is null)
        {
            work.Paths = work.Resolver.Results.ToArray();
            if (work.Paths.Length == 0)
                return true;
        }

        if (work.NextPathIndex < work.Paths.Length)
        {
            var path = work.Paths[work.NextPathIndex++];
            try
            {
                work.Bytes.Add(new TargetPapBytes(path, dataSource.ReadTargetPapBytes(path)));
            }
            catch (Exception exception)
            {
                work.ReadFailure = exception;
                return true;
            }
            return false;
        }

        return true;
    }

    private void StartTargetAnalysis(TargetWorkItem work)
    {
#if DEBUG
        DancyStep3PerformanceTelemetry.RecordTargetInspectionExecuted();
#endif
        var paths = work.Paths ?? Array.Empty<string>();
        var bytes = work.Bytes.ToArray();
        _ = startWorker(() =>
        {
            try
            {
                work.CancellationToken.ThrowIfCancellationRequested();
                TargetStructureSnapshot result;
                if (work.ReadFailure is not null)
                {
                    result = FailedTarget(work.Request.DisplayName);
                }
                else if (paths.Length == 0)
                {
                    result = new TargetStructureSnapshot(
                        TargetInspectionState.Ready,
                        paths,
                        Array.Empty<PapTargetInspection>(),
                        new TargetStructureStatus(false, "Unavailable", $"Dancy recognizes {work.Request.DisplayName}, but could not find a usable current-game target. Refresh Data, then choose another target if it remains unavailable."));
                }
                else
                {
#if DEBUG
                    DancyStep3PerformanceTelemetry.RecordTargetStructuralPreflight();
                    using var timing = DancyStep3PerformanceTelemetry.Measure("InspectTargetStructure");
#endif
                    var inspections = bytes
                        .Select(item => new PapTargetInspection(item.GamePath, InspectTargetPapBytes(item.Bytes)))
                        .ToArray();
                    result = new TargetStructureSnapshot(TargetInspectionState.Ready, paths, inspections, AnalyzeTargetStructure(work.Request, inspections));
                }
                completedTargetWork.Enqueue(new CompletedTargetWork(work.Request.Id, work.Generation, result));
            }
            catch (OperationCanceledException)
            {
                // A newer catalog generation owns the result now.
            }
            catch (Exception)
            {
                completedTargetWork.Enqueue(new CompletedTargetWork(work.Request.Id, work.Generation, FailedTarget(work.Request.DisplayName)));
            }
        }, work.CancellationToken);
    }

    private void AnalyzeCompatibility(
        string key,
        long generation,
        CancellationToken cancellationToken,
        string modFolder,
        OverridePlan plan,
        IReadOnlyList<PapTargetInspection> targetInspections)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
#if DEBUG
            DancyStep3PerformanceTelemetry.RecordPreviewCompatibilityPreflight();
            using var timing = DancyStep3PerformanceTelemetry.Measure("InspectPreviewCompatibility");
#endif
            var selections = new Dictionary<string, TargetSourceSelectionPreview>(StringComparer.OrdinalIgnoreCase);
            PapCompatibilityResult result;
            if (!plan.IsValid)
            {
                result = new PapCompatibilityResult(PapCompatibilityStatus.Unknown, "Select valid Loop source paths before compatibility can be inspected.");
            }
            else
            {
                var results = plan.PapCopies.Select(copy =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!PathSafety.TryResolveInsideRoot(modFolder, copy.SourcePapPath, out var sourcePath))
                        throw new InvalidOperationException($"Dancy refused an unsafe source PAP path: {copy.SourcePapPath}");

                    var source = PapFileInspector.InspectFile(sourcePath);
                    var selectionResult = SourceAnimationSelectionResolver.Resolve(modFolder, copy);
                    if (!selectionResult.IsSuccess || selectionResult.Selection is null)
                    {
                        return new PapCompatibilityResult(
                            PapCompatibilityStatus.Unsupported,
                            selectionResult.Error ?? "Dancy could not select one source animation from the PAP.",
                            Blocker: PapCompatibilityBlocker.Source);
                    }

                    selections[copy.OutputRelativePath] = new TargetSourceSelectionPreview(source.AnimationCount, selectionResult.Selection);
                    var targets = copy.TargetGamePaths
                        .Select(path => targetInspections.FirstOrDefault(target => string.Equals(target.GamePath, path, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    if (targets.Any(target => target is null))
                        return new PapCompatibilityResult(PapCompatibilityStatus.Unknown, "Dancy is still preparing this target's PAP structure.");
                    return PapCompatibilityPreflight.Evaluate(source, selectionResult.Selection, targets!);
                });
                result = PapCompatibilityPreflight.Combine(results);
            }
            completedCompatibilityWork.Enqueue(new CompletedCompatibilityWork(key, generation, new TargetCompatibilitySnapshot(TargetInspectionState.Ready, result, selections)));
        }
        catch (OperationCanceledException)
        {
            // A newer source selection owns compatibility now.
        }
        catch (Exception exception)
        {
            completedCompatibilityWork.Enqueue(new CompletedCompatibilityWork(
                key,
                generation,
                new TargetCompatibilitySnapshot(TargetInspectionState.Failed, new PapCompatibilityResult(PapCompatibilityStatus.Unknown, $"Dancy could not inspect compatibility yet: {exception.Message}"))));
        }
    }

    private void PublishCompletedWork()
    {
        while (completedTargetWork.TryDequeue(out var completed))
        {
            lock (gate)
            {
                if (!disposed && completed.Generation == targetGeneration)
                    targetStructures[completed.Id] = completed.Snapshot;
            }
        }

        while (completedCompatibilityWork.TryDequeue(out var completed))
        {
            lock (gate)
            {
                if (!disposed && completed.Generation == sourceGeneration)
                    compatibilityByKey[completed.Key] = completed.Snapshot;
            }
        }
    }

    private static TargetStructureStatus AnalyzeTargetStructure(TargetInspectionRequest request, IReadOnlyList<PapTargetInspection> inspections)
    {
        if (request.IsStandingIdle)
        {
            var variants = StandingIdleVariantCatalog.Analyze(inspections);
            var detail = variants.AllVariantsSupported
                ? "Dancy can safely use every current Standing Idle variant while preserving the target's required idle structure."
                : variants.HasSupportedVariants
                    ? $"Dancy can safely use {variants.SupportedVariantCount} of {variants.TotalVariantCount} current Standing Idle variants. The remaining variants stay untouched."
                    : "Dancy recognizes Standing Idle, but none of its current variants can be used safely.";
            return new TargetStructureStatus(
                variants.HasSupportedVariants,
                variants.HasSupportedVariants ? variants.AllVariantsSupported ? "Compatible" : "Compatible with limits" : "Unavailable",
                detail,
                variants.SupportedVariantCount,
                variants.TotalVariantCount,
                variants.SupportedGamePaths,
                UsesStandingIdleWriter: true);
        }

        if (inspections.All(inspection => inspection.Inspection.AnimationCount == 1 && inspection.Inspection.TimelineSectionSizes.Count == 1))
            return new TargetStructureStatus(true, "Compatible", "Dancy can safely use this target's current variants.");

        return new TargetStructureStatus(
            false,
            "Unsupported structure",
            $"Dancy recognizes {request.DisplayName}, but its current animation layout is not one Dancy can safely override. Choose a compatible target instead.");
    }

    private static TargetStructureSnapshot FailedTarget(string displayName)
        => new(
            TargetInspectionState.Failed,
            Array.Empty<string>(),
            Array.Empty<PapTargetInspection>(),
            new TargetStructureStatus(false, "Unavailable", $"Dancy could not inspect {displayName} yet. Refresh Data or choose another target."));

    private static PapFileInspector.PapFileInspection InspectTargetPapBytes(byte[] bytes)
    {
#if DEBUG
        DancyStep3PerformanceTelemetry.RecordTargetPapInspection();
#endif
        return PapFileInspector.Inspect(bytes);
    }

    private static TimeSpan Elapsed(long started) => Stopwatch.GetElapsedTime(started);

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(TargetInspectionService));
    }

    private sealed class TargetWorkItem
    {
        public TargetWorkItem(TargetInspectionRequest request, long generation, CancellationToken cancellationToken)
        {
            Request = request;
            Generation = generation;
            CancellationToken = cancellationToken;
        }

        public TargetInspectionRequest Request { get; }
        public long Generation { get; }
        public CancellationToken CancellationToken { get; }
        public IIncrementalTargetPathResolution? Resolver { get; set; }
        public string[]? Paths { get; set; }
        public int NextPathIndex { get; set; }
        public List<TargetPapBytes> Bytes { get; } = new();
        public Exception? ReadFailure { get; set; }
    }

    private sealed record TargetPapBytes(string GamePath, byte[] Bytes);
    private sealed record CompletedTargetWork(string Id, long Generation, TargetStructureSnapshot Snapshot);
    private sealed record CompletedCompatibilityWork(string Key, long Generation, TargetCompatibilitySnapshot Snapshot);
}
