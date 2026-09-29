#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Penumbra.Api.IpcSubscribers;

namespace Dancy.Diagnostics;

/// <summary>
/// Production-only coverage for the one supported selector-backed source bank:
/// Warrior of Lift's Treadmill options. It leaves the original option files
/// untouched and cleans its one generated Dancy option before returning.
/// </summary>
internal sealed class DancyTreadmillSelectorRegressionRunner
{
    private const string ModName = "[HS] Warrior of Lift (Default)";
    private const string SourceGroupName = "Treadmill - /breathcontrol";
    private const string RunOptionName = "Run";
    private const string TargetCommand = "/water";
    private const int TemporarySettingKey = -9277;

    private static readonly IReadOnlyDictionary<string, (string CompanionFile, string Event, int Index)> ExpectedSelections =
        new Dictionary<string, (string, string, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Style"] = ("loop_emot11_loop_back.tmb", "cbem_treadmill_01b_lp0", 0),
            ["Walk"] = ("loop_emot11_loop.tmb", "cbem_treadmill_01f_lp0", 1),
            ["Run"] = ("loop_emot11_loop_run.tmb", "cbem_treadmill_02f_lp0", 2),
            ["Sprint"] = ("loop_emot11_loop_sprint.tmb", "cbem_treadmill_sprint_lp0", 3),
        };

