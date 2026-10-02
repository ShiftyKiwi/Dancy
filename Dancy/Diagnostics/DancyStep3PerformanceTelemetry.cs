#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Dancy.Diagnostics;

/// <summary>
/// Debug-only counters for the Step 3 target catalog. These are deliberately
/// observational: they never change compatibility or writer behavior.
/// </summary>
internal static class DancyStep3PerformanceTelemetry
{
    private const int TimingSampleLimit = 4096;

    private sealed class TimingCounter
    {
        public long Calls;
        public long TotalTicks;
        public long MaxTicks;
        public List<long> Samples { get; } = [];
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, TimingCounter> Timings = new(StringComparer.Ordinal);
    private static long papResolverCacheHits;
    private static long papResolverCacheMisses;
    private static long gameFileExistsCalls;
    private static long gameFileExistsCacheHits;
    private static long gameFileExistsCacheMisses;
    private static long targetPapReads;
    private static long targetPapInspections;
    private static long sourcePapFileReads;
    private static long targetStructuralPreflights;
    private static long previewCompatibilityPreflights;
    private static long penumbraIpcCalls;
    private static long presentationQueries;
    private static long statusQueueTicks;
    private static long targetInspectionRequests;
    private static long targetInspectionExecutions;
    private static long targetInspectionDeduplicated;
    private static long targetInspectionAcquisitionBudgetOverruns;
    private static long targetInspectionAcquisitionStepCapLimitedUpdates;
    private static long targetInspectionAcquisitionEmergencyCapHits;
    private static long targetInspectionAcquisitionFrameHitchesOver16Milliseconds;
    private static long targetInspectionAcquisitionFrameHitchesOver50Milliseconds;
    private static long targetInspectionAcquisitionFrameHitchesOver100Milliseconds;

    public static TimingScope Measure(string stage) => new(stage, Stopwatch.GetTimestamp());

    public static void Reset()
    {
        lock (Gate)
        {
            Timings.Clear();
            papResolverCacheHits = 0;
            papResolverCacheMisses = 0;
            gameFileExistsCalls = 0;
            gameFileExistsCacheHits = 0;
            gameFileExistsCacheMisses = 0;
            targetPapReads = 0;
            targetPapInspections = 0;
            sourcePapFileReads = 0;
            targetStructuralPreflights = 0;
            previewCompatibilityPreflights = 0;
            penumbraIpcCalls = 0;
            presentationQueries = 0;
            statusQueueTicks = 0;
            targetInspectionRequests = 0;
            targetInspectionExecutions = 0;
            targetInspectionDeduplicated = 0;
            targetInspectionAcquisitionBudgetOverruns = 0;
            targetInspectionAcquisitionStepCapLimitedUpdates = 0;
            targetInspectionAcquisitionEmergencyCapHits = 0;
            targetInspectionAcquisitionFrameHitchesOver16Milliseconds = 0;
            targetInspectionAcquisitionFrameHitchesOver50Milliseconds = 0;
            targetInspectionAcquisitionFrameHitchesOver100Milliseconds = 0;
        }
    }

    public static void RecordPapResolverLookup(bool cacheHit)
    {
        if (cacheHit)
            System.Threading.Interlocked.Increment(ref papResolverCacheHits);
        else
            System.Threading.Interlocked.Increment(ref papResolverCacheMisses);
    }

    public static void RecordGameFileExists() => System.Threading.Interlocked.Increment(ref gameFileExistsCalls);
    public static void RecordGameFileExistsCacheHit() => System.Threading.Interlocked.Increment(ref gameFileExistsCacheHits);
    public static void RecordGameFileExistsCacheMiss() => System.Threading.Interlocked.Increment(ref gameFileExistsCacheMisses);
    public static void RecordTargetPapRead() => System.Threading.Interlocked.Increment(ref targetPapReads);
    public static void RecordTargetPapInspection() => System.Threading.Interlocked.Increment(ref targetPapInspections);
    public static void RecordSourcePapFileRead() => System.Threading.Interlocked.Increment(ref sourcePapFileReads);
    public static void RecordTargetStructuralPreflight() => System.Threading.Interlocked.Increment(ref targetStructuralPreflights);
    public static void RecordPreviewCompatibilityPreflight() => System.Threading.Interlocked.Increment(ref previewCompatibilityPreflights);
    public static void RecordPenumbraIpcCall() => System.Threading.Interlocked.Increment(ref penumbraIpcCalls);
    public static void RecordPresentationQuery() => System.Threading.Interlocked.Increment(ref presentationQueries);
    public static void RecordStatusQueueTick() => System.Threading.Interlocked.Increment(ref statusQueueTicks);
    public static void RecordTargetInspectionRequested() => System.Threading.Interlocked.Increment(ref targetInspectionRequests);
    public static void RecordTargetInspectionExecuted() => System.Threading.Interlocked.Increment(ref targetInspectionExecutions);
    public static void RecordTargetInspectionDeduplicated() => System.Threading.Interlocked.Increment(ref targetInspectionDeduplicated);

