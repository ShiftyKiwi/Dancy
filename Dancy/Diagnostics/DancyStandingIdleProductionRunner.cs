#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Dancy.Core;
using Dancy.Core.Models;
using Dancy.Domain;
using Dancy.Files;
using Dancy.Pap;
using Dancy.Penumbra;
using Dancy.Persistence;
using Dancy.Services;
using ECommons.DalamudServices;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Dancy.Diagnostics;

/// <summary>
/// End-to-end evidence for the one supported multi-section writer. This is not
/// a generic multi-section test: it uses one c0701 loop source and the fixed
/// normal/idle target, then removes only the Dancy option it created.
/// </summary>
internal sealed class DancyStandingIdleProductionRunner
{
    private const string ModName = "[Myth] Fireball Idle";
    private const string SourceGroupName = "Miqote M";
    private const string SourceOptionName = "Idle 1";
    private const string SourceGamePath = "chara/human/c0701/animation/a0001/bt_common/emote/pose01_loop.pap";
    private const string TargetGamePath = "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap";
    private const string TargetSkeletonGamePath = "chara/human/c0701/skeleton/base/b0001/skl_c0701b0001.sklb";
    private const string FingerprintEndpoint = "VFXEditor.PapAnimationRebuild.Fingerprint";
    private const string FingerprintScheme = "vfxeditor-pap-motion-sample-sha256-v1";
    private const int TemporarySettingKey = -9248;
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public DancySelfTestResult Run()
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();
        var workspace = Path.Combine(Path.GetTempPath(), "Dancy", "StandingIdleProduction", Guid.NewGuid().ToString("N"));
        Context? context = null;
        var temporarySettingApplied = false;

        try
        {
            Directory.CreateDirectory(workspace);
            if (!RunCase(cases, "Capture c0701 source and restoration snapshot", "snapshot",
                    "The known Fireball Idle loop source, clean Dancy ownership state, player collection, and target-native c0701 assets are captured before mutation.",
                    () =>
                    {
                        context = CaptureContext(workspace);
                        return context.Describe();
                    }))
            {
                return Complete(started, cases, workspace);
            }

            if (context is null)
                return Complete(started, cases, workspace);

            if (!RunCase(cases, "Fixed Standing Idle preflight", "preflight",
                    "A one-section c0701 Loop source must select Dancy's StandingIdleMotion0 strategy for the exact normal/idle c0701 topology.",
                    () => VerifyPreflight(context)))
            {
                return Complete(started, cases, workspace);
            }

            if (!RunCase(cases, "Production Dancy Standing Idle create", "create",
                    "OverrideService creates one Dancy-owned PAP and metadata option, then reloads the selected installed mod.",
                    () => Create(context)))
            {
                return Complete(started, cases, workspace);
            }

            if (!RunCase(cases, "Reopen and verify generated Standing Idle PAP", "structural",
                    "The output retains the exact target header/TMB layout, source motion 0 fingerprint, and target-native motion 1 fingerprint.",
                    () => VerifyGeneratedPap(context)))
            {
                return Complete(started, cases, workspace);
            }

            if (!RunCase(cases, "Player-scoped Penumbra redirect", "runtime",
                    "Only the temporary Dancy selection resolves c0701 normal/idle to the verified generated PAP.",
                    () =>
                    {
                        ApplyTemporarySetting(context);
                        temporarySettingApplied = true;
                        return VerifyPlayerRedirect(context);
                    }))
            {
                return Complete(started, cases, workspace);
            }

            cases.Add(new DancySelfTestCase
            {
                TestName = "External Standing Idle runtime observation window",
                Status = DancySelfTestStatus.Skipped,
                Stage = "runtime-observation",
                Expected = "An independent Penumbra GameObjectResourcePathResolved observer must record the local player's c0701 normal/idle request resolving to this Dancy-generated PAP.",
                Actual = $"The production override remains active for {ObservationWindow.TotalSeconds:0} seconds for the external runtime observer before Dancy removes the temporary player setting and its test option.",
            });
            Task.Delay(ObservationWindow).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            AddFailure(cases, "Standing Idle production end-to-end", "execution", exception);
        }
        finally
        {
            if (context is not null && temporarySettingApplied)
                RunCase(cases, "Restore player-scoped Penumbra setting", "restore",
                    "The test-owned temporary setting is removed and the original effective c0701 idle path returns.",
                    () => RestoreTemporarySetting(context));

            if (context?.Operation is not null)
                RunCase(cases, "Remove production test override", "cleanup",
                    "Dancy removes only its test option, its generated PAP, and the Dancy group that did not exist before the test.",
                    () => RemoveTestOverride(context));

            if (context is not null)
                RunCase(cases, "Verify source and metadata restoration", "integrity",
                    "The Fireball source PAP is unchanged and all non-Dancy metadata equals the pre-test snapshot.",
                    () => VerifyRestoration(context));

            try
            {
                if (Directory.Exists(workspace))
                    Directory.Delete(workspace, recursive: true);
            }
            catch (Exception exception)
            {
                AddFailure(cases, "Delete Standing Idle test workspace", "cleanup", exception);
            }
        }

