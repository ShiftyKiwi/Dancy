#if DEBUG
using System;
using System.Collections.Concurrent;
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
    private const int TemporarySettingKey = -9277;

    private static readonly Scenario[] Scenarios =
    [
        new("Style", "/water"),
        new("Walk", "/water"),
        new("Run", "/water"),
        new("Sprint", "/water"),
        new("Walk", "/beesknees"),
    ];

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
        foreach (var scenario in Scenarios)
            cases.AddRange(RunScenario(scenario).Cases);
        return Complete(started, cases);
    }

    private static DancySelfTestResult RunScenario(Scenario scenario)
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

            if (!RunCase(cases, $"Locate Treadmill {scenario.OptionName} fixture", "baseline", "The installed Warrior of Lift selector is resolved from its actual option mappings without a parent-group assumption.", () =>
                {
                    context = CaptureContext(scenario);
                    return $"{context.ModDirectory}; group {SourceGroupName}; option {scenario.OptionName}; source {context.RunSource.LoopEntries.Count} logical loop path(s); shared PAP {context.SharedPapPath}; metadata {context.MetadataFormat}; source SHA-256 {context.SourcePapHash}.";
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

            if (!RunCase(cases, $"{scenario.OptionName} target resolution", "target", $"A current one-section {scenario.TargetCommand} target is available for the production route.", () => DescribeTarget(context)))
                return Complete(started, cases);

            if (!RunCase(cases, $"{scenario.OptionName} planning and preflight", "preflight", "The normal production plan retains the verified selected source bank for one-section targets.", () => VerifyPreflight(context)))
                return Complete(started, cases);

            if (!RunCase(cases, "Production create and reload", "create", "OverrideService writes only one Dancy-owned option and reloads the selected mod.", () => Create(context)))
                return Complete(started, cases);

            if (!RunCase(cases, $"Generated {scenario.OptionName} PAP", "pap", "The generated PAP retains the complete source bank and changes only the selected header and embedded timeline.", () => VerifyGeneratedPap(context)))
                return Complete(started, cases);

            if (!RunCase(cases, $"{scenario.OptionName} source motion fingerprint", "fingerprint", "VFXEditor fingerprints the selected source motion before and after generation identically.", () => VerifyFingerprint(context)))
                return Complete(started, cases);

            if (!RunCase(cases, $"{scenario.OptionName} redraw and runtime resolution", "runtime", "Temporary player-scoped selection redraws the local player, resolves a generated target PAP, and records its resource request before target dispatch.", () => VerifyRuntimeAndTrigger(context)))
                return Complete(started, cases);

            if (!RunCase(cases, "Source integrity", "integrity", "The original shared PAP and all non-Dancy metadata remain unchanged.", () => VerifySourceIntegrity(context)))
                return Complete(started, cases);

            return Complete(started, cases);
        }
        finally
        {
            if (context?.Created == true)
                RunCase(cases, $"{scenario.OptionName} cleanup and restoration", "cleanup", "Only the generated Treadmill test option and its unreferenced output are removed; unrelated Dancy options remain.", () => Cleanup(context));
        }
    }

    private static RegressionContext CaptureContext(Scenario scenario)
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

        var run = sourceOptions[scenario.OptionName];
        var runEntries = run.LoopEntries;
        if (runEntries.Count == 0)
            throw new InvalidOperationException($"The Treadmill {scenario.OptionName} option has no Loop source PAP paths.");
        var physicalPaps = runEntries.Select(entry => entry.ModdedPapPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (physicalPaps.Count != 1 || !PathSafety.TryResolveInsideRoot(modFolder, physicalPaps[0], out var sharedPapPath) || !File.Exists(sharedPapPath))
            throw new InvalidOperationException($"Treadmill {scenario.OptionName} must resolve to one existing shared physical PAP.");

        var target = OnFramework(() => EmoteLibrary.AllEmotes.FirstOrDefault(emote => string.Equals(emote.Command, scenario.TargetCommand, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException($"The current game data has no {scenario.TargetCommand} target.");
        var targetPaths = OnFramework(() => PapResolver.ResolvePapFiles(target.PrimaryTimelineKey)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList());
        if (targetPaths.Count == 0)
            throw new InvalidOperationException($"No current PAP paths resolved for {scenario.TargetCommand}.");
        var targetInspections = OnFramework(() => targetPaths.ToDictionary(path => path, PapEditor.InspectTargetPap, StringComparer.OrdinalIgnoreCase));
        if (targetInspections.Values.Any(inspection => inspection.AnimationCount != 1 || inspection.TimelineSectionSizes.Count != 1))
            throw new InvalidOperationException($"{scenario.TargetCommand} no longer resolves solely to normal one-section PAP targets.");

        var plan = BuildPlan(mod.Key, run, target, targetPaths, $"{scenario.OptionName} [Dancy redraw regression {scenario.TargetCommand.TrimStart('/')}]");
        if (!plan.IsValid)
            throw new InvalidOperationException(string.Join("; ", plan.Errors));
        var existing = DancyFileManager.GetDancyOverrides(modFolder);
        if (existing.Any(option => string.Equals(option.Id, plan.OverrideId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A matching Dancy Treadmill {scenario.OptionName} test option already exists. Dancy will not overwrite a user-owned matching override during validation.");

        return new RegressionContext
        {
            Scenario = scenario,
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
        var optionName = context.Scenario.OptionName;
        var copy = context.Plan.PapCopies.Single();
        var selection = SourceAnimationSelectionResolver.Resolve(context.ModFolder, copy).Selection
            ?? throw new InvalidOperationException($"The {optionName} source selector did not return a selection.");
        var source = PapFileInspector.InspectFile(context.SharedPapPath);
        var result = PapCompatibilityPreflight.Evaluate(source, selection, copy.TargetGamePaths.Select(path => new PapTargetInspection(path, context.TargetInspections[path])));
        if (!result.CanCreate || result.WriteStrategy != PapOverrideWriteStrategy.SelectorBankEventPatch)
            throw new InvalidOperationException($"The selector-backed {optionName} route was rejected: {result.Reason}");
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
        var optionName = context.Scenario.OptionName;
        var selected = ExpectedSelections[optionName];
        var result = operation.Execution.PapResults.Single();
        var selection = result.SourceSelection ?? throw new InvalidOperationException("The generated PAP did not retain its source-motion selection record.");
        if (selection.AnimationHeaderIndex != selected.Index || selection.HavokMotionIndex != selected.Index || selection.EmbeddedTmbIndex != selected.Index
            || !string.Equals(selection.AnimationEvent, selected.Event, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The generated PAP did not retain the selected {optionName} source unit.");
        }

        var sourceBytes = PapFileInspector.ReadFileWithRetry(context.SharedPapPath);
        var source = PapFileInspector.Inspect(sourceBytes);
        var sourceEmbedded = PapEditor.ReadEmbeddedTimelineEventIdentifiers(context.SharedPapPath);
        var details = new List<string>();
        foreach (var patch in operation.Execution.PapResults)
        {
            if (patch.WriteStrategy != PapOverrideWriteStrategy.SelectorBankEventPatch)
                throw new InvalidOperationException($"The {optionName} output did not use Dancy's selector-bank writer.");
            if (!operation.Execution.FinalMappings.TryGetValue(patch.TargetGamePath, out var output)
                || !PathSafety.TryResolveInsideRoot(context.ModFolder, output, out var outputPath)
                || !File.Exists(outputPath))
            {
                throw new InvalidOperationException($"The generated Treadmill {optionName} PAP is missing or unsafe.");
            }

            var inspection = PapFileInspector.InspectFile(outputPath);
            var embedded = PapEditor.ReadEmbeddedTimelineEventIdentifiers(outputPath);
            if (inspection.AnimationCount != source.AnimationCount
                || inspection.TimelineSectionSizes.Count != source.TimelineSectionSizes.Count
                || !inspection.HavokIndices.SequenceEqual(source.HavokIndices)
                || inspection.HavokIndices[selected.Index] != selected.Index
                || !string.Equals(inspection.AnimationNames[selected.Index], patch.EventIdentifier, StringComparison.OrdinalIgnoreCase)
                || inspection.AnimationNames.Count(name => string.Equals(name, patch.EventIdentifier, StringComparison.OrdinalIgnoreCase)) != 1
                || embedded.Count != sourceEmbedded.Count
                || !embedded[selected.Index].Contains(patch.EventIdentifier, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The generated {optionName} PAP did not preserve the source-bank structure around the selected target event.");
            }

            var outputBytes = PapFileInspector.ReadFileWithRetry(outputPath);
            for (var index = 0; index < source.AnimationCount; index++)
            {
                if (index == selected.Index)
                    continue;
                if (!HashRange(sourceBytes, source.AnimationHeaders[index].Offset, source.AnimationHeaders[index].Size)
                        .Equals(HashRange(outputBytes, inspection.AnimationHeaders[index].Offset, inspection.AnimationHeaders[index].Size), StringComparison.OrdinalIgnoreCase)
                    || !HashRange(sourceBytes, source.TimelineSections[index].Offset, source.TimelineSections[index].Size)
                        .Equals(HashRange(outputBytes, inspection.TimelineSections[index].Offset, inspection.TimelineSections[index].Size), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"The generated {optionName} PAP changed a non-selected source-bank section.");
                }
            }

            if (!ReadHavokPayload(context.SharedPapPath).SequenceEqual(ReadHavokPayload(outputPath)))
                throw new InvalidOperationException($"The generated {optionName} PAP changed the source Havok payload.");
            details.Add($"{output}; headers {inspection.AnimationCount}; H={string.Join(',', inspection.HavokIndices)}; TMB {inspection.TimelineSectionSizes.Count}; selected {optionName} event {patch.EventIdentifier}.");
        }

        return string.Join(" | ", details);
    }

    private static string VerifyFingerprint(RegressionContext context)
    {
        var result = RequireOperation(context).Execution.PapResults.Single();
        if (string.IsNullOrWhiteSpace(result.SourceMotionFingerprint))
            throw new InvalidOperationException($"VFXEditor did not attest to selected {context.Scenario.OptionName} motion fingerprint preservation.");
        return $"{context.Scenario.OptionName} motion {result.SourceSelection?.HavokMotionIndex} fingerprint {result.SourceMotionFingerprint}; PASS.";
    }

    private static string VerifyRuntimeAndTrigger(RegressionContext context)
    {
        var operation = RequireOperation(context);
        var player = OnFramework(() =>
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer
                ?? throw new InvalidOperationException("The local player is unavailable for runtime redirect verification.");
            return (Index: localPlayer.ObjectIndex, Address: localPlayer.Address);
        });
        var playerIndex = player.Index;

        var setTemporary = new SetTemporaryModSettingsPlayer(Plugin.PluginInterface);
        var removeTemporary = new RemoveTemporaryModSettingsPlayer(Plugin.PluginInterface);
        var resolve = new ResolvePlayerPath(Plugin.PluginInterface);
        var redrawn = new ConcurrentQueue<(nint Address, int Index)>();
        var resourcePaths = new ConcurrentQueue<(nint Address, string GamePath, string FullPath)>();
        using var redrawObserver = GameObjectRedrawn.Subscriber(Plugin.PluginInterface, (address, index) => redrawn.Enqueue((address, index)));
        using var resourceObserver = GameObjectResourcePathResolved.Subscriber(Plugin.PluginInterface, (address, gamePath, fullPath) => resourcePaths.Enqueue((address, gamePath, fullPath)));
        redrawObserver.Enable();
        resourceObserver.Enable();
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

            RequestPlayerRedraw(playerIndex);
            Thread.Sleep(2000);
            var redrawnAddresses = redrawn.Where(value => value.Index == playerIndex).Select(value => value.Address).ToHashSet();
            if (redrawnAddresses.Count == 0)
                throw new InvalidOperationException("Penumbra did not report a local-player redraw after Dancy applied the temporary test setting.");

            foreach (var (gamePath, relativePath) in operation.Execution.FinalMappings)
            {
                if (!PathSafety.TryResolveInsideRoot(context.ModFolder, relativePath, out var expected))
                    throw new InvalidOperationException($"Generated target path is unsafe: {relativePath}");
                var actual = OnFramework(() => resolve.Invoke(gamePath));
                if (!EquivalentPath(expected, actual))
                    throw new InvalidOperationException($"{gamePath} resolved to {actual}, expected {expected}.");
            }

            var trigger = TriggerTargetThroughConduit(context.Target.Command);
            Thread.Sleep(1000);
            var observedGeneratedPath = resourcePaths.Any(value => redrawnAddresses.Contains(value.Address)
                && operation.Execution.FinalMappings.TryGetValue(value.GamePath, out var relativePath)
                && PathSafety.TryResolveInsideRoot(context.ModFolder, relativePath, out var expected)
                && EquivalentPath(expected, value.FullPath));
            if (!observedGeneratedPath)
                throw new InvalidOperationException("Penumbra did not report the local player consuming a generated target PAP after redraw and target dispatch.");
            return $"{operation.Execution.FinalMappings.Count} target redirect(s) resolved to generated output after redraw; local generated PAP resource request observed; Conduit {trigger}.";
        }
        finally
        {
            if (applied)
            {
                _ = OnFramework(() => removeTemporary.Invoke(playerIndex, context.ModDirectory, TemporarySettingKey).ToString());
                RequestPlayerRedraw(playerIndex);
            }
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
            throw new InvalidOperationException($"The Treadmill {context.Scenario.OptionName} test option remained in metadata after cleanup.");
        if (operation.Execution.GeneratedFiles.Any(path => PathSafety.TryResolveInsideRoot(context.ModFolder, path, out var full) && File.Exists(full)))
            throw new InvalidOperationException($"Treadmill {context.Scenario.OptionName} cleanup left generated PAP output on disk.");
        var expectedIds = context.DancyOverridesBefore.Select(option => option.Id).OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        var actualIds = remaining.Select(option => option.Id).OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        if (!expectedIds.SequenceEqual(actualIds, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Treadmill {context.Scenario.OptionName} cleanup changed an unrelated Dancy override.");
        var reload = new PenumbraIpcModReloader().Reload(context.ModDirectory, ModName);
        return reload.Succeeded
            ? $"Removed test ID {context.Plan.OverrideId}; generated output absent; unrelated Dancy options retained; reload {reload.Actual}."
            : $"Removed test ID {context.Plan.OverrideId}; generated output absent; unrelated Dancy options retained. Disk cleanup succeeded, but Penumbra refresh needs retry/reload: {reload.UserFacingOutcome}";
    }

    private static OverridePlan BuildPlan(string modIdentity, RemappableOption source, LuminaEmote target, IReadOnlyList<string> targetPaths, string? planOptionName = null)
    {
        var animation = source.LogicalAnimations.FirstOrDefault();
        return OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = modIdentity,
            SourceGroupName = source.GroupName,
            SourceOptionName = planOptionName ?? source.OptionName,
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

    private static string HashRange(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
            throw new InvalidDataException("PAP integrity range is invalid.");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(offset, length)));
    }

    private static void RequestPlayerRedraw(int playerIndex)
        => OnFramework(() =>
        {
            new RedrawObject(Plugin.PluginInterface).Invoke(playerIndex);
            return true;
        });

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

    private sealed record Scenario(string OptionName, string TargetCommand);

    private sealed class RegressionContext
    {
        public Scenario Scenario { get; init; } = new(string.Empty, string.Empty);
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
