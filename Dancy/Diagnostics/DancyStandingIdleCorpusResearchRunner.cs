#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dancy.Pap;
using ECommons.DalamudServices;
using Newtonsoft.Json;

namespace Dancy.Diagnostics;

/// <summary>
/// Reads the configured Penumbra corpus and current game data to document PAP
/// structure. It never calls a PAP writer, touches Penumbra metadata, or emits
/// a redirect. Its only write is the JSON summary under Dancy/Research.
/// </summary>
internal sealed class DancyStandingIdleCorpusResearchRunner
{
    private const string CorpusRoot = @"C:\Users\Nick\Documents\FFXIV Mods";
    private const int InventorySampleCount = 64;

    public DancySelfTestResult Run()
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();
        PenumbraCorpusReadOnlyInventory? before = null;
        PenumbraAnimationCorpusIndex? index = null;
        string? outputPath = null;

        RunCase(cases, "Corpus root and source inventory", "corpus", "The configured Penumbra library exists and a read-only pre-index inventory is captured.", () =>
        {
            before = PenumbraAnimationCorpusIndexer.CaptureReadOnlyInventory(CorpusRoot, InventorySampleCount);
            return DescribeInventory(before);
        });

        RunCase(cases, "Option-aware PAP index", "corpus", "Directory and archive metadata are read without extracting archives or retaining creator assets.", () =>
        {
            index = PenumbraAnimationCorpusIndexer.Create(
                CorpusRoot,
                includePayloadHashesByGamePath: IsResidentIdleGamePath);
            var papMappings = index.Mods.Sum(mod => mod.PapMappings.Count);
            var structuralSummaries = index.Mods.SelectMany(mod => mod.PapMappings).Count(mapping => mapping.Modded is not null);
            var payloadHashes = index.Mods.SelectMany(mod => mod.PapMappings).Count(mapping => mapping.Modded?.PayloadHashesCaptured == true);
            var multiSection = index.Mods.SelectMany(mod => mod.PapMappings).Count(mapping => mapping.Modded?.Sections.Count > 1);
            var residentIdle = index.Mods.SelectMany(mod => mod.PapMappings).Count(mapping => IsResidentIdleGamePath(mapping.GamePath));
            var standingIdle = index.Mods.SelectMany(mod => mod.PapMappings).Count(mapping => IsStandingIdleGamePath(mapping.GamePath));
            return $"mods={index.Mods.Count}; papMappings={papMappings}; structuralSummaries={structuralSummaries}; payloadHashes={payloadHashes}; multiSection={multiSection}; residentIdleMappings={residentIdle}; standingIdleMappings={standingIdle}; issues={index.Issues.Count}";
        });

        RunCase(cases, "Vanilla versus modded PAP comparison", "vanilla", "Every indexed game PAP is compared to a current-game read when the game file resolves; per-section Havok remains unknown when the single Havok payload differs.", () =>
        {
            if (index is null)
                throw new InvalidOperationException("The corpus index was not available.");
            var gamePaths = index.Mods.SelectMany(mod => mod.PapMappings)
                .Where(mapping => mapping.Modded is not null
                    && (IsResidentIdleGamePath(mapping.GamePath)
                        || mapping.Modded.Sections.Count > 1))
                .Select(mapping => mapping.GamePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var vanilla = OnFramework(() => ReadVanillaSummaries(gamePaths));
            index = PenumbraAnimationCorpusIndexer.AttachVanillaSummaries(index, vanilla);
            var compared = index.Mods.SelectMany(mod => mod.PapMappings).Count(mapping => mapping.Comparison is not null);
            var changedWholeHavok = index.Mods.SelectMany(mod => mod.PapMappings).Count(mapping => mapping.Comparison is { HavokPayloadMatches: false });
            return $"relevant resident/idle or multi-section paths requested={gamePaths.Count}; resolved={vanilla.Count}; compared={compared}; changedWholeHavok={changedWholeHavok}; section-local Havok boundaries remain unclaimed";
        });

        RunCase(cases, "Standing Idle evidence summary", "standing-idle", "bt_common/resident/idle mappings remain structural evidence only and do not classify a section as independently replaceable.", () =>
        {
            if (index is null)
                throw new InvalidOperationException("The corpus index was not available.");
            var standing = index.Mods
                .SelectMany(mod => mod.PapMappings.Select(mapping => (mod, mapping)))
                .Where(pair => IsStandingIdleGamePath(pair.mapping.GamePath))
                .ToList();
            if (standing.Count == 0)
                throw new InvalidOperationException("The corpus contains no bt_common/resident/idle PAP mappings.");
            var multi = standing.Count(pair => pair.mapping.Modded?.Sections.Count > 1);
            return $"mappings={standing.Count}; multiSection={multi}; topologyDecision=Unknown; no target was enabled and no planner/write path was invoked";
        });

        RunCase(cases, "Research index output", "artifact", "Only a structured metadata/hash report is written beneath Dancy/Research.", () =>
        {
            if (index is null)
                throw new InvalidOperationException("The corpus index was not available.");
            outputPath = WriteResearchIndex(index);
            return outputPath;
        }, outputPath is null ? Array.Empty<string>() : new[] { outputPath });

        RunCase(cases, "Corpus source unchanged", "integrity", "Post-index inventory and sampled source hashes match the pre-index snapshot.", () =>
        {
            if (before is null)
                throw new InvalidOperationException("The pre-index inventory was not available.");
            var after = PenumbraAnimationCorpusIndexer.CaptureReadOnlyInventory(CorpusRoot, InventorySampleCount);
            if (!EquivalentInventory(before, after))
                throw new InvalidOperationException($"Corpus inventory changed during read-only indexing. Before: {DescribeInventory(before)}. After: {DescribeInventory(after)}.");
            return DescribeInventory(after);
        });

        return new DancySelfTestResult
        {
            Schema = "dancy.standing-idle-corpus-research.v1",
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
            RetainedArtifacts = outputPath is null ? Array.Empty<string>() : new[] { outputPath },
        };
    }