    public static void RecordTargetInspectionAcquisitionUpdate(
        TimeSpan elapsed,
        TimeSpan budget,
        bool limitedByStepCap,
        bool hitEmergencyStepCap)
    {
        RecordTiming("TargetInspectionAcquisitionUpdate", (long)(elapsed.TotalSeconds * Stopwatch.Frequency));
        if (elapsed > budget)
            System.Threading.Interlocked.Increment(ref targetInspectionAcquisitionBudgetOverruns);
        if (limitedByStepCap)
            System.Threading.Interlocked.Increment(ref targetInspectionAcquisitionStepCapLimitedUpdates);
        if (hitEmergencyStepCap)
            System.Threading.Interlocked.Increment(ref targetInspectionAcquisitionEmergencyCapHits);
        if (elapsed > TimeSpan.FromMilliseconds(16.7))
            System.Threading.Interlocked.Increment(ref targetInspectionAcquisitionFrameHitchesOver16Milliseconds);
        if (elapsed > TimeSpan.FromMilliseconds(50))
            System.Threading.Interlocked.Increment(ref targetInspectionAcquisitionFrameHitchesOver50Milliseconds);
        if (elapsed > TimeSpan.FromMilliseconds(100))
            System.Threading.Interlocked.Increment(ref targetInspectionAcquisitionFrameHitchesOver100Milliseconds);
    }

    public static DancyStep3PerformanceSnapshot Snapshot()
    {
        lock (Gate)
        {
            return new DancyStep3PerformanceSnapshot(
                papResolverCacheHits,
                papResolverCacheMisses,
                gameFileExistsCalls,
                gameFileExistsCacheHits,
                gameFileExistsCacheMisses,
                targetPapReads,
                targetPapInspections,
                sourcePapFileReads,
                targetStructuralPreflights,
                previewCompatibilityPreflights,
                penumbraIpcCalls,
                presentationQueries,
                statusQueueTicks,
                targetInspectionRequests,
                targetInspectionExecutions,
                targetInspectionDeduplicated,
                targetInspectionAcquisitionBudgetOverruns,
                targetInspectionAcquisitionStepCapLimitedUpdates,
                targetInspectionAcquisitionEmergencyCapHits,
                targetInspectionAcquisitionFrameHitchesOver16Milliseconds,
                targetInspectionAcquisitionFrameHitchesOver50Milliseconds,
                targetInspectionAcquisitionFrameHitchesOver100Milliseconds,
                Timings.ToDictionary(
                    pair => pair.Key,
                    pair => new DancyStep3TimingSnapshot(
                        pair.Value.Calls,
                        ToMilliseconds(pair.Value.TotalTicks),
                        ToMilliseconds(pair.Value.MaxTicks),
                        MedianMilliseconds(pair.Value.Samples)),
                    StringComparer.Ordinal));
        }
    }

    private static void RecordTiming(string stage, long elapsedTicks)
    {
        lock (Gate)
        {
            if (!Timings.TryGetValue(stage, out var timing))
            {
                timing = new TimingCounter();
                Timings.Add(stage, timing);
            }

            timing.Calls++;
            timing.TotalTicks += elapsedTicks;
            timing.MaxTicks = Math.Max(timing.MaxTicks, elapsedTicks);
            if (timing.Samples.Count < TimingSampleLimit)
                timing.Samples.Add(elapsedTicks);
        }
    }

    public static void RecordElapsed(string stage, long startedAt)
        => RecordTiming(stage, Stopwatch.GetTimestamp() - startedAt);

    private static double ToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static double MedianMilliseconds(IReadOnlyList<long> samples)
    {
        if (samples.Count == 0)
            return 0;

        var ordered = samples.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        var ticks = ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
        return ticks * 1000d / Stopwatch.Frequency;
    }

    internal readonly struct TimingScope : IDisposable
    {
        private readonly string stage;
        private readonly long startedAt;

        public TimingScope(string stage, long startedAt)
        {
            this.stage = stage;
            this.startedAt = startedAt;
        }

        public void Dispose() => RecordTiming(stage, Stopwatch.GetTimestamp() - startedAt);
    }
}

internal sealed record DancyStep3TimingSnapshot(long Calls, double TotalMilliseconds, double MaxMilliseconds, double MedianMilliseconds)
{
    public double AverageMilliseconds => Calls == 0 ? 0 : TotalMilliseconds / Calls;
}

internal sealed record DancyStep3PerformanceSnapshot(
    long PapResolverCacheHits,
    long PapResolverCacheMisses,
    long GameFileExistsCalls,
    long GameFileExistsCacheHits,
    long GameFileExistsCacheMisses,
    long TargetPapReads,
    long TargetPapInspections,
    long SourcePapFileReads,
    long TargetStructuralPreflights,
    long PreviewCompatibilityPreflights,
    long PenumbraIpcCalls,
    long PresentationQueries,
    long StatusQueueTicks,
    long TargetInspectionRequests,
    long TargetInspectionExecutions,
    long TargetInspectionDeduplicated,
    long TargetInspectionAcquisitionBudgetOverruns,
    long TargetInspectionAcquisitionStepCapLimitedUpdates,
    long TargetInspectionAcquisitionEmergencyCapHits,
    long TargetInspectionAcquisitionFrameHitchesOver16Milliseconds,
    long TargetInspectionAcquisitionFrameHitchesOver50Milliseconds,
    long TargetInspectionAcquisitionFrameHitchesOver100Milliseconds,
    IReadOnlyDictionary<string, DancyStep3TimingSnapshot> Timings)
{
    public string Describe() =>
        $"resolver hit/miss {PapResolverCacheHits}/{PapResolverCacheMisses}; " +
        $"FileExists calls/cache hit/miss {GameFileExistsCalls}/{GameFileExistsCacheHits}/{GameFileExistsCacheMisses}; target PAP reads/parses {TargetPapReads}/{TargetPapInspections}; source file reads {SourcePapFileReads}; " +
        $"structural/preview preflights {TargetStructuralPreflights}/{PreviewCompatibilityPreflights}; Penumbra IPC {PenumbraIpcCalls}; " +
        $"presentation queries {PresentationQueries}; target requests/executed/deduplicated {TargetInspectionRequests}/{TargetInspectionExecutions}/{TargetInspectionDeduplicated}.";
}
#endif
