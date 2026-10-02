using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
#if DEBUG
using Dancy.Diagnostics;
#endif

namespace Dancy.Pap
{
    public static class PapResolver
    {
        private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> ResolvedPaths = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> GamePathExistence = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] RaceIds =
        {
        "c0101","c0201","c0301","c0401","c0501","c0601",
        "c0701","c0801","c0901","c1001","c1101","c1201",
        "c1301","c1401","c1501","c1601","c1701","c1801"
    };

        private static readonly string[] AnimationLayers = { "a0001", "a0002" };

        private static readonly string[] Subfolders =
        {
        "",              // direkt
        "bt_common/",    // Emotes
        "resident/",
        "nonresident/"
    };

        public static string? SelectBestKey(List<(string Key, byte LoadType)> keys)
        {
            if (keys.Count == 0) return null;

            // 1) Loop bevorzugen
            var loop = keys.FirstOrDefault(k =>
                k.Key.Contains("loop", StringComparison.OrdinalIgnoreCase)).Key;
            if (!string.IsNullOrEmpty(loop))
                return loop;

            // 2) Start, falls vorhanden
            var start = keys.FirstOrDefault(k =>
                k.Key.Contains("start", StringComparison.OrdinalIgnoreCase)).Key;
            if (!string.IsNullOrEmpty(start))
                return start;

            // 3) Fallback: erster Key (z.B. emote/goodbye_st bei /wave)
            return keys[0].Key;
        }

        public static List<string> ResolvePapFiles(string timelineKey)
            => BeginResolution(timelineKey).ResolveAll();

        /// <summary>
        /// Creates a framework-thread-only, incremental resolver. Each call to
        /// <see cref="PapResolution.TryAdvance"/> performs at most one game-data
        /// FileExists probe, so callers can apply a frame budget.
        /// </summary>
        public static PapResolution BeginResolution(string timelineKey)
        {
            if (string.IsNullOrWhiteSpace(timelineKey))
                return PapResolution.Completed(Array.Empty<string>());

            var normalizedKey = timelineKey.Replace('\\', '/');
#if DEBUG
            DancyStep3PerformanceTelemetry.RecordPapResolverLookup(ResolvedPaths.ContainsKey(normalizedKey));
#endif
            return ResolvedPaths.TryGetValue(normalizedKey, out var cached)
                ? PapResolution.Completed(cached)
                : new PapResolution(normalizedKey);
        }

        public static void ClearCache()
        {
            ResolvedPaths.Clear();
            GamePathExistence.Clear();
        }

        public static PapResolverCacheStatistics GetCacheStatistics()
            => new(ResolvedPaths.Count, GamePathExistence.Count);

        public sealed class PapResolution
        {
            private readonly string timelineKey;
            private readonly Queue<string> candidates = new();
            private readonly List<string> results = new();
            private bool primaryCandidatesComplete;
            private bool completed;
            private int fileExistsRequests;
            private int fileExistsCacheHits;
            private int underlyingFileExistsProbes;

            internal PapResolution(string timelineKey)
            {
                this.timelineKey = timelineKey;
                AddPrimaryCandidates();
            }

            private PapResolution(IReadOnlyList<string> resolved)
            {
                timelineKey = string.Empty;
                results.AddRange(resolved);
                completed = true;
            }

            public bool IsCompleted => completed;
            public IReadOnlyList<string> Results => completed ? results : Array.Empty<string>();
            public int FileExistsRequests => fileExistsRequests;
            public int FileExistsCacheHits => fileExistsCacheHits;
            public int UnderlyingFileExistsProbes => underlyingFileExistsProbes;

            internal static PapResolution Completed(IReadOnlyList<string> resolved)
                => new(resolved);

            /// <summary>Must run on Dalamud's framework thread.</summary>
            public bool TryAdvance()
            {
                if (completed)
                    return false;

                if (candidates.Count == 0)
                {
                    if (!primaryCandidatesComplete)
                    {
                        primaryCandidatesComplete = true;
                        AddStandingIdleFallbackCandidates();
                        if (candidates.Count == 0)
                            Complete();
                        return true;
                    }

                    Complete();
                    return true;
                }

                var path = candidates.Dequeue();
                var exists = FileExists(path, out var cacheHit);
                fileExistsRequests++;
                if (cacheHit)
                    fileExistsCacheHits++;
                else
                    underlyingFileExistsProbes++;
                if (exists)
                    results.Add(path);
                return true;
            }

            public List<string> ResolveAll()
            {
#if DEBUG
                using var timing = DancyStep3PerformanceTelemetry.Measure("ResolvePapFiles");
#endif
                while (!completed)
                    TryAdvance();
                return results.ToList();
            }

            private void AddPrimaryCandidates()
            {
                candidates.Enqueue($"chara/animation/{timelineKey}.pap");
                foreach (var race in RaceIds)
                foreach (var layer in AnimationLayers)
                foreach (var sub in Subfolders)
                    candidates.Enqueue($"chara/human/{race}/animation/{layer}/{sub}{timelineKey}.pap");
            }

            private void AddStandingIdleFallbackCandidates()
            {
                if (results.Count != 0 || !timelineKey.StartsWith("normal/", StringComparison.OrdinalIgnoreCase))
                    return;

                var residentFile = timelineKey["normal/".Length..];
                foreach (var race in RaceIds)
                foreach (var layer in AnimationLayers)
                    candidates.Enqueue($"chara/human/{race}/animation/{layer}/bt_common/resident/{residentFile}.pap");
            }

            private void Complete()
            {
                if (completed)
                    return;

                var resolved = results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var cached = ResolvedPaths.GetOrAdd(timelineKey, resolved);
                results.Clear();
                results.AddRange(cached);
                completed = true;
            }
        }

        private static bool FileExists(string path, out bool cacheHit)
        {
            if (GamePathExistence.TryGetValue(path, out var cached))
            {
                cacheHit = true;
#if DEBUG
                DancyStep3PerformanceTelemetry.RecordGameFileExistsCacheHit();
#endif
                return cached;
            }

            cacheHit = false;
#if DEBUG
            DancyStep3PerformanceTelemetry.RecordGameFileExists();
            DancyStep3PerformanceTelemetry.RecordGameFileExistsCacheMiss();
#endif
            var exists = Plugin.DataManager.FileExists(path);
            return GamePathExistence.GetOrAdd(path, exists);
        }
    }

    public sealed record PapResolverCacheStatistics(int ResolvedTimelineCount, int UniqueGamePathCount);


}