    private static Dictionary<string, PapCorpusSummary> ReadVanillaSummaries(IEnumerable<string> paths)
    {
        var summaries = new Dictionary<string, PapCorpusSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                var file = Plugin.DataManager.GetFile(path);
                if (file is null)
                    continue;
                var stream = file.Reader.BaseStream;
                if (stream.CanSeek)
                    stream.Position = 0;
                if (PapCorpusSummary.TryCreate(stream, includePayloadHashes: true, out var summary, out _) && summary is not null)
                    summaries[path] = summary;
            }
            catch
            {
                // Missing or malformed game assets are represented by the absent comparison.
            }
        }
        return summaries;
    }

    private static string WriteResearchIndex(PenumbraAnimationCorpusIndex index)
    {
        var researchDirectory = FindResearchDirectory();
        Directory.CreateDirectory(researchDirectory);
        var outputPath = Path.Combine(researchDirectory, "PenumbraAnimationCorpusIndex.json");
        var tempPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tempPath, JsonConvert.SerializeObject(index, Formatting.Indented), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, outputPath, overwrite: true);
        return outputPath;
    }

    private static string FindResearchDirectory()
    {
        var assemblyLocation = Plugin.PluginInterface.AssemblyLocation.FullName;
        DirectoryInfo? current = File.Exists(assemblyLocation)
            ? new FileInfo(assemblyLocation).Directory
            : string.IsNullOrWhiteSpace(AppContext.BaseDirectory) ? null : new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; current is not null && level < 8; level++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "Research");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Dancy Research directory could not be located from the loaded Debug artifact.");
    }

    private static string DescribeInventory(PenumbraCorpusReadOnlyInventory inventory)
        => $"files={inventory.FileCount}; bytes={inventory.TotalBytes}; sampledHashes={inventory.Samples.Count}";

    private static bool IsResidentIdleGamePath(string gamePath)
        => gamePath.EndsWith("/resident/idle.pap", StringComparison.OrdinalIgnoreCase);

    private static bool IsStandingIdleGamePath(string gamePath)
        => gamePath.EndsWith("/bt_common/resident/idle.pap", StringComparison.OrdinalIgnoreCase);

    private static bool EquivalentInventory(PenumbraCorpusReadOnlyInventory left, PenumbraCorpusReadOnlyInventory right)
        => left.FileCount == right.FileCount
            && left.TotalBytes == right.TotalBytes
            && left.Samples.SequenceEqual(right.Samples);

    private static T OnFramework<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Svc.Framework.RunOnFrameworkThread(() =>
        {
            try { completion.TrySetResult(work()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private static void RunCase(List<DancySelfTestCase> cases, string testName, string stage, string expected, Func<string> action, IReadOnlyList<string>? artifacts = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cases.Add(new DancySelfTestCase
            {
                TestName = testName,
                Stage = stage,
                Expected = expected,
                Actual = action(),
                Status = DancySelfTestStatus.Passed,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                Artifacts = artifacts ?? Array.Empty<string>(),
            });
        }
        catch (Exception exception)
        {
            cases.Add(new DancySelfTestCase
            {
                TestName = testName,
                Stage = stage,
                Expected = expected,
                Actual = exception.Message,
                FailureReason = exception.ToString(),
                Status = DancySelfTestStatus.Failed,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                Artifacts = artifacts ?? Array.Empty<string>(),
            });
        }
    }
}
#endif