        return Complete(started, cases, workspace);
    }

    private static Context CaptureContext(string workspace)
    {
        var mod = OnFramework(() => new GetModList(Plugin.PluginInterface).Invoke()
            .FirstOrDefault(pair => string.Equals(pair.Value, ModName, StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(mod.Key))
            throw new InvalidOperationException($"{ModName} was not found in Penumbra's installed mod list.");

        var root = PenumbraDirectoryResolver.GetPenumbraDirectory();
        if (string.IsNullOrWhiteSpace(root) || !PathSafety.TryResolveInsideRoot(root, mod.Key, out var modFolder))
            throw new InvalidOperationException("Dancy could not resolve the Fireball Idle mod directory safely.");
        var metaPath = Path.Combine(modFolder, "meta.json");
        if (!File.Exists(metaPath))
            throw new FileNotFoundException("The selected Fireball Idle mod has no meta.json.", metaPath);
        var metadata = JObject.Parse(File.ReadAllText(metaPath));
        if (DancyFileManager.DancyExists(modFolder))
            throw new InvalidOperationException("The selected Fireball Idle mod already has Dancy-owned content; the isolated production test refuses to modify an existing Dancy option.");

        var option = EmoteOverrideScanner.ScanMod(modFolder)
            .SingleOrDefault(candidate => string.Equals(candidate.GroupName, SourceGroupName, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(candidate.OptionName, SourceOptionName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected Fireball Idle source option was not found by Dancy's production scanner.");
        var source = option.LoopEntries.SingleOrDefault(entry => string.Equals(entry.GamePath, SourceGamePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected Fireball Idle option has no unambiguous c0701 pose01 Loop source.");
        if (!PathSafety.TryResolveInsideRoot(modFolder, source.ModdedPapPath, out var sourcePath) || !File.Exists(sourcePath))
            throw new InvalidOperationException($"The selected source PAP is unsafe or missing: {source.ModdedPapPath}");

        var player = OnFramework(() => Plugin.ObjectTable.LocalPlayer ?? throw new InvalidOperationException("The local player is unavailable."));
        var playerIndex = player.ObjectIndex;
        var collection = OnFramework(() => new GetCollectionForObject(Plugin.PluginInterface).Invoke(playerIndex));
        if (!collection.Item1 || collection.Item3.Item1 == Guid.Empty)
            throw new InvalidOperationException("Penumbra did not report an effective local-player collection.");
        var currentSettings = OnFramework(() => new GetCurrentModSettings(Plugin.PluginInterface).Invoke(collection.Item3.Item1, mod.Key));
        if (currentSettings.Item1 != PenumbraApiEc.Success || currentSettings.Item2 is null)
            throw new InvalidOperationException($"Penumbra did not return current Fireball Idle settings: {currentSettings.Item1}.");
        var persistentSettings = CloneSettings(currentSettings.Item2.Value.Item3);
        if (!persistentSettings.TryGetValue(SourceGroupName, out var sourceSelections)
            || !sourceSelections.Contains(SourceOptionName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Enable {ModName} / {SourceGroupName} / {SourceOptionName} before the production validation.");
        }
        var temporary = OnFramework(() =>
        {
            var query = new QueryTemporaryModSettingsPlayer(Plugin.PluginInterface);
            var result = query.Invoke(playerIndex, mod.Key, out var settings, out var owner, TemporarySettingKey);
            return (result, settings, owner);
        });
        if (temporary.result != PenumbraApiEc.Success || temporary.settings is not null)
        {
            var owner = string.IsNullOrWhiteSpace(temporary.owner) ? "unknown" : temporary.owner;
            throw new InvalidOperationException($"The test-owned temporary Penumbra setting is unavailable ({temporary.result}, owner={owner}).");
        }

        var targetPath = CopyGameAsset(TargetGamePath, workspace, "target-c0701-idle.pap");
        var skeletonPath = CopyGameAsset(TargetSkeletonGamePath, workspace, "skl_c0701b0001.sklb");
        var targetInspection = PapFileInspector.InspectFile(targetPath);
        if (!PapCompatibilityPreflight.IsSupportedStandingIdleTarget(new PapTargetInspection(TargetGamePath, targetInspection), out var topologyReason))
            throw new InvalidOperationException($"The current c0701 normal/idle target no longer matches Dancy's proven topology: {topologyReason}");

        return new Context(
            mod.Key,
            mod.Value,
            modFolder,
            metaPath,
            (JObject)metadata.DeepClone(),
            RemoveDancyMetadata(metadata),
            option,
            source,
            sourcePath,
            HashFile(sourcePath),
            playerIndex,
            collection.Item3.Item1,
            persistentSettings,
            new ResolvePlayerPath(Plugin.PluginInterface).Invoke(TargetGamePath),
            targetPath,
            skeletonPath,
            CaptureIntegrity(targetPath));
    }

    private static string VerifyPreflight(Context context)
    {
        var source = PapFileInspector.InspectFile(context.SourcePath);
        var target = PapFileInspector.InspectFile(context.TargetPath);
        var compatibility = PapCompatibilityPreflight.Evaluate(source, new PapTargetInspection(TargetGamePath, target));
        if (!compatibility.CanCreate || compatibility.WriteStrategy != PapOverrideWriteStrategy.StandingIdleMotion0)
            throw new InvalidOperationException($"Dancy did not select the fixed Standing Idle writer: {compatibility.Reason}");

        context.Plan = BuildPlan(context);
        if (!context.Plan.IsValid || context.Plan.PapCopies.Count != 1 || context.Plan.PlannedMappings.Count != 1
            || !context.Plan.PlannedMappings.ContainsKey(TargetGamePath))
        {
            throw new InvalidOperationException("Dancy's production plan did not produce exactly one c0701 Standing Idle mapping.");
        }

        return $"strategy={compatibility.WriteStrategy}; source={context.Source.GamePath}; target={TargetGamePath}; plan={context.Plan.OverrideId}.";
    }

    private static string Create(Context context)
    {
        var plan = RequirePlan(context);
        context.Operation = new OverrideService().CreateOrUpdate(context.ModFolder, context.ModDirectory, context.ModName, plan);
        if (!context.Operation.Reload.Succeeded)
            throw new InvalidOperationException($"Penumbra ReloadMod did not complete after production create: {context.Operation.Reload.UserFacingOutcome}");
        if (context.Operation.Execution.PapResults.Count != 1 || context.Operation.Execution.FinalMappings.Count != 1)
            throw new InvalidOperationException("Dancy production create did not return exactly one Standing Idle output.");
        return $"override={context.Operation.Write.OverrideId}; generated={context.Operation.Execution.GeneratedFiles.Single()}; reload={context.Operation.Reload.Actual}.";
    }

    private static string VerifyGeneratedPap(Context context)
    {
        var operation = RequireOperation(context);
        var plan = RequirePlan(context);
        var patch = operation.Execution.PapResults.Single();
        if (patch.WriteStrategy != PapOverrideWriteStrategy.StandingIdleMotion0
            || !string.Equals(patch.TargetGamePath, TargetGamePath, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(patch.SourceMotionFingerprint)
            || string.IsNullOrWhiteSpace(patch.PreservedTargetMotionFingerprint)
            || patch.PreservedTargetTimelineHashes.Count != 2)
        {
            throw new InvalidOperationException("The production writer did not record the required Standing Idle preservation evidence.");
        }

        var relativePath = operation.Execution.FinalMappings.Single(pair => string.Equals(pair.Key, TargetGamePath, StringComparison.OrdinalIgnoreCase)).Value;
        if (!PathSafety.TryResolveInsideRoot(context.ModFolder, relativePath, out var outputPath) || !File.Exists(outputPath))
            throw new InvalidOperationException($"The generated Standing Idle PAP is unsafe or missing: {relativePath}");
        var persisted = DancyFileManager.GetDancyOverrides(context.ModFolder)
            .SingleOrDefault(option => string.Equals(option.Id, plan.OverrideId, StringComparison.OrdinalIgnoreCase));
        if (persisted is null || persisted.Mappings.Count != 1
            || !persisted.Mappings.TryGetValue(TargetGamePath, out var persistedPath)
            || !string.Equals(persistedPath, relativePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The persisted Dancy option does not match the production Standing Idle mapping.");
        }

        var output = PapFileInspector.InspectFile(outputPath);
        if (!PapCompatibilityPreflight.IsSupportedStandingIdleTarget(new PapTargetInspection(TargetGamePath, output), out var topologyReason))
            throw new InvalidOperationException($"The generated PAP no longer matches the supported Standing Idle topology: {topologyReason}");
        var outputIntegrity = CaptureIntegrity(outputPath);
        RequireSame(context.TargetIntegrity.HeaderHashes, outputIntegrity.HeaderHashes, "target animation headers");
        RequireSame(context.TargetIntegrity.TimelineHashes, outputIntegrity.TimelineHashes, "target-native TMB sections");
        RequireSame(patch.PreservedTargetTimelineHashes, outputIntegrity.TimelineHashes, "writer-reported target-native TMB sections");

        var sourceMotion0 = Fingerprint(context.SourcePath, context.SkeletonPath, 0);
        var targetMotion1 = Fingerprint(context.TargetPath, context.SkeletonPath, 1);
        var outputMotion0 = Fingerprint(outputPath, context.SkeletonPath, 0);
        var outputMotion1 = Fingerprint(outputPath, context.SkeletonPath, 1);
        RequireFingerprint(sourceMotion0, outputMotion0, "source motion 0");
        RequireFingerprint(targetMotion1, outputMotion1, "target-native motion 1");
        RequireFingerprint(sourceMotion0.FingerprintSha256, patch.SourceMotionFingerprint, "writer-reported source motion 0");
        RequireFingerprint(targetMotion1.FingerprintSha256, patch.PreservedTargetMotionFingerprint, "writer-reported target motion 1");
        if (!string.Equals(HashFile(context.SourcePath), context.SourceHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected source PAP changed during Dancy generation.");

        context.OutputPath = outputPath;
        return $"output={relativePath}; sourceM0={sourceMotion0.FingerprintSha256}; targetM1={targetMotion1.FingerprintSha256}; TMB={string.Join(",", outputIntegrity.TimelineHashes)}.";
    }

    private static void ApplyTemporarySetting(Context context)
    {
        var plan = RequirePlan(context);
        var selections = context.PersistentSettings.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.Ordinal);
        selections[DancyMetadataMutator.GroupName] = new[] { plan.DisplayName };
        var result = OnFramework(() => new SetTemporaryModSettingsPlayer(Plugin.PluginInterface).Invoke(
            context.PlayerIndex,
            context.ModDirectory,
            false,
            true,
            9999,
            selections,
            "Dancy Standing Idle production validation",
            TemporarySettingKey));
        if (result != PenumbraApiEc.Success)
            throw new InvalidOperationException($"Penumbra temporary Dancy setting returned {result}.");
        RequestPlayerRedraw(context.PlayerIndex);
        Task.Delay(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    }

    private static string VerifyPlayerRedirect(Context context)
    {
        if (string.IsNullOrWhiteSpace(context.OutputPath))
            throw new InvalidOperationException("The generated PAP output path was not recorded.");
        var actual = OnFramework(() => new ResolvePlayerPath(Plugin.PluginInterface).Invoke(TargetGamePath));
        if (!EquivalentPath(context.OutputPath, actual))
            throw new InvalidOperationException($"{TargetGamePath} resolved to {actual}, expected Dancy output {context.OutputPath}.");
        return $"{TargetGamePath} -> {actual}; observationWindow={ObservationWindow.TotalSeconds:0}s.";
    }

    private static string RestoreTemporarySetting(Context context)
    {
        var result = OnFramework(() => new RemoveTemporaryModSettingsPlayer(Plugin.PluginInterface)
            .Invoke(context.PlayerIndex, context.ModDirectory, TemporarySettingKey));
        if (result != PenumbraApiEc.Success)
            throw new InvalidOperationException($"Penumbra temporary Dancy setting removal returned {result}.");
        RequestPlayerRedraw(context.PlayerIndex);
        var actual = OnFramework(() => new ResolvePlayerPath(Plugin.PluginInterface).Invoke(TargetGamePath));
        if (!EquivalentPath(context.InitialResolvedPath, actual))
            throw new InvalidOperationException($"The restored c0701 idle path is {actual}, expected {context.InitialResolvedPath}.");
        return $"restored={actual}.";
    }

    private static string RemoveTestOverride(Context context)
    {
        var plan = RequirePlan(context);
        var removal = DancyFileManager.RemoveDancyOverride(context.ModFolder, plan.OverrideId);
        if (!removal.RequestedOverrideCleanupSucceeded || !removal.DiskClean)
            throw new InvalidOperationException($"Dancy did not fully remove its production test option: {removal.Verification}");
        var reload = new PenumbraIpcModReloader().Reload(context.ModDirectory, context.ModName);
        return reload.Succeeded
            ? $"removed={removal.RemovedOverrideCount}; files={removal.GarbageCollection.RemovedFiles.Count}; reload={reload.Actual}."
            : $"removed={removal.RemovedOverrideCount}; files={removal.GarbageCollection.RemovedFiles.Count}; disk cleanup verified. Penumbra refresh needs retry/reload: {reload.UserFacingOutcome}";
    }

    private static string VerifyRestoration(Context context)
    {
        if (!string.Equals(HashFile(context.SourcePath), context.SourceHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Fireball Idle source PAP changed during the test.");
        if (DancyFileManager.DancyExists(context.ModFolder))
            throw new InvalidOperationException("Dancy-owned metadata or generated PAPs remain after the production test cleanup.");
        var after = JObject.Parse(File.ReadAllText(context.MetaPath));
        if (!JToken.DeepEquals(context.MetadataWithoutDancy, RemoveDancyMetadata(after)))
            throw new InvalidOperationException("The production test changed metadata outside Dancy-owned content.");
        return $"sourceSHA256={context.SourceHash}; non-Dancy metadata unchanged; Dancy output absent.";
    }

    private static OverridePlan BuildPlan(Context context)
        => OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = context.ModDirectory,
            SourceGroupName = context.SourceOption.GroupName,
            SourceOptionName = context.SourceOption.OptionName,
            SourceAnimationName = context.Source.EmoteName,
            SourceAnimationCommand = context.Source.EmoteCommand,
            TargetTimelineKey = "resident/idle",
            TargetName = "Standing Idle",
            TargetCommand = string.Empty,
            Sources = new[] { new OverridePlanSource(context.Source.GamePath, context.Source.ModdedPapPath) },
            TargetGamePaths = new[] { TargetGamePath },
        });

    private static string CopyGameAsset(string gamePath, string workspace, string fileName)
        => OnFramework(() =>
        {
            var file = Plugin.DataManager.GetFile(gamePath)
                ?? throw new FileNotFoundException($"Game asset {gamePath} was not found.");
            if (file.Reader.BaseStream.CanSeek)
                file.Reader.BaseStream.Position = 0;
            var outputPath = Path.Combine(workspace, fileName);
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Reader.BaseStream.CopyTo(output);
            return outputPath;
        });

    private static PapIntegrity CaptureIntegrity(string path)
    {
        var bytes = PapFileInspector.ReadFileWithRetry(path);
        var inspection = PapFileInspector.Inspect(bytes);
        return new PapIntegrity(
            inspection.AnimationHeaders.Select(header => HashRange(bytes, header.Offset, header.Size)).ToArray(),
            inspection.TimelineSections.Select(section => HashRange(bytes, section.Offset, section.Size)).ToArray());
    }

    private static string HashRange(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
            throw new InvalidDataException("PAP integrity range is invalid.");
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, length)));
    }

    private static FingerprintResponse Fingerprint(string papPath, string skeletonPath, int motionIndex)
    {
        var request = JsonSerializer.Serialize(new FingerprintRequest
        {
            SchemaVersion = 1,
            SourcePath = papPath,
            TargetSkeletonPath = skeletonPath,
            MotionIndex = motionIndex,
        }, JsonOptions);
        var raw = Plugin.PluginInterface.GetIpcSubscriber<string, string>(FingerprintEndpoint).InvokeFunc(request);
        var response = JsonSerializer.Deserialize<FingerprintResponse>(raw, JsonOptions)
            ?? throw new InvalidOperationException("VFXEditor returned no motion fingerprint response.");
        if (!response.Success || !string.Equals(response.FingerprintScheme, FingerprintScheme, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(response.FingerprintSha256))
        {
            throw new InvalidOperationException($"VFXEditor fingerprint failed [{response.ErrorCode}]: {response.Error}");
        }
        return response;
    }

    private static void RequireSame(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string label)
    {
        if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The generated PAP changed {label}.");
    }

    private static void RequireFingerprint(FingerprintResponse expected, FingerprintResponse actual, string label)
        => RequireFingerprint(expected.FingerprintSha256, actual.FingerprintSha256, label);

    private static void RequireFingerprint(string expected, string actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The generated PAP did not preserve {label}.");
    }

    private static OverridePlan RequirePlan(Context context)
        => context.Plan ?? throw new InvalidOperationException("The production Standing Idle plan was not created.");

    private static OverrideOperationResult RequireOperation(Context context)
        => context.Operation ?? throw new InvalidOperationException("The production Standing Idle operation did not complete.");

    private static JObject RemoveDancyMetadata(JObject metadata)
    {
        var copy = (JObject)metadata.DeepClone();
        if (copy["Groups"] is not JArray groups)
            return copy;
        foreach (var group in groups.OfType<JObject>().Where(DancyMetadataMutator.IsDancyGroup).ToList())
            group.Remove();
        return copy;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Dictionary<string, List<string>> CloneSettings(IReadOnlyDictionary<string, List<string>> settings)
        => settings.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);

    private static void RequestPlayerRedraw(int playerIndex)
        => OnFramework(() =>
        {
            new RedrawObject(Plugin.PluginInterface).Invoke(playerIndex);
            return true;
        });

    private static bool EquivalentPath(string expected, string actual)
        => string.Equals(
            Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

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

    private static bool RunCase(List<DancySelfTestCase> cases, string name, string stage, string expected, Func<string> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cases.Add(new DancySelfTestCase
            {
                TestName = name,
                Status = DancySelfTestStatus.Passed,
                Stage = stage,
                Expected = expected,
                Actual = action(),
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
            });
            return true;
        }
        catch (Exception exception)
        {
            AddFailure(cases, name, stage, exception, stopwatch.ElapsedMilliseconds);
            return false;
        }
    }

    private static void AddFailure(ICollection<DancySelfTestCase> cases, string name, string stage, Exception exception, long? duration = null)
        => cases.Add(new DancySelfTestCase
        {
            TestName = name,
            Status = DancySelfTestStatus.Failed,
            Stage = stage,
            Expected = "The fixed Standing Idle writer completes without changing source mod data outside Dancy-owned content.",
            Actual = $"{exception.GetType().Name}: {exception.Message}",
            FailureReason = exception.ToString(),
            DurationMilliseconds = duration ?? 0,
        });

    private static DancySelfTestResult Complete(DateTimeOffset started, IReadOnlyList<DancySelfTestCase> cases, string workspace)
        => new()
        {
            Schema = "dancy.standing-idle-production-e2e.v1",
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
            RetainedArtifacts = Directory.Exists(workspace) ? new[] { workspace } : Array.Empty<string>(),
        };

    private sealed class Context
    {
        public Context(
            string modDirectory,
            string modName,
            string modFolder,
            string metaPath,
            JObject metadata,
            JObject metadataWithoutDancy,
            RemappableOption sourceOption,
            ParsedEmoteOverride source,
            string sourcePath,
            string sourceHash,
            int playerIndex,
            Guid collectionId,
            IReadOnlyDictionary<string, List<string>> persistentSettings,
            string initialResolvedPath,
            string targetPath,
            string skeletonPath,
            PapIntegrity targetIntegrity)
        {
            ModDirectory = modDirectory;
            ModName = modName;
            ModFolder = modFolder;
            MetaPath = metaPath;
            Metadata = metadata;
            MetadataWithoutDancy = metadataWithoutDancy;
            SourceOption = sourceOption;
            Source = source;
            SourcePath = sourcePath;
            SourceHash = sourceHash;
            PlayerIndex = playerIndex;
            CollectionId = collectionId;
            PersistentSettings = persistentSettings;
            InitialResolvedPath = initialResolvedPath;
            TargetPath = targetPath;
            SkeletonPath = skeletonPath;
            TargetIntegrity = targetIntegrity;
        }

        public string ModDirectory { get; }
        public string ModName { get; }
        public string ModFolder { get; }
        public string MetaPath { get; }
        public JObject Metadata { get; }
        public JObject MetadataWithoutDancy { get; }
        public RemappableOption SourceOption { get; }
        public ParsedEmoteOverride Source { get; }
        public string SourcePath { get; }
        public string SourceHash { get; }
        public int PlayerIndex { get; }
        public Guid CollectionId { get; }
        public IReadOnlyDictionary<string, List<string>> PersistentSettings { get; }
        public string InitialResolvedPath { get; }
        public string TargetPath { get; }
        public string SkeletonPath { get; }
        public PapIntegrity TargetIntegrity { get; }
        public OverridePlan? Plan { get; set; }
        public OverrideOperationResult? Operation { get; set; }
        public string OutputPath { get; set; } = string.Empty;

        public string Describe()
            => $"mod={ModName}; source={Source.GamePath}->{Source.ModdedPapPath}; sourceSHA256={SourceHash}; playerIndex={PlayerIndex}; collection={CollectionId}; initial={InitialResolvedPath}; targetHeaders={string.Join(",", TargetIntegrity.HeaderHashes)}; targetTMB={string.Join(",", TargetIntegrity.TimelineHashes)}.";
    }

    private sealed record PapIntegrity(IReadOnlyList<string> HeaderHashes, IReadOnlyList<string> TimelineHashes);

    private sealed class FingerprintRequest
    {
        public int SchemaVersion { get; init; }
        public string SourcePath { get; init; } = string.Empty;
        public string TargetSkeletonPath { get; init; } = string.Empty;
        public int MotionIndex { get; init; }
    }

    private sealed class FingerprintResponse
    {
        public bool Success { get; init; }
        public string FingerprintScheme { get; init; } = string.Empty;
        public string FingerprintSha256 { get; init; } = string.Empty;
        public string? ErrorCode { get; init; }
        public string? Error { get; init; }
    }
}
#endif
