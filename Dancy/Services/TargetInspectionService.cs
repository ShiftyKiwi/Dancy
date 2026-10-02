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

/// <summary>Higher-priority target work is always acquired before lower-priority prewarming.</summary>
public enum TargetInspectionPriority
{
    Selected = 0,
    VisibleSearch = 1,
    VisibleCurrentTab = 2,
    CurrentTab = 3,
    Background = 4,
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

/// <summary>Debug-only presentation of one target's scheduler lifecycle.</summary>
public sealed record TargetInspectionDebugEntry(
    string Id,
    string DisplayName,
    TargetInspectionPriority Priority,
    int? QueuePosition,
    string AcquisitionState,
    string AnalysisState,
    long Generation,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    int FileExistsRequests,
    int FileExistsCacheHits,
    int UnderlyingFileExistsProbes);

/// <summary>Debug-only scheduler state for diagnosing deferred inspection progress.</summary>
public sealed record TargetInspectionServiceDebugState(
    long TargetGeneration,
    long SourceGeneration,
    int PendingRequests,
    int FrameworkAcquisitionQueueLength,
    IReadOnlyList<int> QueueLengthsByPriority,
    int ActiveBackgroundAnalysis,
    int CompletedResults,
    int FailedResults,
    long DiscardedStaleResults,
    string? ActiveAcquisition,
    IReadOnlyList<string> ActiveAnalysis,
    TimeSpan? OldestPendingAge,
    DateTimeOffset? LastSuccessfulProgressUtc,
    long FrameworkUpdateCallbacks,
    long AcquisitionSteps,
    IReadOnlyList<TargetInspectionDebugEntry> Targets);

/// <summary>One framework-thread step of game-data path acquisition.</summary>
public interface IIncrementalTargetPathResolution
{
    bool IsCompleted { get; }
    IReadOnlyList<string> Results { get; }
    int FileExistsRequests { get; }
    int FileExistsCacheHits { get; }
    int UnderlyingFileExistsProbes { get; }
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
    // The deadline is the normal limit. This only prevents a faulty zero-cost
    // resolver from monopolizing a frame if its clock never advances.
    private const int EmergencyMaximumFrameworkStepsPerUpdate = 16 * 1024;
    private const int PendingWarningSeconds = 5;
    private const int PendingAbnormalSeconds = 15;

    private readonly object gate = new();
    private readonly LinkedList<TargetWorkItem>[] pendingTargetWork = CreatePriorityQueues();
    private readonly Dictionary<string, LinkedListNode<TargetWorkItem>> pendingTargetNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TargetWorkItem> targetWorkById = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activeBackgroundAnalysis = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TargetStructureSnapshot> targetStructures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TargetCompatibilitySnapshot> compatibilityByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<CompletedTargetWork> completedTargetWork = new();
    private readonly ConcurrentQueue<CompletedCompatibilityWork> completedCompatibilityWork = new();
    private readonly ITargetInspectionDataSource dataSource;
    private readonly Func<Action, CancellationToken, Task> startWorker;
    private readonly Action<string>? pendingWarning;
    private CancellationTokenSource targetCancellation = new();
    private CancellationTokenSource compatibilityCancellation = new();
    private long targetGeneration;
    private long sourceGeneration;
    private long discardedStaleResults;
    private long frameworkUpdateCallbacks;
    private long acquisitionSteps;
    private int completedResults;
    private int failedResults;
    private DateTimeOffset? lastSuccessfulProgressUtc;
    private TargetWorkItem? activeAcquisition;
    private bool disposed;

    public TargetInspectionService(
        ITargetInspectionDataSource dataSource,
        Func<Action, CancellationToken, Task>? startWorker = null,
        Action<string>? pendingWarning = null)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.startWorker = startWorker ?? ((action, cancellationToken) => Task.Run(action, cancellationToken));
        this.pendingWarning = pendingWarning;
    }

    public int PendingTargetCount
    {
        get
        {
            lock (gate)
                return targetStructures.Values.Count(snapshot => snapshot.State == TargetInspectionState.Pending);
        }
    }

