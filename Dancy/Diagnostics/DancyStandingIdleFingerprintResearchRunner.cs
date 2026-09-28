#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using Dancy.Core;
using Dancy.Domain;
using Dancy.Pap;
using Dancy.Persistence;
using ECommons.DalamudServices;
using Penumbra.Api.IpcSubscribers;

namespace Dancy.Diagnostics;

/// <summary>
/// Calls VFXEditor's read-only per-motion fingerprint endpoint for a small,
/// controlled set of PAPs. Game data is copied only to a temporary Dancy-owned
/// workspace, which is removed before the runner returns.
/// </summary>
internal sealed class DancyStandingIdleFingerprintResearchRunner
{
    private const string VfxCapabilitiesEndpoint = "VFXEditor.PapAnimationRebuild.Capabilities";
    private const string VfxFingerprintEndpoint = "VFXEditor.PapAnimationRebuild.Fingerprint";
    private const string PushupsModName = "[HS] Warrior of Lift (Default)";
    private const string PushupsGroupName = "Bench Press - /pushups";
    private const string PushupsOptionName = "Enable";
    private const string FingerprintScheme = "vfxeditor-pap-motion-sample-sha256-v1";

    public DancySelfTestResult Run()
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();
        var workspace = Path.Combine(Path.GetTempPath(), "Dancy", "StandingIdleFingerprintResearch", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workspace);
            RunCase(cases, "VFXEditor fingerprint capability", "capability", "The active VFXEditor instance advertises the read-only motion fingerprint endpoint.", VerifyCapabilities);
            RunCase(cases, "Water repeat fingerprint", "control", "Repeated sampling of one Water motion is deterministic.", () => VerifyRepeatedGamePap("Water", ResolveWaterPath(), 0, workspace));
            RunCase(cases, "Push-ups repeat fingerprint", "control", "Repeated sampling of one installed Push-ups motion is deterministic.", () => VerifyRepeatedPushups(workspace));
            RunCase(cases, "Standing Idle indexed fingerprints", "standing-idle", "Standing Idle motions 0 and 1 have independently derived semantic fingerprints, not two copies of the shared PAP hash.", () => VerifyStandingIdle(workspace));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception exception) { Svc.Log.Warning(exception, "[Dancy] Could not remove the temporary fingerprint research workspace."); }
            }
        }

        return new DancySelfTestResult
        {
            Schema = "dancy.standing-idle-motion-fingerprint-research.v1",
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
        };
    }

    private static string VerifyCapabilities()
    {
        var raw = Plugin.PluginInterface.GetIpcSubscriber<string>(VfxCapabilitiesEndpoint).InvokeFunc();
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("supportsReadOnlyMotionFingerprinting", out var supported) || !supported.GetBoolean())
            throw new InvalidOperationException("The active VFXEditor instance does not advertise read-only motion fingerprinting.");
        return raw;
    }

    private static string VerifyRepeatedGamePap(string label, string gamePapPath, int motionIndex, string workspace)
    {
        var character = GamePathIdentity.Parse(gamePapPath).Character;
        var localPap = CopyGameAsset(gamePapPath, workspace, $"{label}-{character.Code}.pap");
        var localSkeleton = CopyGameAsset(SkeletonGamePath(character.Code), workspace, $"skl_{character.Code}b0001.sklb");
        var first = Fingerprint(localPap, localSkeleton, motionIndex);
        var repeated = Fingerprint(localPap, localSkeleton, motionIndex);
        RequireDeterministic(first, repeated, label);
        return Describe(first, $"{label}; gamePath={gamePapPath}; rig={character.DisplayName}");
    }

    private static string VerifyRepeatedPushups(string workspace)
    {
        var mod = new GetModList(Plugin.PluginInterface).Invoke()
            .FirstOrDefault(pair => string.Equals(pair.Value, PushupsModName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(mod.Key))
            throw new InvalidOperationException($"{PushupsModName} is not installed in the current Penumbra library.");
        var root = PenumbraDirectoryResolver.GetPenumbraDirectory();
        if (string.IsNullOrWhiteSpace(root) || !PathSafety.TryResolveInsideRoot(root, mod.Key, out var modFolder))
            throw new InvalidOperationException("Dancy could not resolve the installed Push-ups source folder safely.");
        var source = EmoteOverrideScanner.ScanMod(modFolder)
            .SingleOrDefault(option => string.Equals(option.GroupName, PushupsGroupName, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(option.OptionName, PushupsOptionName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The installed Warrior of Lift mod has no Bench Press /pushups Enable source option.");
        var entry = source.LoopEntries.FirstOrDefault()
            ?? throw new InvalidOperationException("The installed Push-ups source has no Loop PAP.");
        if (!PathSafety.TryResolveInsideRoot(modFolder, entry.ModdedPapPath, out var sourcePath) || !File.Exists(sourcePath))
            throw new InvalidOperationException($"The Push-ups source PAP is unsafe or missing: {entry.ModdedPapPath}");

        var character = GamePathIdentity.Parse(entry.GamePath).Character;
        if (!character.IsKnown)
            throw new InvalidOperationException($"Dancy could not derive a known player rig from Push-ups path {entry.GamePath}.");
        var localPap = Path.Combine(workspace, $"pushups-{character.Code}.pap");
        File.Copy(sourcePath, localPap, overwrite: true);
        var localSkeleton = CopyGameAsset(SkeletonGamePath(character.Code), workspace, $"skl_{character.Code}b0001.sklb");
        var first = Fingerprint(localPap, localSkeleton, 0);
        var repeated = Fingerprint(localPap, localSkeleton, 0);
        RequireDeterministic(first, repeated, "Push-ups");
        return Describe(first, $"Push-ups; source={entry.ModdedPapPath}; rig={character.DisplayName}");
    }

    private static string VerifyStandingIdle(string workspace)
    {
        var player = OnFramework(() => Plugin.ObjectTable.LocalPlayer)
            ?? throw new InvalidOperationException("No local player is available for player-scoped Standing Idle research.");
        var playerRace = player.Customize[(int)CustomizeIndex.Race];
        var playerGender = player.Customize[(int)CustomizeIndex.Gender];
        var playerTribe = player.Customize[(int)CustomizeIndex.Tribe];
        var playerCode = ResolveCharacterCode(playerRace, playerGender, playerTribe);
        var standingPath = OnFramework(() => PapResolver.ResolvePapFiles("normal/idle")
            .SingleOrDefault(path => path.Contains($"/{playerCode}/", StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException($"normal/idle did not resolve a PAP for the local player's rig {playerCode}.");
        var localPap = CopyGameAsset(standingPath, workspace, $"standing-idle-{playerCode}.pap");
        var localSkeleton = CopyGameAsset(SkeletonGamePath(playerCode), workspace, $"skl_{playerCode}b0001.sklb");

        var motion0 = Fingerprint(localPap, localSkeleton, 0);
        var motion0Repeated = Fingerprint(localPap, localSkeleton, 0);
        var motion1 = Fingerprint(localPap, localSkeleton, 1);
        var motion1Repeated = Fingerprint(localPap, localSkeleton, 1);
        RequireDeterministic(motion0, motion0Repeated, "Standing Idle motion 0");
        RequireDeterministic(motion1, motion1Repeated, "Standing Idle motion 1");
        if (!string.Equals(motion0.SourceSha256, motion1.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Standing Idle motions unexpectedly returned different source PAP hashes.");
        if (string.Equals(motion0.FingerprintSha256, motion1.FingerprintSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Standing Idle motion 0 and 1 produced the same semantic fingerprint; no per-motion difference was observed.");

        return $"playerCustomizeRace={playerRace}; playerCustomizeGender={playerGender}; playerCustomizeTribe={playerTribe}; playerRig={playerCode}; gamePath={standingPath}; motion0={motion0.FingerprintSha256}; motion1={motion1.FingerprintSha256}; sharedPap={motion0.SourceSha256}; motion0Tracks={motion0.AnimatedTrackCount}; motion1Tracks={motion1.AnimatedTrackCount}";
    }

    private static VfxFingerprintResponse Fingerprint(string sourcePath, string skeletonPath, int motionIndex)
    {
        var request = JsonSerializer.Serialize(new VfxFingerprintRequest
        {
            SchemaVersion = 1,
            SourcePath = sourcePath,
            TargetSkeletonPath = skeletonPath,
            MotionIndex = motionIndex,
        }, JsonOptions);
        var raw = Plugin.PluginInterface.GetIpcSubscriber<string, string>(VfxFingerprintEndpoint).InvokeFunc(request);
        var response = JsonSerializer.Deserialize<VfxFingerprintResponse>(raw, JsonOptions)
            ?? throw new InvalidOperationException("VFXEditor returned no fingerprint response.");
        if (!response.Success)
            throw new InvalidOperationException($"VFXEditor fingerprint failed [{response.ErrorCode}]: {response.Error}");
        if (!string.Equals(response.FingerprintScheme, FingerprintScheme, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(response.FingerprintSha256)
            || response.SampleRate != 30
            || response.SampleCount is null or < 1)
            throw new InvalidOperationException("VFXEditor returned an incomplete or unsupported motion-fingerprint response.");
        return response;
    }

    private static void RequireDeterministic(VfxFingerprintResponse first, VfxFingerprintResponse repeated, string label)
    {
        if (!string.Equals(first.FingerprintSha256, repeated.FingerprintSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{label} returned different fingerprints across identical repeated reads.");
        if (!string.Equals(first.SourceSha256, repeated.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{label} source PAP hash changed during repeated read-only fingerprinting.");
    }

    private static string ResolveWaterPath()
        => OnFramework(() =>
        {
            var water = EmoteLibrary.AllEmotes.SingleOrDefault(emote => string.Equals(emote.Command, "/water", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The current target catalog does not contain /water.");
            return PapResolver.ResolvePapFiles(water.PrimaryTimelineKey).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                ?? throw new InvalidOperationException("/water did not resolve a current PAP path.");
        });

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

    private static string ResolveCharacterCode(byte race, byte gender, byte tribe)
    {
        var prefix = tribe switch
        {
            1 => "c01", 2 => "c03", 3 or 4 => "c05", 5 or 6 => "c11",
            7 or 8 => "c07", 9 or 10 => "c09", 11 or 12 => "c13",
            13 or 14 => "c15", 15 or 16 => "c17", _ => string.Empty,
        };
        if (string.IsNullOrWhiteSpace(prefix) || gender is > 1)
            throw new InvalidOperationException($"Dancy cannot map local player customization race={race}, gender={gender}, tribe={tribe} to a canonical character path.");

        // The public Customize payload encodes 0 as male and 1 as female.
        return prefix + (gender == 0 ? "01" : "02");
    }

    private static string Describe(VfxFingerprintResponse response, string prefix)
        => $"{prefix}; motion={response.MotionIndex}; duration={response.DurationSeconds}; skeletonBones={response.SkeletonBoneCount}; tracks={response.AnimatedTrackCount}; sampleCount={response.SampleCount}; fingerprint={response.FingerprintSha256}; source={response.SourceSha256}";

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
        try
        {
            cases.Add(new DancySelfTestCase { TestName = testName, Stage = stage, Expected = expected, Actual = action(), Status = DancySelfTestStatus.Passed, DurationMilliseconds = stopwatch.ElapsedMilliseconds });
        }
        catch (Exception exception)
        {
            cases.Add(new DancySelfTestCase { TestName = testName, Stage = stage, Expected = expected, Actual = exception.Message, FailureReason = exception.ToString(), Status = DancySelfTestStatus.Failed, DurationMilliseconds = stopwatch.ElapsedMilliseconds });
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class VfxFingerprintRequest
    {
        public int SchemaVersion { get; init; }
        public string SourcePath { get; init; } = string.Empty;
        public string TargetSkeletonPath { get; init; } = string.Empty;
        public int MotionIndex { get; init; }
    }

    private sealed class VfxFingerprintResponse
    {
        public bool Success { get; init; }
        public string SourceSha256 { get; init; } = string.Empty;
        public int? MotionIndex { get; init; }
        public float? DurationSeconds { get; init; }
        public int? SkeletonBoneCount { get; init; }
        public int? AnimatedTrackCount { get; init; }
        public int SampleRate { get; init; }
        public int? SampleCount { get; init; }
        public string FingerprintScheme { get; init; } = string.Empty;
        public string FingerprintSha256 { get; init; } = string.Empty;
        public string? ErrorCode { get; init; }
        public string? Error { get; init; }
    }
}
#endif
