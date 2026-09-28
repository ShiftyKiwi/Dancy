#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dancy.Core;
using Dancy.Persistence;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Dancy.Diagnostics;

/// <summary>
/// Exercises an already-installed Standing Idle mod through a narrowly scoped
/// Penumbra temporary setting. It never changes persistent collection data or mod files.
/// </summary>
internal sealed class DancyStandingIdleExistingModRunner
{
    private const string PlayerIdlePath = "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap";
    private const int TemporarySettingKey = -9246;
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(22);

    internal static readonly Candidate CandidateOne = new(
        "[IV] ogRayrei Male Miqo Relaxed Default Idle",
        "ogRayrei",
        "chara\\human\\c0701\\animation\\a0001\\bt_common\\resident\\idle.pap",
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    internal static readonly Candidate CandidateTwo = new(
        "Rust Idle",
        "Coldship",
        "chara\\human\\c0101\\animation\\a0001\\bt_common\\resident\\idle.pap",
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Races"] = new[] { "Miqo'te" },
        });

    internal DancySelfTestResult Run(Candidate candidate)
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();
        Snapshot? snapshot = null;
        var applied = false;

        try
        {
            snapshot = CaptureSnapshot(candidate);
            AddPass(cases, "Capture player-scoped Penumbra snapshot", "snapshot",
                "The local player's collection, candidate settings, and effective idle mapping are recorded before mutation.",
                snapshot.Describe());

            var baseline = ApplyDisabledBaseline(snapshot);
            AddPass(cases, "Capture candidate-free baseline", "baseline",
                "A supported temporary disable removes this candidate only, then restores the exact pre-test state.", baseline);

            var enabled = EnableCandidate(snapshot);
            applied = true;
            AddPass(cases, "Enable existing Standing Idle candidate", "activation",
                "Only the selected installed mod is temporarily enabled for the local player and the c0701 idle path resolves to its known PAP.", enabled);

            cases.Add(new DancySelfTestCase
            {
                TestName = "Human visual Standing Idle observation",
                Status = DancySelfTestStatus.Skipped,
                Stage = "visual",
                Expected = "Observe neutral standing and Change Pose states while the temporary candidate is active.",
                Actual = $"HUMAN CONFIRMATION REQUIRED: candidate remains active for {ObservationWindow.TotalSeconds:0} seconds before automatic restoration.",
            });

            Task.Delay(ObservationWindow).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            AddFailure(cases, "Standing Idle existing-mod test", "preflight", exception);
        }
        finally
        {
            if (applied && snapshot != null)
            {
                try
                {
                    AddPass(cases, "Restore exact player-scoped state", "restore",
                        "The dedicated temporary setting is removed and collection, persistent mod settings, effective mapping, and candidate bytes match the snapshot.",
                        RestoreSnapshot(snapshot));
                }
                catch (Exception exception)
                {
                    AddFailure(cases, "Restore exact player-scoped state", "restore", exception);
                }
            }
        }