    public DancySelfTestResult Run()
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();
        RegressionContext? context = null;
        try
        {
            if (!RunCase(cases, "Build provenance", "provenance", "The loaded Debug Dancy assembly is readable.", () =>
                {
                    var path = Plugin.PluginInterface.AssemblyLocation.FullName;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        throw new InvalidOperationException("The loaded Dancy Debug assembly location is unavailable.");
                    return $"{path} [{HashFile(path)}]";
                }))
            {
                return Complete(started, cases);
            }

            if (!RunCase(cases, "Locate Treadmill fixture", "baseline", "One installed Warrior of Lift Treadmill fixture is available and no same-ID test override will be replaced.", () =>
                {
                    context = CaptureContext();
                    return $"{context.ModDirectory}; source {context.RunSource.LoopEntries.Count} logical loop path(s); shared PAP {context.SharedPapPath}; metadata {context.MetadataFormat}; source SHA-256 {context.SourcePapHash}.";
                }))
            {
                return Complete(started, cases);
            }

            if (context is null)
                return Complete(started, cases);

            if (!RunCase(cases, "Shared PAP structure", "selection", "The installed shared PAP has four coherent headers, Havok bindings, and embedded timelines.", () => DescribeSharedPap(context)))
                return Complete(started, cases);

            if (!RunCase(cases, "All option selectors", "selection", "Style, Walk, Run, and Sprint each resolve from their companion action TMB to one distinct source motion.", () => VerifyAllSelectors(context)))
                return Complete(started, cases);

            if (!RunCase(cases, "Run target resolution", "target", "A current one-section /water target is available for the Run production route.", () => DescribeTarget(context)))
                return Complete(started, cases);

            if (!RunCase(cases, "Run planning and preflight", "preflight", "The normal production plan is valid and selector-backed Run is accepted only for one-section targets.", () => VerifyPreflight(context)))
                return Complete(started, cases);

            if (!RunCase(cases, "Production create and reload", "create", "OverrideService writes only one Dancy-owned option and reloads the selected mod.", () => Create(context)))
                return Complete(started, cases);

            if (!RunCase(cases, "Generated Run PAP", "pap", "The generated one-section PAP uses Run header/motion/timeline and no other active treadmill-bank section.", () => VerifyGeneratedPap(context)))
                return Complete(started, cases);

            if (!RunCase(cases, "Source motion fingerprint", "fingerprint", "VFXEditor fingerprints the selected Run motion before and after generation identically.", () => VerifyFingerprint(context)))
                return Complete(started, cases);

            if (!RunCase(cases, "Penumbra runtime resolution", "runtime", "Temporary player-scoped selection resolves every mapped /water PAP to Dancy output.", () => VerifyRuntimeAndTrigger(context)))
                return Complete(started, cases);

            if (!RunCase(cases, "Source integrity", "integrity", "The original shared PAP and all non-Dancy metadata remain unchanged.", () => VerifySourceIntegrity(context)))
                return Complete(started, cases);

            return Complete(started, cases);
        }
        finally
        {
            if (context?.Created == true)
                RunCase(cases, "Cleanup and restoration", "cleanup", "Only the generated Treadmill Run test option and its unreferenced output are removed; unrelated Dancy options remain.", () => Cleanup(context));
        }
    }

    private static RegressionContext CaptureContext()
    {
        var mod = OnFramework(() => new GetModList(Plugin.PluginInterface).Invoke()
            .FirstOrDefault(pair => string.Equals(pair.Value, ModName, StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(mod.Key))
            throw new InvalidOperationException($"{ModName} was not found in Penumbra's installed mod list.");

        var penumbraRoot = PenumbraDirectoryResolver.GetPenumbraDirectory();
        if (string.IsNullOrWhiteSpace(penumbraRoot) || !PathSafety.TryResolveInsideRoot(penumbraRoot, mod.Key, out var modFolder))
            throw new InvalidOperationException("Dancy could not resolve the Warrior of Lift folder safely.");

        var metaPath = Path.Combine(modFolder, "meta.json");
        if (!File.Exists(metaPath))
            throw new FileNotFoundException("Warrior of Lift meta.json is unavailable.", metaPath);
        var metadata = JObject.Parse(File.ReadAllText(metaPath));
        var sourceOptions = EmoteOverrideScanner.ScanMod(modFolder)
            .Where(option => string.Equals(option.GroupName, SourceGroupName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(option => option.OptionName, StringComparer.OrdinalIgnoreCase);
        foreach (var optionName in ExpectedSelections.Keys)
        {
            if (!sourceOptions.ContainsKey(optionName))
                throw new InvalidOperationException($"Warrior of Lift is missing Treadmill option '{optionName}'.");
        }

        var run = sourceOptions[RunOptionName];
        var runEntries = run.LoopEntries;
        if (runEntries.Count == 0)
            throw new InvalidOperationException("The Treadmill Run option has no Loop source PAP paths.");
        var physicalPaps = runEntries.Select(entry => entry.ModdedPapPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (physicalPaps.Count != 1 || !PathSafety.TryResolveInsideRoot(modFolder, physicalPaps[0], out var sharedPapPath) || !File.Exists(sharedPapPath))
            throw new InvalidOperationException("Treadmill Run must resolve to one existing shared physical PAP.");

        var target = OnFramework(() => EmoteLibrary.AllEmotes.FirstOrDefault(emote => string.Equals(emote.Command, TargetCommand, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException($"The current game data has no {TargetCommand} target.");
        var targetPaths = OnFramework(() => PapResolver.ResolvePapFiles(target.PrimaryTimelineKey)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList());
        if (targetPaths.Count == 0)
            throw new InvalidOperationException($"No current PAP paths resolved for {TargetCommand}.");
        var targetInspections = OnFramework(() => targetPaths.ToDictionary(path => path, PapEditor.InspectTargetPap, StringComparer.OrdinalIgnoreCase));
        if (targetInspections.Values.Any(inspection => inspection.AnimationCount != 1 || inspection.TimelineSectionSizes.Count != 1))
            throw new InvalidOperationException($"{TargetCommand} no longer resolves solely to normal one-section PAP targets.");

        var plan = BuildPlan(mod.Key, run, target, targetPaths);
        if (!plan.IsValid)
            throw new InvalidOperationException(string.Join("; ", plan.Errors));
        var existing = DancyFileManager.GetDancyOverrides(modFolder);
        if (existing.Any(option => string.Equals(option.Id, plan.OverrideId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A matching Dancy Treadmill Run test option already exists. Dancy will not overwrite a user-owned matching override during validation.");

        return new RegressionContext
        {
            ModDirectory = mod.Key,
            ModFolder = modFolder,
            MetaPath = metaPath,
            MetadataFormat = $"Penumbra meta.json FileVersion {metadata["FileVersion"]?.Value<int?>() ?? 0}",
            MetadataWithoutDancy = RemoveDancyMetadata(metadata),
            SourceOptions = sourceOptions,
            RunSource = run,
            SharedPapPath = sharedPapPath,
            SourcePapHash = HashFile(sharedPapPath),
            Target = target,
            TargetPaths = targetPaths,
            TargetInspections = targetInspections,
            Plan = plan,
            DancyOverridesBefore = existing,
        };
    }

    private static string DescribeSharedPap(RegressionContext context)
    {
        var inspection = PapFileInspector.InspectFile(context.SharedPapPath);
        var embedded = PapEditor.ReadEmbeddedTimelineEventIdentifiers(context.SharedPapPath);
        if (inspection.AnimationCount != 4
            || inspection.AnimationNames.Count != 4
            || inspection.HavokIndices.Count != 4
            || inspection.TimelineSectionSizes.Count != 4
            || embedded.Count != 4)
        {
            throw new InvalidOperationException("The Treadmill shared PAP is not a coherent four-motion source bank.");
        }

        return string.Join(" | ", Enumerable.Range(0, inspection.AnimationCount)
            .Select(index => $"{index}: {inspection.AnimationNames[index]}; Havok {inspection.HavokIndices[index]}; embedded TMB {index} [{string.Join(",", embedded[index])}]"));
    }

    private static string VerifyAllSelectors(RegressionContext context)
    {
        var actual = new List<string>();
        foreach (var (optionName, expected) in ExpectedSelections)
        {
            var option = context.SourceOptions[optionName];
            var plan = BuildPlan(context.ModDirectory, option, context.Target, context.TargetPaths);
            if (!plan.IsValid)
                throw new InvalidOperationException($"Treadmill {optionName} did not produce a valid plan: {string.Join("; ", plan.Errors)}");
            var copy = plan.PapCopies.Single();
            var selectionResult = SourceAnimationSelectionResolver.Resolve(context.ModFolder, copy);
            if (!selectionResult.IsSuccess || selectionResult.Selection is null)
                throw new InvalidOperationException($"Treadmill {optionName} selector failed: {selectionResult.Error}");
            var selection = selectionResult.Selection;
            var hasExpectedCompanion = option.CompanionTimelines.Any(timeline => timeline.IsActionTimeline
                && timeline.ModdedTimelinePath.EndsWith(expected.CompanionFile, StringComparison.OrdinalIgnoreCase));
            if (selection.Method != SourceAnimationSelectionMethod.CompanionTimelineEvent
                || selection.AnimationHeaderIndex != expected.Index
                || selection.HavokMotionIndex != expected.Index
                || selection.EmbeddedTmbIndex != expected.Index
                || !string.Equals(selection.AnimationEvent, expected.Event, StringComparison.OrdinalIgnoreCase)
                || !hasExpectedCompanion)
            {
                throw new InvalidOperationException($"Treadmill {optionName} resolved to method {selection.Method}, header {selection.AnimationHeaderIndex}, motion {selection.HavokMotionIndex}, timeline {selection.EmbeddedTmbIndex}, event {selection.AnimationEvent}; expected companion {expected.CompanionFile}, event {expected.Event} at {expected.Index}.");
            }
            actual.Add($"{optionName}: {expected.CompanionFile} -> {selection.AnimationEvent}; header {selection.AnimationHeaderIndex}; motion {selection.HavokMotionIndex}; embedded TMB {selection.EmbeddedTmbIndex}");
        }
        return string.Join(" | ", actual);
    }

    private static string DescribeTarget(RegressionContext context)
        => $"{context.Target.Name} {context.Target.Command}; {context.TargetPaths.Count} current target path(s); " +
           string.Join(" | ", context.TargetPaths.Select(path => $"{GamePathIdentity.Parse(path).Character.Code}:{context.TargetInspections[path].AnimationNames.Single()} H{context.TargetInspections[path].HavokIndices.Single()}"));

    private static string VerifyPreflight(RegressionContext context)
    {
        var copy = context.Plan.PapCopies.Single();
        var selection = SourceAnimationSelectionResolver.Resolve(context.ModFolder, copy).Selection
            ?? throw new InvalidOperationException("The Run source selector did not return a selection.");
        var source = PapFileInspector.InspectFile(context.SharedPapPath);
        var result = PapCompatibilityPreflight.Evaluate(source, selection, copy.TargetGamePaths.Select(path => new PapTargetInspection(path, context.TargetInspections[path])));
        if (!result.CanCreate || result.WriteStrategy != PapOverrideWriteStrategy.SingleSectionEventPatch)
            throw new InvalidOperationException($"The selector-backed Run route was rejected: {result.Reason}");
        return $"{result.Status}: {result.Reason}; selected {selection.AnimationEvent} / H{selection.HavokMotionIndex} / TMB {selection.EmbeddedTmbIndex}.";
    }

    private static string Create(RegressionContext context)
    {
        context.Operation = new OverrideService().CreateOrUpdate(context.ModFolder, context.ModDirectory, ModName, context.Plan);
        context.Created = true;
        if (!context.Operation.Reload.Succeeded)
            throw new InvalidOperationException($"Penumbra ReloadMod did not complete after test create: {context.Operation.Reload.UserFacingOutcome}");
        return $"Stable ID {context.Operation.Write.OverrideId}; generated {context.Operation.Execution.GeneratedFiles.Count} PAP(s); mapped {context.Operation.Execution.FinalMappings.Count} target path(s); reload {context.Operation.Reload.Actual}.";
    }

    private static string VerifyGeneratedPap(RegressionContext context)
    {
        var operation = RequireOperation(context);
        var run = ExpectedSelections[RunOptionName];
        var result = operation.Execution.PapResults.Single();
        var selection = result.SourceSelection ?? throw new InvalidOperationException("The generated PAP did not retain its source-motion selection record.");
        if (selection.AnimationHeaderIndex != run.Index || selection.HavokMotionIndex != run.Index || selection.EmbeddedTmbIndex != run.Index
            || !string.Equals(selection.AnimationEvent, run.Event, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The generated PAP did not retain the selected Run source unit.");
        }

        var output = operation.Execution.GeneratedFiles.Single();
        if (!PathSafety.TryResolveInsideRoot(context.ModFolder, output, out var outputPath) || !File.Exists(outputPath))
            throw new InvalidOperationException("The generated Treadmill Run PAP is missing or unsafe.");
        var inspection = PapFileInspector.InspectFile(outputPath);
        var expectedTargetEvent = context.TargetInspections[operation.Execution.FinalMappings.First().Key].AnimationNames.Single();
        var events = PapEditor.ReadTimelineEventIdentifiers(outputPath);
        if (inspection.AnimationCount != 1
            || inspection.TimelineSectionSizes.Count != 1
            || inspection.HavokIndices.Single() != run.Index
            || !string.Equals(inspection.AnimationNames.Single(), expectedTargetEvent, StringComparison.OrdinalIgnoreCase)
            || events.Count == 0
            || events.Any(value => !string.Equals(value, expectedTargetEvent, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The generated PAP does not have the required one-section target structure.");
        }
        if (inspection.AnimationNames.Any(name => ExpectedSelections.Values.Any(expected => string.Equals(name, expected.Event, StringComparison.OrdinalIgnoreCase)))
            || events.Any(value => ExpectedSelections.Values.Any(expected => string.Equals(value, expected.Event, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("A stale treadmill selector event remained in the generated target PAP.");
        }

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(outputPath));
        if (!text.Contains("treadmill_run.avfx", StringComparison.OrdinalIgnoreCase)
            || text.Contains("treadmill_back.avfx", StringComparison.OrdinalIgnoreCase)
            || text.Contains("treadmill_sprint.avfx", StringComparison.OrdinalIgnoreCase)
            || text.Contains("treadmill_screen.avfx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The generated Run PAP did not retain only the selected PAP-local TMB effect references.");
        }

        if (!ReadHavokPayload(context.SharedPapPath).SequenceEqual(ReadHavokPayload(outputPath)))
            throw new InvalidOperationException("The generated PAP changed the source Havok payload.");

        return $"{output}; header {inspection.AnimationNames.Single()}; H{inspection.HavokIndices.Single()}; TMB 1; selected Run TMB effect treadmill_run.avfx retained; Style/Walk/Sprint external PAP-local effects absent; companion treadmill_screen.avfx not transferred.";
    }

    private static string VerifyFingerprint(RegressionContext context)
    {
        var result = RequireOperation(context).Execution.PapResults.Single();
        if (string.IsNullOrWhiteSpace(result.SourceMotionFingerprint))
            throw new InvalidOperationException("VFXEditor did not attest to selected Run motion fingerprint preservation.");
        return $"Run motion {result.SourceSelection?.HavokMotionIndex} fingerprint {result.SourceMotionFingerprint}; PASS.";
    }

    private static string VerifyRuntimeAndTrigger(RegressionContext context)
    {
        var operation = RequireOperation(context);
        var playerIndex = OnFramework(() => Plugin.ObjectTable.LocalPlayer?.ObjectIndex ?? -1);
        if (playerIndex < 0)
            throw new InvalidOperationException("The local player is unavailable for runtime redirect verification.");

        var setTemporary = new SetTemporaryModSettingsPlayer(Plugin.PluginInterface);
        var removeTemporary = new RemoveTemporaryModSettingsPlayer(Plugin.PluginInterface);
        var resolve = new ResolvePlayerPath(Plugin.PluginInterface);
        var selections = new Dictionary<string, IReadOnlyList<string>>
        {
            [DancyMetadataMutator.GroupName] = new[] { context.Plan.DisplayName },
        };
        var applied = false;
        try
        {
            var setResult = OnFramework(() => setTemporary.Invoke(playerIndex, context.ModDirectory, false, true, 9999, selections, "Dancy Treadmill selector regression", TemporarySettingKey).ToString());
            if (!string.Equals(setResult, "Success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Penumbra temporary setting returned {setResult}.");
            applied = true;

            foreach (var (gamePath, relativePath) in operation.Execution.FinalMappings)
            {
                if (!PathSafety.TryResolveInsideRoot(context.ModFolder, relativePath, out var expected))
                    throw new InvalidOperationException($"Generated target path is unsafe: {relativePath}");
                var actual = OnFramework(() => resolve.Invoke(gamePath));
                if (!EquivalentPath(expected, actual))
                    throw new InvalidOperationException($"{gamePath} resolved to {actual}, expected {expected}.");
            }

            var trigger = TriggerTargetThroughConduit(context.Target.Command);
            return $"{operation.Execution.FinalMappings.Count} target redirect(s) resolved to generated output; Conduit {trigger}.";
        }
        finally
        {
            if (applied)
                _ = OnFramework(() => removeTemporary.Invoke(playerIndex, context.ModDirectory, TemporarySettingKey).ToString());
        }
    }

    private static string VerifySourceIntegrity(RegressionContext context)
    {
        if (!string.Equals(HashFile(context.SharedPapPath), context.SourcePapHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The original shared Treadmill PAP changed during generation.");
        var after = JObject.Parse(File.ReadAllText(context.MetaPath));
        if (!JToken.DeepEquals(context.MetadataWithoutDancy, RemoveDancyMetadata(after)))
            throw new InvalidOperationException("Warrior of Lift metadata changed outside Dancy-owned group content.");
        return $"Shared source PAP SHA-256 unchanged: {context.SourcePapHash}; non-Dancy metadata unchanged.";
    }

    private static string Cleanup(RegressionContext context)
    {
        var operation = RequireOperation(context);
        var removal = DancyFileManager.RemoveDancyOverride(context.ModFolder, context.Plan.OverrideId);
        if (removal.RemovedOverrideCount != 1)
            throw new InvalidOperationException($"Cleanup removed {removal.RemovedOverrideCount} Dancy options instead of one test option.");
        var remaining = DancyFileManager.GetDancyOverrides(context.ModFolder);
        if (remaining.Any(option => string.Equals(option.Id, context.Plan.OverrideId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The Treadmill Run test option remained in metadata after cleanup.");
        if (operation.Execution.GeneratedFiles.Any(path => PathSafety.TryResolveInsideRoot(context.ModFolder, path, out var full) && File.Exists(full)))
            throw new InvalidOperationException("Treadmill Run cleanup left generated PAP output on disk.");
        var expectedIds = context.DancyOverridesBefore.Select(option => option.Id).OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        var actualIds = remaining.Select(option => option.Id).OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        if (!expectedIds.SequenceEqual(actualIds, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Treadmill Run cleanup changed an unrelated Dancy override.");
        var reload = new PenumbraIpcModReloader().Reload(context.ModDirectory, ModName);
        return reload.Succeeded
            ? $"Removed test ID {context.Plan.OverrideId}; generated output absent; unrelated Dancy options retained; reload {reload.Actual}."
            : $"Removed test ID {context.Plan.OverrideId}; generated output absent; unrelated Dancy options retained. Disk cleanup succeeded, but Penumbra refresh needs retry/reload: {reload.UserFacingOutcome}";
    }

    private static OverridePlan BuildPlan(string modIdentity, RemappableOption source, LuminaEmote target, IReadOnlyList<string> targetPaths)
    {
        var animation = source.LogicalAnimations.FirstOrDefault();
        return OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = modIdentity,
            SourceGroupName = source.GroupName,
            SourceOptionName = source.OptionName,
            SourceAnimationName = animation?.Name ?? SourceGroupName,
            SourceAnimationCommand = animation?.Command ?? string.Empty,
            TargetTimelineKey = target.PrimaryTimelineKey,
            TargetName = target.Name,
            TargetCommand = target.Command,
            Sources = source.LoopEntries.Select(entry => new OverridePlanSource(entry.GamePath, entry.ModdedPapPath)).ToList(),
            CompanionTimelines = source.CompanionTimelines,
            TargetGamePaths = targetPaths,
        });
    }

    private static string TriggerTargetThroughConduit(string targetCommand)
    {
        var started = SendConduitCommand(new JObject
        {
            ["flow"] = "dancy-treadmill-selector-regression",
            ["script"] = $"chat {targetCommand}",
        });
        var action = started.Value<string>("ActionResult") ?? string.Empty;
        if (!action.Contains("started inline flow", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Conduit refused the target trigger: {action}");

        Thread.Sleep(750);
        var observed = SendConduitCommand(new JObject
        {
            ["expression"] = "Conduit::Conduit.Plugin.P.Flows.Status()",
        });
        var flow = observed["Flow"] as JObject;
        var outcome = flow?.Value<string>("Outcome") ?? string.Empty;
        var succeeded = flow?.Value<bool?>("Succeeded") ?? false;
        if (!succeeded || !string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Conduit target flow did not complete: {flow?.ToString(Formatting.None) ?? "no flow result"}");
        return $"inline flow triggered {targetCommand} and completed";
    }

    private static JObject SendConduitCommand(JObject command)
    {
        var pluginConfigs = Directory.GetParent(Plugin.PluginInterface.ConfigDirectory.FullName)?.FullName
            ?? throw new InvalidOperationException("Dancy could not locate the plugin configuration directory.");
        var conduitDirectory = Path.Combine(pluginConfigs, "Conduit");
        if (!Directory.Exists(conduitDirectory))
            throw new DirectoryNotFoundException("Conduit configuration directory is unavailable.");

        var id = Guid.NewGuid().ToString("N");
        command["id"] = id;
        command["dump"] = true;
        var commandPath = Path.Combine(conduitDirectory, "ConduitCommand.json");
        var temporaryPath = Path.Combine(conduitDirectory, $"ConduitCommand.{id}.tmp");
        File.WriteAllText(temporaryPath, command.ToString(Formatting.None), new UTF8Encoding(false));
        File.Move(temporaryPath, commandPath, overwrite: true);

        var responsePath = Path.Combine(conduitDirectory, "ConduitResponse.json");
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            try
            {
                if (!File.Exists(responsePath))
                    continue;
                var response = JObject.Parse(File.ReadAllText(responsePath));
                if (string.Equals(response.Value<string>("Id"), id, StringComparison.OrdinalIgnoreCase))
                    return response;
            }
            catch (IOException)
            {
                // Conduit publishes responses atomically; a concurrent read can simply retry.
            }
            catch (JsonReaderException)
            {
                // As above, keep polling within the bounded request lifetime.
            }
        }

        throw new TimeoutException("Conduit did not return a correlated response for the target trigger.");
    }

    private static byte[] ReadHavokPayload(string path)
    {
        var bytes = PapFileInspector.ReadFileWithRetry(path);
        var inspection = PapFileInspector.Inspect(bytes);
        var payload = new byte[inspection.TimelineOffset - inspection.HavokOffset];
        Buffer.BlockCopy(bytes, inspection.HavokOffset, payload, 0, payload.Length);
        return payload;
    }

    private static JObject RemoveDancyMetadata(JObject metadata)
    {
        var copy = (JObject)metadata.DeepClone();
        if (copy["Groups"] is not JArray groups)
            return copy;
        foreach (var group in groups.OfType<JObject>().Where(DancyMetadataMutator.IsDancyGroup).ToList())
            group.Remove();
        return copy;
    }

    private static OverrideOperationResult RequireOperation(RegressionContext context)
        => context.Operation ?? throw new InvalidOperationException("The production create did not run.");

    private static string HashFile(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

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
            cases.Add(new DancySelfTestCase
            {
                TestName = name,
                Status = DancySelfTestStatus.Failed,
                Stage = stage,
                Expected = expected,
                Actual = exception.Message,
                FailureReason = exception.ToString(),
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
            });
            return false;
        }
    }

    private static DancySelfTestResult Complete(DateTimeOffset started, IReadOnlyList<DancySelfTestCase> cases)
        => new()
        {
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
        };

    private sealed class RegressionContext
    {
        public string ModDirectory { get; init; } = string.Empty;
        public string ModFolder { get; init; } = string.Empty;
        public string MetaPath { get; init; } = string.Empty;
        public string MetadataFormat { get; init; } = string.Empty;
        public JObject MetadataWithoutDancy { get; init; } = new();
        public IReadOnlyDictionary<string, RemappableOption> SourceOptions { get; init; } = new Dictionary<string, RemappableOption>();
        public RemappableOption RunSource { get; init; } = new();
        public string SharedPapPath { get; init; } = string.Empty;
        public string SourcePapHash { get; init; } = string.Empty;
        public LuminaEmote Target { get; init; } = new();
        public IReadOnlyList<string> TargetPaths { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, PapFileInspector.PapFileInspection> TargetInspections { get; init; } = new Dictionary<string, PapFileInspector.PapFileInspection>();
        public OverridePlan Plan { get; init; } = new();
        public IReadOnlyList<DancyOverrideInfo> DancyOverridesBefore { get; init; } = Array.Empty<DancyOverrideInfo>();
        public OverrideOperationResult? Operation { get; set; }
        public bool Created { get; set; }
    }
}
#endif
