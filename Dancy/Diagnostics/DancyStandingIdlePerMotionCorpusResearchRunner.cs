#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Dancy.Domain;
using ECommons.DalamudServices;

namespace Dancy.Diagnostics;

/// <summary>
/// Applies VFXEditor's read-only selected-motion sampler to canonical
/// multi-section Standing Idle mappings. No mod source is edited, retained, or
/// repacked: archive payloads exist only under a disposable temp workspace.
/// </summary>
internal sealed class DancyStandingIdlePerMotionCorpusResearchRunner
{
    private const string CorpusRoot = @"C:\Users\Nick\Documents\FFXIV Mods";
    private const int InventorySampleCount = 64;
    private const string CapabilitiesEndpoint = "VFXEditor.PapAnimationRebuild.Capabilities";
    private const string FingerprintEndpoint = "VFXEditor.PapAnimationRebuild.Fingerprint";
    private const string FingerprintScheme = "vfxeditor-pap-motion-sample-sha256-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    public DancySelfTestResult Run()
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();
        var workspace = Path.Combine(Path.GetTempPath(), "Dancy", "StandingIdlePerMotionCorpus", Guid.NewGuid().ToString("N"));
        var before = PenumbraAnimationCorpusIndexer.CaptureReadOnlyInventory(CorpusRoot, InventorySampleCount);
        string? reportPath = null;
        try
        {
            Directory.CreateDirectory(workspace);
            RunCase(cases, "VFXEditor per-motion capability", "capability", "The active VFXEditor reports the read-only selected-motion fingerprint API.", VerifyCapabilities);
            PenumbraAnimationCorpusIndex? index = null;
            RunCase(cases, "Current canonical Standing Idle corpus", "corpus", "Installed directory and PMP metadata are re-indexed without retaining assets.", () =>
            {
                index = PenumbraAnimationCorpusIndexer.Create(CorpusRoot);
                var count = Candidates(index).Count();
                if (count == 0)
                    throw new InvalidOperationException("The configured corpus contained no canonical two-section Standing Idle mappings.");
                return $"canonicalTwoSectionMappings={count}; indexedMods={index.Mods.Count}; issues={index.Issues.Count}";
            });
            RunCase(cases, "Per-motion differential matrix", "fingerprint", "Every readable canonical two-section mapping is compared against the same-rig vanilla motion 0 and motion 1 fingerprints.", () =>
            {
                if (index is null)
                    throw new InvalidOperationException("The current corpus index was not available.");
                var report = BuildReport(index, workspace);
                reportPath = WriteReport(report);
                var natural = report.Records.Count(record => record.Motion0 == "same" && record.Motion1 == "changed" && record.Tmb0 == "same" && record.Tmb1 == "changed");
                return $"records={report.Records.Count}; successful={report.Records.Count(record => record.Status == "ok")}; unknown={report.Records.Count(record => record.Status != "ok")}; naturalExperimentM0SameM1ChangedTmb0SameTmb1Changed={natural}; report={reportPath}";
            });
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception exception) { Svc.Log.Warning(exception, "[Dancy] Could not remove the temporary per-motion corpus workspace."); }
            }
        }

        RunCase(cases, "Corpus source unchanged", "integrity", "Pre- and post-research file counts, bytes, and deterministic sampled hashes agree.", () =>
        {
            var after = PenumbraAnimationCorpusIndexer.CaptureReadOnlyInventory(CorpusRoot, InventorySampleCount);
            if (!EquivalentInventory(before, after))
                throw new InvalidOperationException($"Corpus inventory changed. Before={DescribeInventory(before)}; after={DescribeInventory(after)}.");
            return DescribeInventory(after);
        });

        return new DancySelfTestResult
        {
            Schema = "dancy.standing-idle-per-motion-corpus-research.v1",
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
            RetainedArtifacts = reportPath is null ? Array.Empty<string>() : new[] { reportPath },
        };
    }

    private static string VerifyCapabilities()
    {
        var raw = Plugin.PluginInterface.GetIpcSubscriber<string>(CapabilitiesEndpoint).InvokeFunc();
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("supportsReadOnlyMotionFingerprinting", out var supported) || !supported.GetBoolean())
            throw new InvalidOperationException("The active VFXEditor does not advertise read-only motion fingerprinting.");
        return raw;
    }

    private static PerMotionCorpusReport BuildReport(PenumbraAnimationCorpusIndex index, string workspace)
    {
        var vanilla = new Dictionary<string, VanillaPair>(StringComparer.OrdinalIgnoreCase);
        var skeletons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var records = new List<PerMotionCorpusRecord>();
        foreach (var (mod, mapping) in Candidates(index))
        {
            var character = GamePathIdentity.Parse(mapping.GamePath).Character;
            if (!character.IsKnown)
            {
                records.Add(FailedRecord(mod, mapping, "unknown-rig"));
                continue;
            }

            try
            {
                if (!skeletons.TryGetValue(character.Code, out var skeletonPath))
                    skeletons[character.Code] = skeletonPath = CopyGameAsset(SkeletonGamePath(character.Code), workspace, $"skl_{character.Code}b0001.sklb");
                if (!vanilla.TryGetValue(mapping.GamePath, out var vanillaPair))
                {
                    var vanillaPath = CopyGameAsset(mapping.GamePath, workspace, $"vanilla-{character.Code}-{vanilla.Count}.pap");
                    if (!PapCorpusSummary.TryCreate(File.ReadAllBytes(vanillaPath), out var vanillaSummary, out var summaryError) || vanillaSummary is null)
                        throw new InvalidDataException($"Dancy could not inspect the copied current-game Standing Idle PAP: {summaryError}");
                    vanilla[mapping.GamePath] = vanillaPair = new VanillaPair(
                        new MotionPair(Fingerprint(vanillaPath, skeletonPath, 0), Fingerprint(vanillaPath, skeletonPath, 1)),
                        vanillaSummary);
                }

                var localModded = MaterializeAsset(mod.Source, mapping.ModPath, workspace, records.Count);
                var moddedPair = new MotionPair(Fingerprint(localModded, skeletonPath, 0), Fingerprint(localModded, skeletonPath, 1));
                records.Add(new PerMotionCorpusRecord(
                    "ok", mod.Name, mod.Author, mod.Source.Kind, mod.Source.RelativePath, mapping.Scope.GroupName, mapping.Scope.OptionName,
                    mapping.GamePath, character.Code,
                    Compare(moddedPair.Motion0, vanillaPair.Motions.Motion0), Compare(moddedPair.Motion1, vanillaPair.Motions.Motion1),
                    CompareTmb(mapping, vanillaPair.Summary, 0), CompareTmb(mapping, vanillaPair.Summary, 1),
                    vanillaPair.Motions.Motion0.FingerprintSha256, vanillaPair.Motions.Motion1.FingerprintSha256,
                    moddedPair.Motion0.FingerprintSha256, moddedPair.Motion1.FingerprintSha256,
                    moddedPair.Motion0.SourceSha256, string.Empty));
            }
            catch (Exception exception)
            {
                records.Add(FailedRecord(mod, mapping, exception.Message));
            }
        }

        return new PerMotionCorpusReport(
            SchemaVersion: 1,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            FingerprintScheme: FingerprintScheme,
            SourceRoot: Path.GetFileName(CorpusRoot),
            Records: records,
            Notes: "Motion equality compares selected-binding semantic sample fingerprints. It establishes no writer or transplant behavior.");
    }

    private static IEnumerable<(PenumbraCorpusMod Mod, PenumbraCorpusPapMapping Mapping)> Candidates(PenumbraAnimationCorpusIndex index)
        => index.Mods.SelectMany(mod => mod.PapMappings.Select(mapping => (mod, mapping)))
            .Where(pair => pair.mapping.GamePath.EndsWith("/bt_common/resident/idle.pap", StringComparison.OrdinalIgnoreCase)
                           && pair.mapping.Modded?.Sections.Count == 2
                           && pair.mapping.Modded.Sections[0].HavokMotionIndex == 0
                           && pair.mapping.Modded.Sections[1].HavokMotionIndex == 1)
            .OrderBy(pair => pair.mod.Author, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.mod.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.mapping.GamePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.mapping.ModPath, StringComparer.OrdinalIgnoreCase);

    private static PerMotionCorpusRecord FailedRecord(PenumbraCorpusMod mod, PenumbraCorpusPapMapping mapping, string error)
        => new("unknown", mod.Name, mod.Author, mod.Source.Kind, mod.Source.RelativePath, mapping.Scope.GroupName, mapping.Scope.OptionName,
            mapping.GamePath, GamePathIdentity.Parse(mapping.GamePath).Character.Code, "unknown", "unknown", CompareTmb(mapping, 0), CompareTmb(mapping, 1), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, error);

    private static string Compare(VfxFingerprintResponse source, VfxFingerprintResponse vanilla)
        => string.Equals(source.FingerprintSha256, vanilla.FingerprintSha256, StringComparison.OrdinalIgnoreCase) ? "same" : "changed";

    private static string CompareTmb(PenumbraCorpusPapMapping mapping, PapCorpusSummary vanilla, int section)
    {
        var moddedTimeline = mapping.Modded?.TimelineSections.FirstOrDefault(candidate => candidate.Index == section);
        var vanillaTimeline = vanilla.TimelineSections.FirstOrDefault(candidate => candidate.Index == section);
        return moddedTimeline is null || vanillaTimeline is null || !mapping.Modded!.PayloadHashesCaptured || !vanilla.PayloadHashesCaptured
            ? "unknown"
            : string.Equals(moddedTimeline.ContentSha256, vanillaTimeline.ContentSha256, StringComparison.OrdinalIgnoreCase) ? "same" : "changed";
    }

    private static string CompareTmb(PenumbraCorpusPapMapping mapping, int section)
        => "unknown";

    private static string MaterializeAsset(PenumbraCorpusSource source, string modPath, string workspace, int ordinal)
    {
        var output = Path.Combine(workspace, $"modded-{ordinal:D4}.pap");
        if (source.Kind.Equals("Directory", StringComparison.OrdinalIgnoreCase))
        {
            var folder = ResolveInside(CorpusRoot, source.RelativePath);
            var sourcePath = ResolveInside(folder, modPath);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"The indexed directory PAP is no longer present: {sourcePath}");
            File.Copy(sourcePath, output, overwrite: true);
            return output;
        }

        if (!source.Kind.Equals("Archive", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported corpus source kind {source.Kind}.");
        var archivePath = ResolveInside(CorpusRoot, source.RelativePath);
        using var archive = ZipFile.OpenRead(archivePath);
        var normalized = Normalize(modPath);
        var entry = archive.Entries.FirstOrDefault(candidate => Normalize(candidate.FullName).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"The indexed PMP does not contain {modPath}.");
        using var input = entry.Open();
        using var outputStream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(outputStream);
        return output;
    }

    private static VfxFingerprintResponse Fingerprint(string sourcePath, string skeletonPath, int motionIndex)
    {
        var request = JsonSerializer.Serialize(new VfxFingerprintRequest { SchemaVersion = 1, SourcePath = sourcePath, TargetSkeletonPath = skeletonPath, MotionIndex = motionIndex }, JsonOptions);
        var raw = Plugin.PluginInterface.GetIpcSubscriber<string, string>(FingerprintEndpoint).InvokeFunc(request);
        var response = JsonSerializer.Deserialize<VfxFingerprintResponse>(raw, JsonOptions)
            ?? throw new InvalidOperationException("VFXEditor returned no fingerprint response.");
        if (!response.Success)
            throw new InvalidOperationException($"VFXEditor fingerprint failed [{response.ErrorCode}]: {response.Error}");
        if (!string.Equals(response.FingerprintScheme, FingerprintScheme, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(response.FingerprintSha256))
            throw new InvalidOperationException("VFXEditor returned an incomplete motion fingerprint response.");
        return response;
    }

    private static string CopyGameAsset(string gamePath, string workspace, string outputName)
        => OnFramework(() =>
        {
            var file = Plugin.DataManager.GetFile(gamePath) ?? throw new FileNotFoundException($"Game asset {gamePath} was not found.");
            var outputPath = Path.Combine(workspace, outputName);
            if (file.Reader.BaseStream.CanSeek)
                file.Reader.BaseStream.Position = 0;
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            file.Reader.BaseStream.CopyTo(output);
            return outputPath;
        });

    private static string SkeletonGamePath(string characterCode)
        => $"chara/human/{characterCode}/skeleton/base/b0001/skl_{characterCode}b0001.sklb";

    private static string ResolveInside(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Indexed source path escapes its expected root.");
        return candidate;
    }

    private static string WriteReport(PerMotionCorpusReport report)
    {
        var directory = FindResearchDirectory();
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "StandingIdlePerMotionCorpusResearch.json");
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, JsonOptions));
        File.Move(temporary, output, overwrite: true);
        return output;
    }

    private static string FindResearchDirectory()
    {
        var current = new FileInfo(Plugin.PluginInterface.AssemblyLocation.FullName).Directory;
        for (var level = 0; current is not null && level < 8; level++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "Research");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Dancy Research directory could not be located from the loaded Debug artifact.");
    }

    private static bool EquivalentInventory(PenumbraCorpusReadOnlyInventory left, PenumbraCorpusReadOnlyInventory right)
        => left.FileCount == right.FileCount && left.TotalBytes == right.TotalBytes && left.Samples.SequenceEqual(right.Samples);

    private static string DescribeInventory(PenumbraCorpusReadOnlyInventory inventory)
        => $"files={inventory.FileCount}; bytes={inventory.TotalBytes}; sampledHashes={inventory.Samples.Count}";

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static T OnFramework<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Svc.Framework.RunOnFrameworkThread(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private static void RunCase(List<DancySelfTestCase> cases, string testName, string stage, string expected, Func<string> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try { cases.Add(new DancySelfTestCase { TestName = testName, Stage = stage, Expected = expected, Actual = action(), Status = DancySelfTestStatus.Passed, DurationMilliseconds = stopwatch.ElapsedMilliseconds }); }
        catch (Exception exception) { cases.Add(new DancySelfTestCase { TestName = testName, Stage = stage, Expected = expected, Actual = exception.Message, FailureReason = exception.ToString(), Status = DancySelfTestStatus.Failed, DurationMilliseconds = stopwatch.ElapsedMilliseconds }); }
    }

    private sealed record MotionPair(VfxFingerprintResponse Motion0, VfxFingerprintResponse Motion1);
    private sealed record VanillaPair(MotionPair Motions, PapCorpusSummary Summary);
    private sealed record PerMotionCorpusReport(int SchemaVersion, DateTimeOffset GeneratedAtUtc, string FingerprintScheme, string SourceRoot, IReadOnlyList<PerMotionCorpusRecord> Records, string Notes);
    private sealed record PerMotionCorpusRecord(string Status, string Mod, string Author, string SourceKind, string Source, string Group, string Option, string GamePath, string Race, string Motion0, string Motion1, string Tmb0, string Tmb1, string VanillaMotion0, string VanillaMotion1, string ModdedMotion0, string ModdedMotion1, string ModdedPapSha256, string Error);
    private sealed class VfxFingerprintRequest { public int SchemaVersion { get; init; } public string SourcePath { get; init; } = string.Empty; public string TargetSkeletonPath { get; init; } = string.Empty; public int MotionIndex { get; init; } }
    private sealed class VfxFingerprintResponse { public bool Success { get; init; } public string SourceSha256 { get; init; } = string.Empty; public string FingerprintScheme { get; init; } = string.Empty; public string FingerprintSha256 { get; init; } = string.Empty; public string? ErrorCode { get; init; } public string? Error { get; init; } }
}
#endif