        return new DancySelfTestResult
        {
            Schema = "dancy.standing-idle-existing-mod-validation.v1",
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
        };
    }

    /// <summary>
    /// Captures evidence for a candidate that the player already has enabled.
    /// This path deliberately performs no temporary-setting writes, so it can
    /// confirm the live mapping without interrupting a manual visual check.
    /// </summary>
    internal DancySelfTestResult InspectActive(Candidate candidate)
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();
        try
        {
            var snapshot = CaptureSnapshot(candidate);
            if (!snapshot.PersistentEnabled)
                throw new InvalidOperationException($"{candidate.ModName} is not enabled in the local player's persistent collection settings.");

            AddPass(cases, "Capture active Standing Idle candidate", "snapshot",
                "The selected installed mod is persistently enabled for the local player's collection and its source PAP is present.",
                snapshot.Describe());

            if (!EquivalentPath(snapshot.InitialResolvedPath, snapshot.CandidatePapPath))
            {
                throw new InvalidOperationException(
                    $"{PlayerIdlePath} resolved to {snapshot.InitialResolvedPath}, expected the active candidate PAP {snapshot.CandidatePapPath}.");
            }

            AddPass(cases, "Verify active player idle resolution", "resolution",
                "The live c0701 Standing Idle path resolves to the enabled candidate's installed PAP without changing Penumbra state.",
                $"{PlayerIdlePath} -> {snapshot.InitialResolvedPath}; candidateSHA256={snapshot.CandidatePapSha256}");
        }
        catch (Exception exception)
        {
            cases.Add(new DancySelfTestCase
            {
                TestName = "Inspect active Standing Idle candidate",
                Status = DancySelfTestStatus.Failed,
                Stage = "preflight",
                Expected = "The currently enabled candidate resolves to its known c0701 Standing Idle PAP without a Penumbra setting change.",
                Actual = $"{exception.GetType().Name}: {exception.Message}",
                FailureReason = exception.Message,
            });
        }

        return new DancySelfTestResult
        {
            Schema = "dancy.standing-idle-active-mod-inspection.v1",
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
        };
    }

    private static Snapshot CaptureSnapshot(Candidate candidate)
        => OnFramework(() =>
        {
            var playerIndex = Plugin.ObjectTable.LocalPlayer?.ObjectIndex ?? -1;
            if (playerIndex < 0)
                throw new InvalidOperationException("LIVE MOD TEST BLOCKED BY RESTORATION SAFETY: the local player is unavailable.");

            var mod = new GetModList(Plugin.PluginInterface).Invoke()
                .FirstOrDefault(pair => string.Equals(pair.Value, candidate.ModName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(mod.Key))
                throw new InvalidOperationException($"LIVE MOD TEST BLOCKED BY RESTORATION SAFETY: {candidate.ModName} is not installed.");

            if (!TryGetCandidateFile(mod.Key, candidate.RelativePapPath, out var candidatePath))
                throw new InvalidOperationException("LIVE MOD TEST BLOCKED BY RESTORATION SAFETY: the selected candidate PAP is missing or resolves outside its mod directory.");

            var collection = new GetCollectionForObject(Plugin.PluginInterface).Invoke(playerIndex);
            if (!collection.Item1 || collection.Item3.Item1 == Guid.Empty)
                throw new InvalidOperationException("LIVE MOD TEST BLOCKED BY RESTORATION SAFETY: Penumbra did not report an effective local-player collection.");

            var temporary = new QueryTemporaryModSettingsPlayer(Plugin.PluginInterface);
            var temporaryEc = temporary.Invoke(playerIndex, mod.Key, out var temporarySettings, out var temporarySource, TemporarySettingKey);
            if (temporaryEc != PenumbraApiEc.Success)
                throw new InvalidOperationException($"LIVE MOD TEST BLOCKED BY RESTORATION SAFETY: QueryTemporaryModSettingsPlayer returned {temporaryEc}.");
            if (temporarySettings != null)
            {
                var source = string.IsNullOrWhiteSpace(temporarySource) ? "an unknown source" : temporarySource;
                throw new InvalidOperationException($"LIVE MOD TEST BLOCKED BY RESTORATION SAFETY: {candidate.ModName} already has temporary settings from {source}; Dancy will not replace them.");
            }

            var current = new GetCurrentModSettings(Plugin.PluginInterface).Invoke(collection.Item3.Item1, mod.Key);
            if (current.Item1 != PenumbraApiEc.Success || current.Item2 == null)
                throw new InvalidOperationException($"LIVE MOD TEST BLOCKED BY RESTORATION SAFETY: GetCurrentModSettings returned {current.Item1}.");

            var changedItems = new GetChangedItemsForCollection(Plugin.PluginInterface).Invoke(collection.Item3.Item1);
            var idleChangedItems = changedItems.Keys
                .Where(key => key.Contains("idle", StringComparison.OrdinalIgnoreCase))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var resolved = new ResolvePlayerPath(Plugin.PluginInterface).Invoke(PlayerIdlePath);

            return new Snapshot(
                candidate,
                playerIndex,
                collection.Item2,
                collection.Item3.Item1,
                collection.Item3.Item2,
                mod.Key,
                candidatePath,
                HashFile(candidatePath),
                current.Item2.Value.Item1,
                current.Item2.Value.Item2,
                CloneSettings(current.Item2.Value.Item3),
                current.Item2.Value.Item4,
                resolved,
                changedItems.Count,
                idleChangedItems);
        });

    private static string ApplyDisabledBaseline(Snapshot snapshot)
    {
        var baseline = OnFramework(() =>
        {
            EnsureSnapshotCollection(snapshot);
            EnsureNoTemporarySettings(snapshot);
            var result = new SetTemporaryModSettingsPlayer(Plugin.PluginInterface).Invoke(
                snapshot.PlayerIndex,
                snapshot.ModDirectory,
                false,
                false,
                9999,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
                "Dancy Standing Idle baseline control",
                TemporarySettingKey);
            if (result != PenumbraApiEc.Success)
                throw new InvalidOperationException($"Penumbra baseline temporary setting returned {result}.");

            var resolved = new ResolvePlayerPath(Plugin.PluginInterface).Invoke(PlayerIdlePath);
            if (EquivalentPath(resolved, snapshot.CandidatePapPath))
                throw new InvalidOperationException("The candidate-free baseline still resolves to the candidate PAP.");
            return resolved;
        });

        var restored = RestoreSnapshot(snapshot);
        return $"candidate-free={baseline}; {restored}";
    }

    private static string EnableCandidate(Snapshot snapshot)
        => OnFramework(() =>
        {
            EnsureSnapshotCollection(snapshot);
            EnsureNoTemporarySettings(snapshot);
            var result = new SetTemporaryModSettingsPlayer(Plugin.PluginInterface).Invoke(
                snapshot.PlayerIndex,
                snapshot.ModDirectory,
                false,
                true,
                9999,
                snapshot.Candidate.GroupSelections,
                "Dancy Standing Idle evidence escalation",
                TemporarySettingKey);
            if (result != PenumbraApiEc.Success)
                throw new InvalidOperationException($"Penumbra candidate temporary setting returned {result}.");

            var resolved = new ResolvePlayerPath(Plugin.PluginInterface).Invoke(PlayerIdlePath);
            if (!EquivalentPath(resolved, snapshot.CandidatePapPath))
                throw new InvalidOperationException($"{PlayerIdlePath} resolved to {resolved}, expected {snapshot.CandidatePapPath}.");
            if (!string.Equals(HashFile(snapshot.CandidatePapPath), snapshot.CandidatePapSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The candidate PAP changed during the test window.");
            return $"{PlayerIdlePath} -> {resolved}; window={ObservationWindow.TotalSeconds:0}s";
        });

    private static string RestoreSnapshot(Snapshot snapshot)
        => OnFramework(() =>
        {
            EnsureSnapshotCollection(snapshot);
            var result = new RemoveTemporaryModSettingsPlayer(Plugin.PluginInterface).Invoke(
                snapshot.PlayerIndex,
                snapshot.ModDirectory,
                TemporarySettingKey);
            if (result != PenumbraApiEc.Success)
                throw new InvalidOperationException($"Penumbra restoration returned {result}; the temporary setting was not assumed removed.");

            EnsureNoTemporarySettings(snapshot);
            var collection = new GetCollectionForObject(Plugin.PluginInterface).Invoke(snapshot.PlayerIndex);
            if (!collection.Item1 || collection.Item2 != snapshot.IndividualCollection || collection.Item3.Item1 != snapshot.CollectionId
             || !string.Equals(collection.Item3.Item2, snapshot.CollectionName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The local-player collection changed during the test and does not match the snapshot.");
            }

            var current = new GetCurrentModSettings(Plugin.PluginInterface).Invoke(snapshot.CollectionId, snapshot.ModDirectory);
            if (current.Item1 != PenumbraApiEc.Success || current.Item2 == null
             || current.Item2.Value.Item1 != snapshot.PersistentEnabled
             || current.Item2.Value.Item2 != snapshot.PersistentPriority
             || current.Item2.Value.Item4 != snapshot.Inherited
             || !EquivalentSettings(current.Item2.Value.Item3, snapshot.PersistentSettings))
            {
                throw new InvalidOperationException("Persistent candidate mod settings no longer match the pre-test snapshot.");
            }

            var resolved = new ResolvePlayerPath(Plugin.PluginInterface).Invoke(PlayerIdlePath);
            if (!EquivalentPath(resolved, snapshot.InitialResolvedPath))
                throw new InvalidOperationException($"The restored idle mapping is {resolved}, expected {snapshot.InitialResolvedPath}.");
            if (!string.Equals(HashFile(snapshot.CandidatePapPath), snapshot.CandidatePapSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The candidate PAP changed during the test.");
            return $"collection={snapshot.CollectionName}; resolved={resolved}; candidateSHA256={snapshot.CandidatePapSha256}";
        });

    private static void EnsureSnapshotCollection(Snapshot snapshot)
    {
        var collection = new GetCollectionForObject(Plugin.PluginInterface).Invoke(snapshot.PlayerIndex);
        if (!collection.Item1 || collection.Item3.Item1 != snapshot.CollectionId)
            throw new InvalidOperationException("LIVE MOD TEST BLOCKED BY RESTORATION SAFETY: the local player's effective collection changed.");
    }

    private static void EnsureNoTemporarySettings(Snapshot snapshot)
    {
        var temporary = new QueryTemporaryModSettingsPlayer(Plugin.PluginInterface);
        var result = temporary.Invoke(snapshot.PlayerIndex, snapshot.ModDirectory, out var settings, out var source, TemporarySettingKey);
        if (result != PenumbraApiEc.Success || settings != null)
        {
            var owner = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
            throw new InvalidOperationException($"LIVE MOD TEST BLOCKED BY RESTORATION SAFETY: temporary settings are occupied ({result}, source={owner}).");
        }
    }

    private static bool TryGetCandidateFile(string modDirectory, string relativePath, out string candidatePath)
    {
        candidatePath = string.Empty;
        var root = PenumbraDirectoryResolver.GetPenumbraDirectory();
        return !string.IsNullOrWhiteSpace(root)
            && PathSafety.TryResolveInsideRoot(root, modDirectory, out var modFolder)
            && PathSafety.TryResolveInsideRoot(modFolder, relativePath, out candidatePath)
            && File.Exists(candidatePath);
    }

    private static Dictionary<string, List<string>> CloneSettings(IReadOnlyDictionary<string, List<string>> settings)
        => settings.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);

    private static bool EquivalentSettings(IReadOnlyDictionary<string, List<string>> left, IReadOnlyDictionary<string, List<string>> right)
        => left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var values)
                       && pair.Value.SequenceEqual(values, StringComparer.Ordinal));

    private static bool EquivalentPath(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

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

    private static void AddPass(ICollection<DancySelfTestCase> cases, string name, string stage, string expected, string actual)
        => cases.Add(new DancySelfTestCase
        {
            TestName = name,
            Status = DancySelfTestStatus.Passed,
            Stage = stage,
            Expected = expected,
            Actual = actual,
        });

    private static void AddFailure(ICollection<DancySelfTestCase> cases, string name, string stage, Exception exception)
        => cases.Add(new DancySelfTestCase
        {
            TestName = name,
            Status = DancySelfTestStatus.Failed,
            Stage = stage,
            Expected = "The supported temporary-setting experiment completes without touching persistent mod state.",
            Actual = $"{exception.GetType().Name}: {exception.Message}",
            FailureReason = exception.Message,
        });

    internal sealed record Candidate(string ModName, string Author, string RelativePapPath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> GroupSelections);

    private sealed record Snapshot(
        Candidate Candidate,
        int PlayerIndex,
        bool IndividualCollection,
        Guid CollectionId,
        string CollectionName,
        string ModDirectory,
        string CandidatePapPath,
        string CandidatePapSha256,
        bool PersistentEnabled,
        int PersistentPriority,
        IReadOnlyDictionary<string, List<string>> PersistentSettings,
        bool Inherited,
        string InitialResolvedPath,
        int ChangedItemCount,
        IReadOnlyList<string> IdleChangedItems)
    {
        public string Describe()
            => $"playerIndex={PlayerIndex}; collection={CollectionName} ({CollectionId}); individual={IndividualCollection}; mod={Candidate.ModName}; persistentEnabled={PersistentEnabled}; priority={PersistentPriority}; inherited={Inherited}; selections={FormatSettings(PersistentSettings)}; initial={InitialResolvedPath}; changedItems={ChangedItemCount}; idleChangedItems={string.Join(" | ", IdleChangedItems)}; candidateSHA256={CandidatePapSha256}";

        private static string FormatSettings(IReadOnlyDictionary<string, List<string>> settings)
            => string.Join(", ", settings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=[" + string.Join("|", pair.Value) + "]"));
    }
}
#endif