    public void RequestTarget(TargetInspectionRequest request, TargetInspectionPriority priority = TargetInspectionPriority.Background)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TimelineKey);

        lock (gate)
        {
            ThrowIfDisposed();
            if (targetStructures.ContainsKey(request.Id))
            {
                PromotePendingWork(request.Id, priority);
#if DEBUG
                DancyStep3PerformanceTelemetry.RecordTargetInspectionDeduplicated();
#endif
                return;
            }

            targetStructures[request.Id] = new TargetStructureSnapshot(TargetInspectionState.Pending, Array.Empty<string>(), Array.Empty<PapTargetInspection>());
            var work = new TargetWorkItem(request, priority, targetGeneration, targetCancellation.Token);
            targetWorkById.Add(request.Id, work);
            EnqueuePendingWork(work);
#if DEBUG
            DancyStep3PerformanceTelemetry.RecordTargetInspectionRequested();
#endif
        }
    }

    public TargetInspectionServiceDebugState GetDebugState()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var queuePositions = GetQueuePositions();
            var pending = targetStructures
                .Where(pair => pair.Value.State == TargetInspectionState.Pending)
                .Select(pair => targetWorkById.TryGetValue(pair.Key, out var work) ? work : null)
                .Where(work => work is not null)
                .Cast<TargetWorkItem>()
                .ToList();
            var oldest = pending.Count == 0 ? (TimeSpan?)null : now - pending.Min(work => work.RequestedAtUtc);
            var targets = targetWorkById.Values
                .OrderBy(work => work.RequestedAtUtc)
                .Select(work => new TargetInspectionDebugEntry(
                    work.Request.Id,
                    work.Request.DisplayName,
                    work.Priority,
                    queuePositions.GetValueOrDefault(work.Request.Id),
                    work.AcquisitionState,
                    work.AnalysisState,
                    work.Generation,
                    work.RequestedAtUtc,
                    work.CompletedAtUtc,
                    work.PublishedAtUtc,
                    work.Resolver?.FileExistsRequests ?? 0,
                    work.Resolver?.FileExistsCacheHits ?? 0,
                    work.Resolver?.UnderlyingFileExistsProbes ?? 0))
                .ToList();
            return new TargetInspectionServiceDebugState(
                targetGeneration,
                sourceGeneration,
                pending.Count,
                pendingTargetNodes.Count,
                pendingTargetWork.Select(queue => queue.Count).ToList(),
                activeBackgroundAnalysis.Count,
                completedResults,
                failedResults,
                discardedStaleResults,
                activeAcquisition?.Request.Id,
                activeBackgroundAnalysis.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
                oldest,
                lastSuccessfulProgressUtc,
                frameworkUpdateCallbacks,
                acquisitionSteps,
                targets);
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
            ClearPendingTargetWork();
            targetWorkById.Clear();
            activeBackgroundAnalysis.Clear();
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
            try
            {
                ObserveCompatibilityWorker(
                    startWorker(() => AnalyzeCompatibility(key, generation, cancellationToken, modFolder, plan, immutableTargetInspections), cancellationToken),
                    key,
                    generation,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                completedCompatibilityWork.Enqueue(new CompletedCompatibilityWork(
                    key,
                    generation,
                    new TargetCompatibilitySnapshot(
                        TargetInspectionState.Failed,
                        new PapCompatibilityResult(PapCompatibilityStatus.Unknown, $"Dancy could not start compatibility inspection: {exception.Message}"))));
            }
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

        lock (gate)
            frameworkUpdateCallbacks++;
        PublishCompletedWork();
        var started = Stopwatch.GetTimestamp();
        var processedSteps = 0;
        while (processedSteps < EmergencyMaximumFrameworkStepsPerUpdate && Elapsed(started) < FrameworkBudget)
        {
            TargetWorkItem? work;
            lock (gate)
            {
                work = DequeuePendingWork();
                if (work is null)
                    break;
                activeAcquisition = work;
            }

            try
            {
                if (work.CancellationToken.IsCancellationRequested || work.Generation != Volatile.Read(ref targetGeneration))
                {
                    RecordDiscardedTargetWork(work);
                    continue;
                }

#if DEBUG
                var stepStarted = Stopwatch.GetTimestamp();
#endif
                var completed = AdvanceTargetWork(work);
#if DEBUG
                DancyStep3PerformanceTelemetry.RecordElapsed("TargetInspectionAcquisitionStep", stepStarted);
#endif
                processedSteps++;
                lock (gate)
                {
                    acquisitionSteps++;
                    lastSuccessfulProgressUtc = DateTimeOffset.UtcNow;
                }
                if (completed)
                    StartTargetAnalysis(work);
                else
                {
                    lock (gate)
                    {
                        if (!work.CancellationToken.IsCancellationRequested && work.Generation == targetGeneration)
                            EnqueuePendingWork(work);
                    }
                }
            }
            catch (Exception exception)
            {
                QueueTargetFailure(work, exception);
            }
            finally
            {
                lock (gate)
                    activeAcquisition = null;
            }
        }

#if DEBUG
        var acquisitionElapsed = Elapsed(started);
        bool pendingAfterUpdate;
        lock (gate)
            pendingAfterUpdate = pendingTargetNodes.Count > 0;
        DancyStep3PerformanceTelemetry.RecordTargetInspectionAcquisitionUpdate(
            acquisitionElapsed,
            FrameworkBudget,
            limitedByStepCap: false,
            hitEmergencyStepCap: processedSteps >= EmergencyMaximumFrameworkStepsPerUpdate && pendingAfterUpdate);
#endif

        PublishCompletedWork();
        ReportPendingWatchdog();
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
            ClearPendingTargetWork();
            targetWorkById.Clear();
            activeBackgroundAnalysis.Clear();
            targetStructures.Clear();
            compatibilityByKey.Clear();
        }
    }

    private bool AdvanceTargetWork(TargetWorkItem work)
    {
        if (work.Resolver is null)
        {
            work.AcquisitionState = "Resolving game paths";
            work.Resolver = dataSource.BeginResolution(work.Request.TimelineKey);
        }
        if (!work.Resolver.IsCompleted)
        {
            work.Resolver.TryAdvance();
            return false;
        }

        if (work.Paths is null)
        {
            work.Paths = work.Resolver.Results.ToArray();
            work.AcquisitionState = work.Paths.Length == 0 ? "No game paths found" : $"Reading PAPs (0/{work.Paths.Length})";
            if (work.Paths.Length == 0)
                return true;
        }

        if (work.NextPathIndex < work.Paths.Length)
        {
            var path = work.Paths[work.NextPathIndex++];
            work.AcquisitionState = $"Reading PAPs ({work.NextPathIndex}/{work.Paths.Length})";
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

        work.AcquisitionState = "Acquired";
        return true;
    }

    private void StartTargetAnalysis(TargetWorkItem work)
    {
#if DEBUG
        DancyStep3PerformanceTelemetry.RecordTargetInspectionExecuted();
#endif
        var paths = work.Paths ?? Array.Empty<string>();
        var bytes = work.Bytes.ToArray();
        lock (gate)
        {
            work.AcquisitionState = "Acquired";
            work.AnalysisState = "Queued";
            activeBackgroundAnalysis.Add(work.Request.Id);
        }
        try
        {
            ObserveTargetWorker(startWorker(() =>
            {
                try
                {
                    lock (gate)
                        work.AnalysisState = "Running";
                    work.CancellationToken.ThrowIfCancellationRequested();
                    TargetStructureSnapshot result;
                    if (work.ReadFailure is not null)
                    {
                        result = FailedTarget(work.Request.DisplayName, work.ReadFailure);
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
                    QueueCompletedTargetWork(work, result);
                }
                catch (OperationCanceledException) when (work.CancellationToken.IsCancellationRequested)
                {
                    RecordDiscardedTargetWork(work);
                }
                catch (Exception exception)
                {
                    QueueTargetFailure(work, exception);
                }
            }, work.CancellationToken), work);
        }
        catch (Exception exception)
        {
            QueueTargetFailure(work, exception);
        }
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
                if (disposed || completed.Generation != targetGeneration)
                {
                    discardedStaleResults++;
                    continue;
                }

                targetStructures[completed.Id] = completed.Snapshot;
                if (targetWorkById.TryGetValue(completed.Id, out var work))
                {
                    work.PublishedAtUtc = DateTimeOffset.UtcNow;
                    work.AcquisitionState = completed.Snapshot.State == TargetInspectionState.Failed ? "Failed" : "Published";
                    work.AnalysisState = completed.Snapshot.State == TargetInspectionState.Failed ? "Failed" : "Published";
                }
                completedResults++;
                if (completed.Snapshot.State == TargetInspectionState.Failed)
                    failedResults++;
                lastSuccessfulProgressUtc = DateTimeOffset.UtcNow;
            }
        }

        while (completedCompatibilityWork.TryDequeue(out var completed))
        {
            lock (gate)
            {
                if (disposed || completed.Generation != sourceGeneration)
                {
                    discardedStaleResults++;
                    continue;
                }

                compatibilityByKey[completed.Key] = completed.Snapshot;
            }
        }
    }

    private static LinkedList<TargetWorkItem>[] CreatePriorityQueues()
        => Enum.GetValues<TargetInspectionPriority>().Select(_ => new LinkedList<TargetWorkItem>()).ToArray();

    private void EnqueuePendingWork(TargetWorkItem work)
    {
        var priority = (int)work.Priority;
        var node = pendingTargetWork[priority].AddLast(work);
        pendingTargetNodes[work.Request.Id] = node;
        work.AcquisitionState = "Queued";
    }

    private TargetWorkItem? DequeuePendingWork()
    {
        foreach (var queue in pendingTargetWork)
        {
            if (queue.First is not { } node)
                continue;

            queue.RemoveFirst();
            pendingTargetNodes.Remove(node.Value.Request.Id);
            return node.Value;
        }

        return null;
    }

    private void PromotePendingWork(string id, TargetInspectionPriority priority)
    {
        if (!pendingTargetNodes.TryGetValue(id, out var node) || node.Value.Priority <= priority)
            return;

        node.List!.Remove(node);
        node.Value.Priority = priority;
        pendingTargetNodes[id] = pendingTargetWork[(int)priority].AddFirst(node.Value);
        node.Value.AcquisitionState = "Queued (promoted)";
    }

    private void ClearPendingTargetWork()
    {
        foreach (var queue in pendingTargetWork)
            queue.Clear();
        pendingTargetNodes.Clear();
        activeAcquisition = null;
    }

    private Dictionary<string, int> GetQueuePositions()
    {
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        foreach (var queue in pendingTargetWork)
        {
            foreach (var work in queue)
                positions[work.Request.Id] = position++;
        }

        return positions;
    }

    private void QueueCompletedTargetWork(TargetWorkItem work, TargetStructureSnapshot snapshot)
    {
        lock (gate)
        {
            activeBackgroundAnalysis.Remove(work.Request.Id);
            work.CompletedAtUtc = DateTimeOffset.UtcNow;
            work.AnalysisState = "Completed (awaiting publish)";
        }
        completedTargetWork.Enqueue(new CompletedTargetWork(work.Request.Id, work.Generation, snapshot));
    }

    private void QueueTargetFailure(TargetWorkItem work, Exception exception)
    {
        lock (gate)
        {
            activeBackgroundAnalysis.Remove(work.Request.Id);
            work.CompletedAtUtc = DateTimeOffset.UtcNow;
            work.AcquisitionState = "Failed";
            work.AnalysisState = $"Failed: {exception.GetType().Name}";
        }
        completedTargetWork.Enqueue(new CompletedTargetWork(work.Request.Id, work.Generation, FailedTarget(work.Request.DisplayName, exception)));
    }

    private void RecordDiscardedTargetWork(TargetWorkItem work)
    {
        lock (gate)
        {
            activeBackgroundAnalysis.Remove(work.Request.Id);
            discardedStaleResults++;
            work.AnalysisState = "Discarded as stale";
        }
    }

    private void ObserveTargetWorker(Task worker, TargetWorkItem work)
        => _ = ObserveTargetWorkerAsync(worker, work);

    private async Task ObserveTargetWorkerAsync(Task worker, TargetWorkItem work)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (work.CancellationToken.IsCancellationRequested)
        {
            RecordDiscardedTargetWork(work);
        }
        catch (Exception exception)
        {
            QueueTargetFailure(work, exception);
        }
    }

    private void ObserveCompatibilityWorker(Task worker, string key, long generation, CancellationToken cancellationToken)
        => _ = ObserveCompatibilityWorkerAsync(worker, key, generation, cancellationToken);

    private async Task ObserveCompatibilityWorkerAsync(Task worker, string key, long generation, CancellationToken cancellationToken)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Source changes clear the previous generation's pending state.
        }
        catch (Exception exception)
        {
            completedCompatibilityWork.Enqueue(new CompletedCompatibilityWork(
                key,
                generation,
                new TargetCompatibilitySnapshot(
                    TargetInspectionState.Failed,
                    new PapCompatibilityResult(PapCompatibilityStatus.Unknown, $"Dancy compatibility inspection failed: {exception.Message}"))));
        }
    }

    private void ReportPendingWatchdog()
    {
        if (pendingWarning is null)
            return;

        List<string>? warnings = null;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var work in targetWorkById.Values)
            {
                if (!targetStructures.TryGetValue(work.Request.Id, out var snapshot) || snapshot.State != TargetInspectionState.Pending)
                    continue;

                var age = now - work.RequestedAtUtc;
                if (age >= TimeSpan.FromSeconds(PendingAbnormalSeconds) && !work.ReportedAbnormalPending)
                {
                    work.ReportedAbnormalPending = true;
                    (warnings ??= []).Add($"[Dancy] Target inspection has remained pending for {age.TotalSeconds:F1}s: {work.Request.DisplayName} [{work.Request.Id}], acquisition={work.AcquisitionState}, analysis={work.AnalysisState}, generation={work.Generation}.");
                }
                else if (age >= TimeSpan.FromSeconds(PendingWarningSeconds) && !work.ReportedSlowPending)
                {
                    work.ReportedSlowPending = true;
                    (warnings ??= []).Add($"[Dancy] Target inspection is still pending after {age.TotalSeconds:F1}s: {work.Request.DisplayName} [{work.Request.Id}], acquisition={work.AcquisitionState}, analysis={work.AnalysisState}, generation={work.Generation}.");
                }
            }
        }

        if (warnings is not null)
        {
            foreach (var warning in warnings)
                pendingWarning(warning);
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

    private static TargetStructureSnapshot FailedTarget(string displayName, Exception? exception = null)
        => new(
            TargetInspectionState.Failed,
            Array.Empty<string>(),
            Array.Empty<PapTargetInspection>(),
            new TargetStructureStatus(
                false,
                "Unavailable",
                exception is null
                    ? $"Dancy could not inspect {displayName} yet. Refresh Data or choose another target."
                    : $"Dancy could not inspect {displayName}: {exception.Message}"));

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
        public TargetWorkItem(TargetInspectionRequest request, TargetInspectionPriority priority, long generation, CancellationToken cancellationToken)
        {
            Request = request;
            Priority = priority;
            Generation = generation;
            CancellationToken = cancellationToken;
            RequestedAtUtc = DateTimeOffset.UtcNow;
        }

        public TargetInspectionRequest Request { get; }
        public TargetInspectionPriority Priority { get; set; }
        public long Generation { get; }
        public CancellationToken CancellationToken { get; }
        public DateTimeOffset RequestedAtUtc { get; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public DateTimeOffset? PublishedAtUtc { get; set; }
        public string AcquisitionState { get; set; } = "Queued";
        public string AnalysisState { get; set; } = "Not started";
        public bool ReportedSlowPending { get; set; }
        public bool ReportedAbnormalPending { get; set; }
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
