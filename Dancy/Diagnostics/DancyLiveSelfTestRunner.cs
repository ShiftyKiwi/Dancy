#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dancy.Core;
using Dancy.Domain;
using Dancy.Pap;
using Dancy.Penumbra;
using Dancy.Persistence;
using Dancy.Services;
using ECommons.DalamudServices;
using Newtonsoft.Json.Linq;
using Penumbra.Api.IpcSubscribers;

namespace Dancy.Diagnostics;

/// <summary>
/// Creates a marker-guarded mod fixture and executes the normal production pipeline against it.
/// Every mutating Penumbra setting is player-scoped and removed in a finally block.
/// </summary>
internal sealed class DancyLiveSelfTestRunner
{
    private const string FixtureDirectory = "__dancy_debug_selftest";
    private const string FixtureName = "Dancy Integration Fixture";
    private const string FixtureMarker = "Dancy.DebugFixture.v1";
    private const int TemporarySettingKey = -9234;
    private static readonly string[] RequiredTargetRigs = ["c0101", "c0201", "c0901", "c1701"];

    public DancySelfTestResult Run()
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();
        FixtureContext? fixture = null;

        RunCase(cases, "Build provenance", "provenance", "Loaded assembly has a readable SHA-256.", () =>
        {
            var location = Plugin.PluginInterface.AssemblyLocation.FullName;
            if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
                throw new InvalidOperationException("The loaded Dancy assembly location is unavailable.");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(location)));
            return $"{location} [{hash}]";
        });

        RunCase(cases, "Penumbra fixture preparation", "fixture", "A marker-guarded fixture is added through Penumbra IPC.", () =>
        {
            fixture = PrepareFixture();
            return fixture.ModFolder;
        });

        if (fixture is not null)
        {
            RunCase(cases, "Fixture scan", "scan", "Dancy discovers the fixture source option.", () =>
            {
                var scanned = EmoteOverrideScanner.ScanMod(fixture.ModFolder);
                if (!scanned.Any(option => option.GroupName == "Dancy fixture source" && option.OptionName == "c1501 source"))
                    throw new InvalidOperationException("Dancy did not discover the fixture source option.");
                return "Fixture source option discovered.";
            }, fixture.ModFolder);

            RunCase(cases, "Historical c1501 fallback", "planning", "FallbackAllTargetVariants maps every non-c1501 target variant.", () =>
            {
                var plan = CreatePlan(fixture, fixture.TargetPaths, fixture.TargetTimelineKey);
                fixture.Plan = plan;
                if (!plan.IsValid)
                    throw new InvalidOperationException(string.Join("; ", plan.Errors));
                var match = plan.PapCopies.SelectMany(copy => copy.MatchResults).Single();
                if (match.Strategy != TargetMatchStrategy.FallbackAllTargetVariants)
                    throw new InvalidOperationException($"Expected FallbackAllTargetVariants, got {match.Strategy}.");
                if (!fixture.TargetPaths.All(path => plan.PlannedMappings.ContainsKey(path)))
                    throw new InvalidOperationException("The planner did not include every fixture target path.");
                return $"{fixture.TargetPaths.Count} variants planned with {match.Strategy}.";
            }, fixture.ModFolder);

            RunCase(cases, "Atomic metadata rollback", "metadata", "Injected writer failure leaves metadata unchanged and removes temporary JSON.", () =>
            {
                var plan = GetPlan(fixture);
                var before = JToken.Parse(File.ReadAllText(fixture.MetaPath));
                try
                {
                    PenumbraGroupWriter.CreateOrUpdateDancyGroup(fixture.ModFolder, plan, plan.PlannedMappings, new AtomicJsonWriteOptions
                    {
                        Checkpoint = checkpoint =>
                        {
                            if (checkpoint == AtomicJsonWriteCheckpoint.BeforeReplacement)
                                throw new IOException("Dancy self-test injected metadata replacement failure.");
                        },
                    });
                    throw new InvalidOperationException("The injected writer failure was not observed.");
                }
                catch (IOException)
                {
                    // Expected: the real writer aborted before replacing the original JSON.
                }

                var after = JToken.Parse(File.ReadAllText(fixture.MetaPath));
                if (!JToken.DeepEquals(before, after))
                    throw new InvalidOperationException("Metadata changed despite the injected replacement failure.");
                if (Directory.GetFiles(fixture.ModFolder, ".meta.json.*.tmp").Length != 0)
                    throw new InvalidOperationException("The injected writer failure left temporary JSON files behind.");
                return "Original metadata remained valid.";
            }, fixture.MetaPath);

            RunCase(cases, "Production create", "override", "OverrideService creates PAPs, upserts metadata, and reloads the fixture mod.", () =>
            {
                var plan = GetPlan(fixture);
                var operation = new OverrideService().CreateOrUpdate(fixture.ModFolder, FixtureDirectory, FixtureName, plan);
                fixture.First = operation;
                if (!operation.Reload.Succeeded)
                    throw new InvalidOperationException($"Penumbra reload returned {operation.Reload.Actual}.");
                return $"{operation.Execution.GeneratedFiles.Count} generated PAP(s); reload {operation.Reload.Actual}.";
            }, fixture.ModFolder);

            if (fixture.First is not null)
            {
                RunCase(cases, "Generated PAP round-trip", "pap", "Every generated PAP reopens with the strict normal inspector.", () =>
                {
                    var expectedEvents = OnFramework(() => fixture.TargetPaths
                        .Select(PapEditor.ReadTargetEventIdentifier)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase));
                    if (fixture.First.Execution.PapResults.Count != expectedEvents.Count)
                        throw new InvalidOperationException($"Expected {expectedEvents.Count} PAP event group(s), generated {fixture.First.Execution.PapResults.Count}.");

                    for (var index = 0; index < fixture.First.Execution.PapResults.Count; index++)
                    {
                        var patchResult = fixture.First.Execution.PapResults[index];
                        var relativePath = fixture.First.Execution.GeneratedFiles[index];
                        if (!PathSafety.TryResolveInsideRoot(fixture.ModFolder, relativePath, out var fullPath))
                            throw new InvalidOperationException($"Generated PAP is outside the fixture: {relativePath}");
                        var inspection = PapFileInspector.InspectFile(fullPath);
                        if (inspection.AnimationCount <= 0 || string.IsNullOrWhiteSpace(inspection.AnimationNames[0]))
                            throw new InvalidOperationException($"Generated PAP failed round-trip inspection: {relativePath}");
                        if (!string.Equals(inspection.AnimationNames[0], patchResult.EventIdentifier, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"Generated PAP event identifier mismatch: expected {patchResult.EventIdentifier}, found {inspection.AnimationNames[0]}.");
                    }

                    var generatedEvents = fixture.First.Execution.PapResults.Select(result => result.EventIdentifier).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!expectedEvents.SetEquals(generatedEvents))
                        throw new InvalidOperationException("Dancy did not emit the required PAP event variants.");
                    return $"{fixture.First.Execution.PapResults.Count} PAP event group(s) inspected.";
                }, fixture.First.Execution.GeneratedFiles.Select(path => Path.Combine(fixture.ModFolder, path)).ToArray());

                RunCase(cases, "Metadata upsert", "metadata", "One Dancy option contains exactly the generated mappings.", () =>
                {
                    var first = GetFirst(fixture);
                    var option = GetDancyOption(fixture.MetaPath, GetPlan(fixture).OverrideId);
                    var files = option["Files"] as JObject ?? throw new InvalidOperationException("Dancy option has no Files object.");
                    if (files.Count != first.Execution.FinalMappings.Count)
                        throw new InvalidOperationException($"Expected {first.Execution.FinalMappings.Count} mappings, found {files.Count}.");
                    return $"{files.Count} mappings in one stable option.";
                }, fixture.MetaPath);

                RunCase(cases, "Penumbra runtime redirections", "runtime", "Player resolve IPC returns each generated PAP and leaves the source path outside Dancy output.", () => VerifyRuntimeRedirections(fixture), fixture.ModFolder);

                RunCase(cases, "Idempotent create", "idempotency", "A second identical production call leaves one option and stable generated paths.", () =>
                {
                    var plan = GetPlan(fixture);
                    fixture.Second = new OverrideService().CreateOrUpdate(fixture.ModFolder, FixtureDirectory, FixtureName, plan);
                    var option = GetDancyOption(fixture.MetaPath, plan.OverrideId);
                    var optionCount = GetDancyOptions(fixture.MetaPath).Count;
                    if (optionCount != 1)
                        throw new InvalidOperationException($"Expected one Dancy option, found {optionCount}.");
                    if (!GetFirst(fixture).Execution.GeneratedFiles.OrderBy(path => path).SequenceEqual(fixture.Second.Execution.GeneratedFiles.OrderBy(path => path), StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Identical CreateOrUpdate generated a different PAP set.");
                    return $"One option, {fixture.Second.Execution.GeneratedFiles.Count} PAP(s).";
                }, fixture.MetaPath);

                RunCase(cases, "Serialized rapid repeat", "concurrency", "Two concurrent production calls complete with one final Dancy option.", () =>
                {
                    var service = new OverrideService();
                    var plan = GetPlan(fixture);
                    Task.WaitAll(
                        Task.Run(() => service.CreateOrUpdate(fixture.ModFolder, FixtureDirectory, FixtureName, plan)),
                        Task.Run(() => service.CreateOrUpdate(fixture.ModFolder, FixtureDirectory, FixtureName, plan)));
                    if (GetDancyOptions(fixture.MetaPath).Count != 1)
                        throw new InvalidOperationException("Rapid repeat created duplicate Dancy options.");
                    return "Concurrent calls serialized to one option.";
                }, fixture.MetaPath);

                RunCase(cases, "Update existing override", "update", "Changing target mappings preserves the stable override identity and removes prior mappings.", () =>
                {
                    var plan = GetPlan(fixture);
                    var updateTargets = fixture.TargetPaths.Take(1).ToList();
                    var updatePlan = CreatePlan(fixture, updateTargets, fixture.TargetTimelineKey + "-update");
                    fixture.UpdatePlan = updatePlan;
                    if (updatePlan.OverrideId != plan.OverrideId)
                        throw new InvalidOperationException("Changing target mappings changed Dancy's logical override identity.");
                    fixture.Updated = new OverrideService().CreateOrUpdate(fixture.ModFolder, FixtureDirectory, FixtureName, updatePlan);
                    var option = GetDancyOption(fixture.MetaPath, plan.OverrideId);
                    var files = option["Files"] as JObject ?? throw new InvalidOperationException("Updated option has no Files object.");
                    if (files.Count != fixture.Updated.Execution.FinalMappings.Count || files.Properties().Any(property => !fixture.Updated.Execution.FinalMappings.ContainsKey(property.Name)))
                        throw new InvalidOperationException("Updated metadata retained obsolete target mappings.");
                    return $"Stable ID {plan.OverrideId}; {files.Count} updated mapping(s).";
                }, fixture.MetaPath);

                RunCase(cases, "Fixture source integrity", "integrity", "The source PAP bytes remain unchanged after normal create and update operations.", () => VerifyFixtureSourceIntegrity(fixture), fixture.ModFolder);
            }
        }

        var allPassedBeforeCleanup = cases.All(test => test.Status != DancySelfTestStatus.Failed);
        if (fixture is not null && allPassedBeforeCleanup)
        {
            RunCase(cases, "Fixture cleanup", "cleanup", "Penumbra fixture and temporary files are removed without touching user mods.", () => CleanupFixture(fixture), fixture.ModFolder);
        }
        else if (fixture is not null)
        {
            cases.Add(new DancySelfTestCase
            {
                TestName = "Fixture cleanup",
                Status = DancySelfTestStatus.Skipped,
                Stage = "cleanup",
                Expected = "Cleanup after a passing run.",
                Actual = "Fixture retained because an earlier test failed.",
                Artifacts = new[] { fixture.ModFolder },
            });
        }

        return new DancySelfTestResult
        {
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
            RetainedArtifacts = fixture is not null && cases.Any(test => test.Status == DancySelfTestStatus.Failed)
                ? new[] { fixture.ModFolder }
                : Array.Empty<string>(),
        };
    }

    private static FixtureContext PrepareFixture()
    {
        var penumbraRoot = PenumbraDirectoryResolver.GetPenumbraDirectory();
        if (string.IsNullOrWhiteSpace(penumbraRoot) || !Directory.Exists(penumbraRoot))
            throw new InvalidOperationException("Penumbra's mod root is unavailable.");
        if (!PathSafety.TryResolveInsideRoot(penumbraRoot, FixtureDirectory, out var modFolder))
            throw new InvalidOperationException("Dancy refused an unsafe integration fixture path.");

        ResetFixtureDirectory(modFolder);
        Directory.CreateDirectory(Path.Combine(modFolder, "fixture"));

        var target = OnFramework(SelectFixtureTarget);
        var sourceBytes = OnFramework(() => ReadGameFile(target.TargetPaths[0]));
        var sourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes));
        var sourceRelativePath = "fixture/source.pap";
        File.WriteAllBytes(Path.Combine(modFolder, "fixture", "source.pap"), sourceBytes);

        var sourceGamePath = "chara/human/c1501/animation/a0001/bt_common/emote/dancy_debug_source_loop.pap";
        var metaPath = Path.Combine(modFolder, "meta.json");
        AtomicJsonFile.Write(metaPath, new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = FixtureName,
            ["Groups"] = new JArray
            {
                new JObject
                {
                    ["Name"] = "Dancy fixture source",
                    ["Id"] = "1f22c1ec-7d83-4e2d-9f5e-213d40253075",
                    ["Type"] = "Single",
                    ["DefaultSettings"] = 0,
                    ["Options"] = new JArray
                    {
                        new JObject
                        {
                            ["Name"] = "c1501 source",
                            ["Id"] = "16363b37-dcc4-4a4e-a630-a08a61b18ee0",
                            ["Files"] = new JObject { [sourceGamePath] = sourceRelativePath },
                        },
                    },
                },
            },
        });
        AtomicJsonFile.Write(Path.Combine(modFolder, ".dancy-debug-fixture.json"), new JObject { ["Identity"] = FixtureMarker });

        var addResult = new AddMod(Plugin.PluginInterface).Invoke(FixtureDirectory).ToString();
        if (!string.Equals(addResult, "Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Penumbra AddMod returned {addResult}.");

        return new FixtureContext
        {
            ModFolder = modFolder,
            MetaPath = metaPath,
            SourceGamePath = sourceGamePath,
            SourceRelativePath = sourceRelativePath,
            SourceSha256 = sourceSha256,
            TargetTimelineKey = target.TimelineKey,
            TargetName = target.Name,
            TargetCommand = target.Command,
            TargetPaths = target.TargetPaths,
        };
    }

    private static OverridePlan CreatePlan(FixtureContext fixture, IReadOnlyList<string> targetPaths, string targetTimelineKey)
        => OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = FixtureDirectory,
            SourceGroupName = "Dancy fixture source",
            SourceOptionName = "c1501 source",
            TargetTimelineKey = targetTimelineKey,
            TargetName = fixture.TargetName,
            TargetCommand = fixture.TargetCommand,
            Sources = new[] { new OverridePlanSource(fixture.SourceGamePath, fixture.SourceRelativePath) },
            TargetGamePaths = targetPaths,
        });

    private static string VerifyRuntimeRedirections(FixtureContext fixture)
    {
        var plan = GetPlan(fixture);
        var playerIndex = OnFramework(() => Plugin.ObjectTable.LocalPlayer?.ObjectIndex ?? -1);
        if (playerIndex < 0)
            throw new InvalidOperationException("The local player is unavailable for player-scoped Penumbra resolution.");

        var temporarySettings = new SetTemporaryModSettingsPlayer(Plugin.PluginInterface);
        var removeTemporarySettings = new RemoveTemporaryModSettingsPlayer(Plugin.PluginInterface);
        var resolvePlayerPath = new ResolvePlayerPath(Plugin.PluginInterface);
        var options = new Dictionary<string, IReadOnlyList<string>>
        {
            [DancyMetadataMutator.GroupName] = new[] { plan.DisplayName },
        };

        var applied = false;
        try
        {
            var setResult = OnFramework(() => temporarySettings.Invoke(playerIndex, FixtureDirectory, false, true, 9999, options, "Dancy debug self-test", TemporarySettingKey).ToString());
            if (!string.Equals(setResult, "Success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Penumbra temporary setting returned {setResult}.");
            applied = true;

            foreach (var (gamePath, relativePath) in GetFirst(fixture).Execution.FinalMappings)
            {
                if (!PathSafety.TryResolveInsideRoot(fixture.ModFolder, relativePath, out var expectedPath))
                    throw new InvalidOperationException($"Expected generated PAP path was unsafe: {relativePath}");
                var actualPath = OnFramework(() => resolvePlayerPath.Invoke(gamePath));
                if (!EquivalentPath(expectedPath, actualPath))
                    throw new InvalidOperationException($"{gamePath} resolved to {actualPath}, expected {expectedPath}.");
            }

            var untouched = OnFramework(() => resolvePlayerPath.Invoke(fixture.SourceGamePath));
            if (untouched.Replace('\\', '/').Contains("/yucksdancy/paps/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The fixture source path unexpectedly resolved to a Dancy-generated PAP.");
            return $"{GetFirst(fixture).Execution.FinalMappings.Count} runtime redirection(s) verified.";
        }
        finally
        {
            if (applied)
                _ = OnFramework(() => removeTemporarySettings.Invoke(playerIndex, FixtureDirectory, TemporarySettingKey).ToString());
        }
    }

    private static string VerifyFixtureSourceIntegrity(FixtureContext fixture)
    {
        if (!PathSafety.TryResolveInsideRoot(fixture.ModFolder, fixture.SourceRelativePath, out var sourcePath))
            throw new InvalidOperationException("Fixture source PAP path was unsafe.");
        var current = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath)));
        if (!string.Equals(current, fixture.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Normal Dancy operations changed the fixture source PAP bytes.");
        return "Fixture source PAP SHA-256 is unchanged.";
    }

    private static string CleanupFixture(FixtureContext fixture)
    {
        var deleteResult = new DeleteMod(Plugin.PluginInterface).Invoke(FixtureDirectory, FixtureName).ToString();
        if (deleteResult is not "Success" and not "NothingChanged")
            throw new InvalidOperationException($"Penumbra DeleteMod returned {deleteResult}.");

        if (Directory.Exists(fixture.ModFolder))
        {
            ValidateFixtureMarker(fixture.ModFolder);
            Directory.Delete(fixture.ModFolder, recursive: true);
        }
        return "Fixture directory removed.";
    }

    private static void ResetFixtureDirectory(string modFolder)
    {
        if (Directory.Exists(modFolder))
        {
            ValidateFixtureMarker(modFolder);
            Directory.Delete(modFolder, recursive: true);
        }
        Directory.CreateDirectory(modFolder);
    }

    private static void ValidateFixtureMarker(string modFolder)
    {
        var markerPath = Path.Combine(modFolder, ".dancy-debug-fixture.json");
        if (!File.Exists(markerPath))
            throw new InvalidOperationException($"Dancy refused to remove unmarked directory {modFolder}.");
        var marker = JObject.Parse(File.ReadAllText(markerPath));
        if (!string.Equals(marker["Identity"]?.ToString(), FixtureMarker, StringComparison.Ordinal))
            throw new InvalidOperationException($"Dancy refused to remove directory with an invalid fixture marker: {modFolder}.");
    }

    private static FixtureTarget SelectFixtureTarget()
    {
        foreach (var emote in EmoteLibrary.LoopingEmotes)
        {
            var paths = PapResolver.ResolvePapFiles(emote.PrimaryTimelineKey)
                .Where(path => !path.Contains("/c1501/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (RequiredTargetRigs.All(rig => paths.Any(path => path.Contains($"/{rig}/", StringComparison.OrdinalIgnoreCase))))
            {
                return new FixtureTarget(emote.PrimaryTimelineKey, emote.Name, emote.Command, paths);
            }
        }

        throw new InvalidOperationException("No looping emote exposed c0101, c0201, c0901, and c1701 PAP variants for the c1501 fallback fixture.");
    }

    private static byte[] ReadGameFile(string gamePath)
    {
        var file = Plugin.DataManager.GetFile(gamePath) ?? throw new FileNotFoundException($"Game PAP {gamePath} was not found.");
        if (file.Reader.BaseStream.CanSeek)
            file.Reader.BaseStream.Position = 0;
        using var memory = new MemoryStream();
        file.Reader.BaseStream.CopyTo(memory);
        return memory.ToArray();
    }

    private static JObject GetDancyOption(string metaPath, string overrideId)
        => GetDancyOptions(metaPath).Single(option => string.Equals(option["Id"]?.ToString(), overrideId, StringComparison.OrdinalIgnoreCase));

    private static List<JObject> GetDancyOptions(string metaPath)
    {
        var meta = JObject.Parse(File.ReadAllText(metaPath));
        var group = (meta["Groups"] as JArray)?.OfType<JObject>().Single(DancyMetadataMutator.IsDancyGroup)
            ?? throw new InvalidOperationException("Dancy metadata group was not found.");
        return (group["Options"] as JArray)?.OfType<JObject>().ToList() ?? throw new InvalidOperationException("Dancy metadata group has no options.");
    }

    private static bool EquivalentPath(string expected, string actual)
    {
        var expectedFull = Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actualFull = Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(expectedFull, actualFull, StringComparison.OrdinalIgnoreCase);
    }

    private static OverridePlan GetPlan(FixtureContext fixture)
        => fixture.Plan ?? throw new InvalidOperationException("The fixture plan was not created.");

    private static OverrideOperationResult GetFirst(FixtureContext fixture)
        => fixture.First ?? throw new InvalidOperationException("The first production fixture operation did not complete.");

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

    private static void RunCase(List<DancySelfTestCase> cases, string testName, string stage, string expected, Func<string> action, params string[] artifacts)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var actual = action();
            cases.Add(new DancySelfTestCase
            {
                TestName = testName,
                Status = DancySelfTestStatus.Passed,
                Stage = stage,
                Expected = expected,
                Actual = actual,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                Artifacts = artifacts,
            });
        }
        catch (Exception exception)
        {
            cases.Add(new DancySelfTestCase
            {
                TestName = testName,
                Status = DancySelfTestStatus.Failed,
                Stage = stage,
                Expected = expected,
                Actual = exception.Message,
                FailureReason = exception.ToString(),
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                Artifacts = artifacts,
            });
        }
    }

    private sealed class FixtureContext
    {
        public string ModFolder { get; init; } = string.Empty;
        public string MetaPath { get; init; } = string.Empty;
        public string SourceGamePath { get; init; } = string.Empty;
        public string SourceRelativePath { get; init; } = string.Empty;
        public string SourceSha256 { get; init; } = string.Empty;
        public string TargetTimelineKey { get; init; } = string.Empty;
        public string TargetName { get; init; } = string.Empty;
        public string TargetCommand { get; init; } = string.Empty;
        public IReadOnlyList<string> TargetPaths { get; init; } = Array.Empty<string>();
        public OverridePlan? Plan { get; set; }
        public OverridePlan? UpdatePlan { get; set; }
        public OverrideOperationResult? First { get; set; }
        public OverrideOperationResult? Second { get; set; }
        public OverrideOperationResult? Updated { get; set; }
    }

    private sealed record FixtureTarget(string TimelineKey, string Name, string Command, IReadOnlyList<string> TargetPaths);
}
#endif
