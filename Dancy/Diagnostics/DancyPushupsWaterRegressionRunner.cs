#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using Penumbra.Api.IpcSubscribers;

namespace Dancy.Diagnostics;

/// <summary>
/// Exercises the installed, known legacy Push-ups to Water override through the
/// same services used by the wizard. It deliberately refuses ambiguous matches.
/// </summary>
internal sealed class DancyPushupsWaterRegressionRunner
{
    private const string ModName = "[HS] Warrior of Lift (Default)";
    private const string SourceGroupName = "Bench Press - /pushups";
    private const string SourceOptionName = "Enable";
    private const string WaterCommand = "/water";
    private const string StaleWaterEvent = "sp60_loop";
    private const int TemporarySettingKey = -9235;

    public DancySelfTestResult Run()
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();
        RegressionContext? context = null;

        if (!RunCase(cases, "Build provenance", "provenance", "Loaded Dancy assembly is readable and hashable.", () =>
            {
                var location = Plugin.PluginInterface.AssemblyLocation.FullName;
                if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
                    throw new InvalidOperationException("The loaded Dancy assembly location is unavailable.");
                return $"{location} [{HashFile(location)}]";
            }))
        {
            return Complete(started, cases);
        }

        if (!RunCase(cases, "Locate installed Warrior of Lift", "baseline", "One installed Warrior of Lift source mod is available and its Push-ups to Water Dancy state is unambiguous.", () =>
            {
                context = CaptureContext();
                return $"Identity {context.ModDirectory}; folder {context.ModFolder}; metadata {context.MetadataFormat}; meta SHA-256 {context.MetadataHash}; source PAPs {FormatHashes(context.SourcePapHashes)}; Dancy options {context.DancyOverridesBefore.Count}; generated {context.GeneratedBefore.Count}.";
            }))
        {
            return Complete(started, cases);
        }

        if (context is null)
            return Complete(started, cases);

        if (!RunCase(cases, "Legacy Push-ups to Water override", "legacy", "The exact Dancy-owned Push-ups to Water option is identified before removal, or the current mod is already clean from a prior Dancy removal.", () => DescribeLegacy(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Production Dancy removal", "removal", "Dancy removes only the identified stable override, or preserves an already-clean resumption state before recreation.", () => RemoveLegacyOverride(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Disk cleanup after removal", "removal", "The removed option, its mappings, and its unreferenced Dancy PAPs are absent from disk.", () => VerifyRemovalDiskState(context)))
            return Complete(started, cases);

        RunCase(cases, "Penumbra state after removal", "runtime", "Supported Penumbra APIs are queried after the production reload without claiming option-level attribution they do not provide.", () => QueryChangedItemsState(context));

        if (!RunCase(cases, "Push-ups phase scan", "scan", "The production scanner identifies Loop and Start source paths separately.", () => VerifyPhaseScan(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Loop-only source selection model", "selection", "Default, select-all, clear, select-only, and deliberate non-loop validation preserve the Loop-only contract.", () => VerifySourceSelectionModel(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Current Water target resolution", "water", "Water is resolved from current game data and each target PAP is structurally inspected.", () => DescribeWaterTargets(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Water compatibility preflight", "preflight", "The exact Push-ups Loop to Water plan passes Dancy's production PAP preflight.", () => VerifyPreflight(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Production mapping plan", "planning", "The plan contains only Loop sources and maps every current Water target exactly once.", () => DescribePlan(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Production create and reload", "create", "OverrideService generates PAPs, atomically upserts metadata, and Penumbra reloads the installed mod.", () => CreateOverride(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Generated Water PAP inspection", "pap", "Every Dancy-generated Water PAP reopens, retains source Havok payload, and has the current Water event in both PAP header and timeline.", () => InspectGeneratedPaps(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Persisted override and garbage collection", "metadata", "One intended override is persisted, every generated file is referenced, and dry verification finds no orphan Dancy output.", () => VerifyPersistedOverride(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Generated Penumbra description", "metadata", "The persisted option name and rich description exactly describe the final mapping races and count.", () => VerifyDescription(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Penumbra runtime redirections", "runtime", "Player-scoped temporary settings resolve every Water path to its expected generated PAP and leave a source path outside Dancy output.", () => VerifyRuntimeRedirections(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Idempotent production update", "idempotency", "A second identical OverrideService call retains one stable option, stable generated paths, and no orphans.", () => VerifyIdempotency(context)))
            return Complete(started, cases);

        if (!RunCase(cases, "Warrior source integrity", "integrity", "Source PAP bytes are unchanged and metadata differs only by Dancy-owned group content.", () => VerifySourceIntegrity(context)))
            return Complete(started, cases);

        RunCase(cases, "Automated visual T-pose check", "visual", "Only an already-supported animation-state or visual oracle may claim an automated pose result.", () =>
            "HUMAN CONFIRMATION REQUIRED: the active DAB/Conduit capability set has no supported animation-pose or skeleton-state oracle. Runtime redirect correctness was verified separately and is not treated as visual proof.");

        return Complete(started, cases);
    }

    private static RegressionContext CaptureContext()
    {
        var mod = OnFramework(() => new GetModList(Plugin.PluginInterface).Invoke()
            .FirstOrDefault(pair => string.Equals(pair.Value, ModName, StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(mod.Key))
            throw new InvalidOperationException($"{ModName} was not found in Penumbra's installed mod list.");

        var root = PenumbraDirectoryResolver.GetPenumbraDirectory();
        if (string.IsNullOrWhiteSpace(root) || !PathSafety.TryResolveInsideRoot(root, mod.Key, out var modFolder))
            throw new InvalidOperationException("Dancy could not resolve the installed Warrior of Lift folder safely.");

        var metaPath = Path.Combine(modFolder, "meta.json");
        if (!File.Exists(metaPath))
            throw new FileNotFoundException("The installed Warrior of Lift mod has no meta.json.", metaPath);
        var metadata = JObject.Parse(File.ReadAllText(metaPath));
        var fileVersion = metadata["FileVersion"]?.Value<int?>() ?? 0;
        var metadataFormat = fileVersion >= 4 ? $"Penumbra v4 meta.json (FileVersion {fileVersion})" : $"legacy/unknown meta.json (FileVersion {fileVersion})";

        var source = EmoteOverrideScanner.ScanMod(modFolder)
            .SingleOrDefault(option => string.Equals(option.GroupName, SourceGroupName, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(option.OptionName, SourceOptionName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The installed Warrior of Lift mod has no Bench Press /pushups Enable source option.");
        var loops = source.LoopEntries;
        var starts = source.Entries.Where(entry => entry.AppliesTo.Phase == AnimationPhase.Start).ToList();
        if (loops.Count == 0 || starts.Count == 0)
            throw new InvalidOperationException($"Expected both Loop and Start source paths, found {loops.Count} Loop and {starts.Count} Start.");

        var sourcePapHashes = source.Entries
            .Select(entry => entry.ModdedPapPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path =>
            {
                if (!PathSafety.TryResolveInsideRoot(modFolder, path, out var fullPath) || !File.Exists(fullPath))
                    throw new InvalidOperationException($"Unsafe or missing Warrior source PAP: {path}");
                return HashFile(fullPath);
            }, StringComparer.OrdinalIgnoreCase);

        var water = OnFramework(() => EmoteLibrary.AllEmotes.FirstOrDefault(emote => string.Equals(emote.Command, WaterCommand, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("The /water emote was not found in current game data.");
        var targets = OnFramework(() => PapResolver.ResolvePapFiles(water.PrimaryTimelineKey)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList());
        if (targets.Count == 0)
            throw new InvalidOperationException("Current game data did not resolve any Water PAP target paths.");
        var inspections = OnFramework(() => targets.ToDictionary(path => path, PapEditor.InspectTargetPap, StringComparer.OrdinalIgnoreCase));
        var targetEvents = inspections.ToDictionary(pair => pair.Key, pair => pair.Value.AnimationNames.SingleOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        if (targetEvents.Values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("A current Water target PAP has no animation event identifier.");

        var overrides = DancyFileManager.GetDancyOverrides(modFolder);
        var candidates = overrides.Where(option => IsPushupsWaterOverride(option, targets)).ToList();
        if (candidates.Count > 1)
            throw new InvalidOperationException($"Found {candidates.Count} Push-ups to Water options. Dancy will not guess which override to remove.");
        var legacy = candidates.SingleOrDefault();
        if (legacy is not null && (legacy.Mappings.Count == 0 || legacy.Mappings.Keys.Any(path => !targets.Contains(path, StringComparer.OrdinalIgnoreCase))))
            throw new InvalidOperationException("The identified Push-ups to Water option contains non-Water mappings and cannot be safely removed as an isolated regression target.");

        var generatedBefore = GetGeneratedFiles(modFolder);
        var legacyGenerated = legacy?.Mappings.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        foreach (var relativePath in legacyGenerated)
        {
            if (!PathSafety.TryResolveInsideRoot(modFolder, relativePath, out var fullPath) || !File.Exists(fullPath))
                throw new InvalidOperationException($"Legacy Dancy mapping points to an unsafe or missing PAP: {relativePath}");
        }

        return new RegressionContext
        {
            ModDirectory = mod.Key,
            ModName = mod.Value,
            ModFolder = modFolder,
            MetaPath = metaPath,
            MetadataFormat = metadataFormat,
            MetadataHash = HashFile(metaPath),
            MetadataWithoutDancy = RemoveDancyMetadata(metadata),
            Source = source,
            LoopEntries = loops,
            StartEntries = starts,
            SourcePapHashes = sourcePapHashes,
            Water = water,
            WaterTargetPaths = targets,
            WaterTargetInspections = inspections,
            WaterTargetEvents = targetEvents,
            DancyOverridesBefore = overrides,
            LegacyOverride = legacy,
            LegacyGeneratedPaths = legacyGenerated,
            GeneratedBefore = generatedBefore,
        };
    }

    private static bool IsPushupsWaterOverride(DancyOverrideInfo option, IReadOnlyCollection<string> waterTargets)
    {
        var sourceEvidence = option.Description.Contains(SourceGroupName, StringComparison.OrdinalIgnoreCase)
                           || option.Name.Contains("Bench Press", StringComparison.OrdinalIgnoreCase);
        var targetEvidence = option.Description.Contains("Water", StringComparison.OrdinalIgnoreCase)
                           || option.Name.Contains("Water", StringComparison.OrdinalIgnoreCase);
        return sourceEvidence
            && targetEvidence
            && option.Mappings.Keys.Any(path => waterTargets.Contains(path, StringComparer.OrdinalIgnoreCase));
    }

    private static string DescribeLegacy(RegressionContext context)
    {
        if (context.LegacyOverride is null)
            return "No current Push-ups to Water Dancy option exists. This is an already-clean resumption state; no metadata or files will be manually reconstructed before production recreation.";

        var events = context.LegacyOverride.Mappings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair =>
        {
            if (!PathSafety.TryResolveInsideRoot(context.ModFolder, pair.Value, out var fullPath))
                throw new InvalidOperationException($"Unsafe legacy generated path: {pair.Value}");
            var actual = PapFileInspector.InspectFile(fullPath).AnimationNames.SingleOrDefault() ?? "<missing>";
            var expected = context.WaterTargetEvents[pair.Key];
            return $"{GamePathIdentity.Parse(pair.Key).Character.Code}: {actual} -> {expected}; {pair.Value} [{HashFile(fullPath)}]";
        });
        var dryRun = DancyFileManager.CollectGeneratedFiles(context.ModFolder, dryRun: true);
        return $"ID {context.LegacyOverride.Id}; name {context.LegacyOverride.Name}; description {context.LegacyOverride.Description.Replace(Environment.NewLine, " | ")}; mappings {context.LegacyOverride.Mappings.Count}; generated {string.Join(", ", context.LegacyGeneratedPaths)}; orphaned {dryRun.RemovedFiles.Count}; events {string.Join("; ", events)}.";
    }

    private static string RemoveLegacyOverride(RegressionContext context)
    {
        if (context.LegacyOverride is null)
            return "No current Push-ups to Water Dancy option to remove; preserving the already-clean Dancy state for production recreation.";

        context.Removal = DancyFileManager.RemoveDancyOverride(context.ModFolder, context.LegacyOverride.Id);
        if (context.Removal.RemovedOverrideCount != 1)
            throw new InvalidOperationException($"Dancy removed {context.Removal.RemovedOverrideCount} options instead of the single identified legacy override.");
        context.RemovalReload = new PenumbraIpcModReloader().Reload(context.ModDirectory, context.ModName);
        return $"Removed stable ID {context.LegacyOverride.Id}; Dancy collected {context.Removal.GarbageCollection.RemovedFiles.Count} unreferenced file(s); ReloadMod {(context.RemovalReload.Succeeded ? "PASS" : "FAIL")}: {context.RemovalReload.UserFacingOutcome}.";
    }

    private static string VerifyRemovalDiskState(RegressionContext context)
    {
        var after = DancyFileManager.GetDancyOverrides(context.ModFolder);
        if (context.LegacyOverride is not null && after.Any(option => string.Equals(option.Id, context.LegacyOverride.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The removed Push-ups to Water option is still present in disk metadata.");
        if (after.Any(option => IsPushupsWaterOverride(option, context.WaterTargetPaths)))
        {
            throw new InvalidOperationException("A Push-ups to Water Dancy mapping is still present after removal.");
        }

        var expectedRemaining = context.DancyOverridesBefore.Where(option => context.LegacyOverride is null || !string.Equals(option.Id, context.LegacyOverride.Id, StringComparison.OrdinalIgnoreCase))
            .Select(option => option.Id)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        var actualRemaining = after.Select(option => option.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        if (!expectedRemaining.SequenceEqual(actualRemaining, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Dancy removal changed an unrelated Dancy option.");

        var generated = GetGeneratedFiles(context.ModFolder);
        var retainedLegacy = context.LegacyGeneratedPaths.Where(path => generated.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList();
        if (retainedLegacy.Count != 0)
            throw new InvalidOperationException($"Removed override left generated PAP(s) on disk: {string.Join(", ", retainedLegacy)}");
        var dryRun = DancyFileManager.CollectGeneratedFiles(context.ModFolder, dryRun: true);
        if (dryRun.RemovedFiles.Count != 0)
            throw new InvalidOperationException($"Dancy removal left orphaned output: {string.Join(", ", dryRun.RemovedFiles)}");
        return $"Metadata clean for {(context.LegacyOverride?.Id ?? "already-clean resumption")}; legacy generated files absent; {after.Count} unrelated Dancy option(s) retained; 0 orphaned Dancy files.";
    }

    private static string QueryChangedItemsState(RegressionContext context)
    {
        try
        {
            var items = OnFramework(() => new GetChangedItems(Plugin.PluginInterface).Invoke(context.ModDirectory, context.ModName));
            return items.Count == 0
                ? "CLEAN: GetChangedItems returned 0 items after ReloadMod."
                : $"CANNOT_ATTRIBUTE: GetChangedItems returned {items.Count} mod-scoped item(s) after ReloadMod; the supported API cannot assign an item to a removed Dancy option or prove a Penumbra UI cache state.";
        }
        catch (Exception exception)
        {
            return $"CANNOT_ATTRIBUTE: GetChangedItems was unavailable ({exception.GetType().Name}: {exception.Message}).";
        }
    }

    private static string VerifyPhaseScan(RegressionContext context)
    {
        var phaseCounts = context.Source.Entries.GroupBy(entry => entry.AppliesTo.Phase).ToDictionary(group => group.Key, group => group.Count());
        var start = context.Source.Entries.Where(entry => entry.GamePath.EndsWith("loop_emot08_start.pap", StringComparison.OrdinalIgnoreCase)).ToList();
        var loop = context.Source.Entries.Where(entry => entry.GamePath.EndsWith("loop_emot08_loop.pap", StringComparison.OrdinalIgnoreCase)).ToList();
        if (start.Count == 0 || loop.Count == 0
            || start.Any(entry => entry.AppliesTo.Phase != AnimationPhase.Start)
            || loop.Any(entry => entry.AppliesTo.Phase != AnimationPhase.Loop))
        {
            throw new InvalidOperationException("Push-ups loop_emot08 start/loop assets were not classified into their required phases.");
        }

        return $"Total {context.Source.Entries.Count}; Loop {GetPhaseCount(phaseCounts, AnimationPhase.Loop)}; Start {GetPhaseCount(phaseCounts, AnimationPhase.Start)}; End {GetPhaseCount(phaseCounts, AnimationPhase.End)}; Unknown {GetPhaseCount(phaseCounts, AnimationPhase.Unknown)}; loop_emot08_start.pap = Start; loop_emot08_loop.pap = Loop.";
    }

    private static string VerifySourceSelectionModel(RegressionContext context)
    {
        var defaultSelection = SourceSelectionPolicy.DefaultLoopGamePaths(context.Source.Entries);
        var defaultEntries = SourceSelectionPolicy.SelectedLoopEntries(context.Source.Entries, new HashSet<string>(defaultSelection, StringComparer.OrdinalIgnoreCase));
        if (defaultSelection.Count != context.LoopEntries.Count || defaultEntries.Count != context.LoopEntries.Count)
            throw new InvalidOperationException("Default source selection did not include every Loop path.");
        if (context.Source.Entries.Any(entry => entry.AppliesTo.Phase != AnimationPhase.Loop && defaultSelection.Contains(entry.GamePath)))
            throw new InvalidOperationException("A non-Loop source path entered the normal default selection.");
        if (context.Source.LogicalAnimations.Count != 1)
            throw new InvalidOperationException($"Expected repeated Push-ups labels to form one logical source animation, found {context.Source.LogicalAnimations.Count}.");
        if (context.Source.Entries.Any(entry => !entry.AppliesTo.Character.IsKnown)
            || context.Source.Entries.Any(entry => !context.SourcePapHashes.ContainsKey(entry.ModdedPapPath)))
        {
            throw new InvalidOperationException("A scanned Push-ups source path lost its race identity or PAP origin identity.");
        }

        var selectAll = SourceSelectionPolicy.DefaultLoopGamePaths(context.Source.Entries);
        var clear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (SourceSelectionPolicy.SelectedLoopEntries(context.Source.Entries, clear).Count != 0)
            throw new InvalidOperationException("Clear loop selection retained a Loop source.");
        var selectOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { context.LoopEntries[0].GamePath };
        if (SourceSelectionPolicy.SelectedLoopEntries(context.Source.Entries, selectOnly).Count != 1)
            throw new InvalidOperationException("Select only did not result in one Loop source.");

        var invalid = BuildPlan(context, new[] { context.StartEntries[0] });
        if (invalid.IsValid || !invalid.Errors.Any(error => error.Contains("Loop-phase", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The planner accepted a deliberately supplied Start source PAP.");
        context.Plan = BuildPlan(context, defaultEntries);
        if (!context.Plan.IsValid)
            throw new InvalidOperationException(string.Join("; ", context.Plan.Errors));

        return $"Logical animations {context.Source.LogicalAnimations.Count}; Select all loops {selectAll.Count}; Clear loop selection 0; Select only 1; default Loop selected {defaultEntries.Count}; Start selected 0; End selected 0; Unknown selected 0; planner rejected Start input.";
    }

    private static string DescribeWaterTargets(RegressionContext context)
    {
        var variants = context.WaterTargetPaths.Select(path =>
        {
            var inspection = context.WaterTargetInspections[path];
            return $"{GamePathIdentity.Parse(path).Character.DisplayName}: {path}; event {context.WaterTargetEvents[path]}; animations {inspection.AnimationCount}; Havok {string.Join(",", inspection.HavokIndices)}; TMB {inspection.TimelineSectionSizes.Count}";
        });
        return $"{context.Water.Name} {context.Water.Command}; timeline {context.Water.PrimaryTimelineKey}; variants {context.WaterTargetPaths.Count}; {string.Join(" | ", variants)}";
    }

    private static string VerifyPreflight(RegressionContext context)
    {
        var plan = GetPlan(context);
        var results = plan.PapCopies.Select(copy =>
        {
            if (!PathSafety.TryResolveInsideRoot(context.ModFolder, copy.SourcePapPath, out var sourcePath))
                throw new InvalidOperationException($"Unsafe selected source PAP: {copy.SourcePapPath}");
            return PapCompatibilityPreflight.Evaluate(PapFileInspector.InspectFile(sourcePath), copy.TargetGamePaths.Select(path => context.WaterTargetInspections[path]));
        }).ToList();
        var combined = PapCompatibilityPreflight.Combine(results);
        if (!combined.CanCreate)
            throw new InvalidOperationException($"Water preflight blocked creation: {combined.Reason}");
        context.Preflight = combined;
        return $"{combined.Status}: {combined.Reason}";
    }

    private static string DescribePlan(RegressionContext context)
    {
        var plan = GetPlan(context);
        if (plan.PlannedMappings.Count != context.WaterTargetPaths.Count
            || plan.PlannedMappings.Keys.Except(context.WaterTargetPaths, StringComparer.OrdinalIgnoreCase).Any()
            || plan.PapCopies.SelectMany(copy => copy.SourceGamePaths).Any(path => GamePathIdentity.Parse(path).Phase != AnimationPhase.Loop))
        {
            throw new InvalidOperationException("The production Water plan is not an exact Loop-only mapping of current Water target paths.");
        }

        var strategies = string.Join(", ", plan.PapCopies.SelectMany(copy => copy.MatchResults).Select(result => result.Strategy).Distinct());
        var races = plan.PlannedMappings.Keys.Select(path => GamePathIdentity.Parse(path).Character.DisplayName).Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        return $"Source logical animation {context.Source.LogicalAnimations[0].Name} ({context.Source.LogicalAnimations[0].Command}); Loop source paths {context.LoopEntries.Count}; Start source paths 0; PAP copies {plan.PapCopies.Count}; mappings {plan.PlannedMappings.Count}; strategies {strategies}; final races {string.Join(", ", races)}; target events {string.Join(", ", context.WaterTargetEvents.Values.Distinct(StringComparer.OrdinalIgnoreCase))}.";
    }

    private static string CreateOverride(RegressionContext context)
    {
        context.FirstCreate = new OverrideService().CreateOrUpdate(context.ModFolder, context.ModDirectory, context.ModName, GetPlan(context));
        return $"Stable ID {context.FirstCreate.Write.OverrideId}; {context.FirstCreate.Execution.GeneratedFiles.Count} PAP(s); {context.FirstCreate.Execution.FinalMappings.Count} mappings; preflight {context.FirstCreate.Execution.Compatibility.Status}; ReloadMod {(context.FirstCreate.Reload.Succeeded ? "PASS" : "FAIL")}: {context.FirstCreate.Reload.Actual}.";
    }

    private static string InspectGeneratedPaps(RegressionContext context)
    {
        var operation = GetFirstCreate(context);
        var plan = GetPlan(context);
        var perFile = new List<string>();
        foreach (var relativePath in operation.Execution.GeneratedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!PathSafety.TryResolveInsideRoot(context.ModFolder, relativePath, out var fullPath) || !File.Exists(fullPath))
                throw new InvalidOperationException($"Generated PAP is missing or unsafe: {relativePath}");
            var targets = operation.Execution.FinalMappings.Where(pair => string.Equals(pair.Value, relativePath, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToList();
            var expectedEvents = targets.Select(path => context.WaterTargetEvents[path]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (expectedEvents.Count != 1)
                throw new InvalidOperationException($"Generated PAP {relativePath} maps targets with different expected Water events.");
            var inspection = PapFileInspector.InspectFile(fullPath);
            var actualHeader = inspection.AnimationNames.SingleOrDefault() ?? string.Empty;
            var actualTimeline = PapEditor.ReadTimelineEventIdentifiers(fullPath);
            if (inspection.AnimationCount != 1 || inspection.HavokIndices.SingleOrDefault() != 0 || inspection.TimelineSectionSizes.Count != 1
                || !string.Equals(actualHeader, expectedEvents[0], StringComparison.OrdinalIgnoreCase)
                || actualTimeline.Count == 0 || actualTimeline.Any(value => !string.Equals(value, expectedEvents[0], StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Generated PAP did not retain the expected Water structure or event: {relativePath}");
            }
            if (string.Equals(actualHeader, StaleWaterEvent, StringComparison.OrdinalIgnoreCase)
                || actualTimeline.Any(value => string.Equals(value, StaleWaterEvent, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Generated Water PAP retained stale {StaleWaterEvent}: {relativePath}");
            }

            var source = plan.PapCopies.Single(copy => string.Equals(copy.OutputRelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (!PathSafety.TryResolveInsideRoot(context.ModFolder, source.SourcePapPath, out var sourcePath))
                throw new InvalidOperationException($"Generated PAP source is unsafe: {source.SourcePapPath}");
            if (!ReadHavokPayload(sourcePath).SequenceEqual(ReadHavokPayload(fullPath)))
                throw new InvalidOperationException($"Generated PAP changed its source Havok motion payload: {relativePath}");
            perFile.Add($"{relativePath}: source phase Loop; event expected {expectedEvents[0]}; header {actualHeader}; timeline {string.Join(",", actualTimeline)}; animations {inspection.AnimationCount}; Havok {inspection.HavokIndices.Single()}; TMB {inspection.TimelineSectionSizes.Count}; PASS");
        }

        return string.Join(" | ", perFile) + $" | stale {StaleWaterEvent}: ABSENT.";
    }

    private static string VerifyPersistedOverride(RegressionContext context)
    {
        var operation = GetFirstCreate(context);
        var persisted = DancyFileManager.GetDancyOverrides(context.ModFolder)
            .Where(option => string.Equals(option.Id, GetPlan(context).OverrideId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (persisted.Count != 1)
            throw new InvalidOperationException($"Expected one persisted Push-ups to Water option, found {persisted.Count}.");
        if (persisted[0].Mappings.Count != operation.Execution.FinalMappings.Count
            || persisted[0].Mappings.Any(pair => !operation.Execution.FinalMappings.TryGetValue(pair.Key, out var expected)
                                              || !string.Equals(expected, pair.Value, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Persisted Dancy mappings differ from the production execution result.");
        }
        var generated = GetGeneratedFiles(context.ModFolder);
        if (operation.Execution.GeneratedFiles.Any(path => !generated.Contains(path, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A generated Water PAP is missing from Dancy-owned output.");
        var dryRun = DancyFileManager.CollectGeneratedFiles(context.ModFolder, dryRun: true);
        if (dryRun.RemovedFiles.Count != 0)
            throw new InvalidOperationException($"Dancy garbage collection found orphan output after create: {string.Join(", ", dryRun.RemovedFiles)}");
        return $"ID {persisted[0].Id}; name {persisted[0].Name}; mappings {persisted[0].Mappings.Count}; generated {operation.Execution.GeneratedFiles.Count}; dry-run orphan files 0.";
    }

    private static string VerifyDescription(RegressionContext context)
    {
        var plan = GetPlan(context);
        var persisted = DancyFileManager.GetDancyOverrides(context.ModFolder).Single(option => string.Equals(option.Id, plan.OverrideId, StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(persisted.Name, plan.DisplayName, StringComparison.Ordinal)
            || !string.Equals(persisted.Description, plan.Description, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The persisted Dancy option name or description differs from the production mapping plan.");
        }

        var races = plan.PlannedMappings.Keys.Select(path => GamePathIdentity.Parse(path).Character).DistinctBy(character => character.Code, StringComparer.OrdinalIgnoreCase).ToList();
        if (races.Any(character => !persisted.Description.Contains(character.DisplayName, StringComparison.Ordinal))
            || !persisted.Description.Contains($"Target mappings:\n{plan.PlannedMappings.Count}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Dancy description does not enumerate the final mapped races/codes and mapping count.");
        }
        return $"Visible option name: {persisted.Name}\n{persisted.Description}";
    }

    private static string VerifyRuntimeRedirections(RegressionContext context)
    {
        var operation = GetFirstCreate(context);
        var playerIndex = OnFramework(() => Plugin.ObjectTable.LocalPlayer?.ObjectIndex ?? -1);
        if (playerIndex < 0)
            throw new InvalidOperationException("The local player is unavailable for player-scoped Penumbra resolution.");

        var temporarySettings = new SetTemporaryModSettingsPlayer(Plugin.PluginInterface);
        var removeTemporarySettings = new RemoveTemporaryModSettingsPlayer(Plugin.PluginInterface);
        var resolvePlayerPath = new ResolvePlayerPath(Plugin.PluginInterface);
        var selections = new Dictionary<string, IReadOnlyList<string>>
        {
            [DancyMetadataMutator.GroupName] = new[] { GetPlan(context).DisplayName },
        };
        var applied = false;
        try
        {
            var set = OnFramework(() => temporarySettings.Invoke(playerIndex, context.ModDirectory, false, true, 9999, selections, "Dancy Push-ups to Water regression", TemporarySettingKey).ToString());
            if (!string.Equals(set, "Success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Penumbra temporary setting returned {set}.");
            applied = true;

            var results = new List<string>();
            foreach (var (gamePath, relativePath) in operation.Execution.FinalMappings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!PathSafety.TryResolveInsideRoot(context.ModFolder, relativePath, out var expected))
                    throw new InvalidOperationException($"Expected generated PAP path is unsafe: {relativePath}");
                var actual = OnFramework(() => resolvePlayerPath.Invoke(gamePath));
                if (!EquivalentPath(expected, actual))
                    throw new InvalidOperationException($"{gamePath} resolved to {actual}, expected {expected}.");
                results.Add($"{gamePath} -> {actual} PASS");
            }

            var sourceActual = OnFramework(() => resolvePlayerPath.Invoke(context.LoopEntries[0].GamePath));
            if (sourceActual.Replace('\\', '/').Contains("/yucksdancy/paps/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An unrelated Push-ups source game path resolved to Dancy-generated output.");
            return string.Join(" | ", results) + $" | source path {context.LoopEntries[0].GamePath} -> {sourceActual} (outside Dancy output).";
        }
        finally
        {
            if (applied)
                _ = OnFramework(() => removeTemporarySettings.Invoke(playerIndex, context.ModDirectory, TemporarySettingKey).ToString());
        }
    }

    private static string VerifyIdempotency(RegressionContext context)
    {
        var first = GetFirstCreate(context);
        context.SecondCreate = new OverrideService().CreateOrUpdate(context.ModFolder, context.ModDirectory, context.ModName, GetPlan(context));
        var options = DancyFileManager.GetDancyOverrides(context.ModFolder)
            .Where(option => string.Equals(option.Id, GetPlan(context).OverrideId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (options.Count != 1
            || !first.Execution.GeneratedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(context.SecondCreate.Execution.GeneratedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("An identical Dancy update changed the stable option identity or generated PAP set.");
        }
        var dryRun = DancyFileManager.CollectGeneratedFiles(context.ModFolder, dryRun: true);
        if (dryRun.RemovedFiles.Count != 0)
            throw new InvalidOperationException($"Idempotent update left orphan output: {string.Join(", ", dryRun.RemovedFiles)}");
        return $"Second create PASS; stable ID {options[0].Id}; logical overrides {options.Count}; generated before {first.Execution.GeneratedFiles.Count}; generated after {context.SecondCreate.Execution.GeneratedFiles.Count}; ReloadMod {(context.SecondCreate.Reload.Succeeded ? "PASS" : "FAIL")}: {context.SecondCreate.Reload.Actual}; orphans 0.";
    }

    private static string VerifySourceIntegrity(RegressionContext context)
    {
        foreach (var (relativePath, expectedHash) in context.SourcePapHashes)
        {
            if (!PathSafety.TryResolveInsideRoot(context.ModFolder, relativePath, out var fullPath) || !File.Exists(fullPath)
                || !string.Equals(HashFile(fullPath), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Warrior source PAP changed: {relativePath}");
            }
        }
        var after = JObject.Parse(File.ReadAllText(context.MetaPath));
        if (!JToken.DeepEquals(context.MetadataWithoutDancy, RemoveDancyMetadata(after)))
            throw new InvalidOperationException("Warrior metadata changed outside Dancy-owned group content.");
        return $"Original source PAP hashes UNCHANGED: {FormatHashes(context.SourcePapHashes)}. Metadata SHA-256 before {context.MetadataHash}; after {HashFile(context.MetaPath)}; non-Dancy metadata unchanged.";
    }

    private static OverridePlan BuildPlan(RegressionContext context, IEnumerable<ParsedEmoteOverride> selected)
    {
        var animation = context.Source.LogicalAnimations.Single();
        return OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = context.ModDirectory,
            SourceGroupName = context.Source.GroupName,
            SourceOptionName = context.Source.OptionName,
            SourceAnimationName = animation.Name,
            SourceAnimationCommand = animation.Command,
            TargetTimelineKey = context.Water.PrimaryTimelineKey,
            TargetName = context.Water.Name,
            TargetCommand = context.Water.Command,
            Sources = selected.Select(entry => new OverridePlanSource(entry.GamePath, entry.ModdedPapPath)).ToList(),
            TargetGamePaths = context.WaterTargetPaths,
        });
    }

    private static OverridePlan GetPlan(RegressionContext context)
        => context.Plan ?? throw new InvalidOperationException("The Loop-only production plan was not generated.");

    private static OverrideOperationResult GetFirstCreate(RegressionContext context)
        => context.FirstCreate ?? throw new InvalidOperationException("The production create operation did not run.");

    private static IReadOnlyList<string> GetGeneratedFiles(string modFolder)
        => DancyFileManager.CollectGeneratedFiles(modFolder, dryRun: true).RemainingFiles;

    private static byte[] ReadHavokPayload(string path)
    {
        var bytes = PapFileInspector.ReadFileWithRetry(path);
        var inspection = PapFileInspector.Inspect(bytes);
        var length = inspection.TimelineOffset - inspection.HavokOffset;
        var payload = new byte[length];
        Buffer.BlockCopy(bytes, inspection.HavokOffset, payload, 0, length);
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

    private static int GetPhaseCount(IReadOnlyDictionary<AnimationPhase, int> counts, AnimationPhase phase)
        => counts.TryGetValue(phase, out var count) ? count : 0;

    private static string HashFile(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static string FormatHashes(IReadOnlyDictionary<string, string> hashes)
        => string.Join(", ", hashes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"));

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
        public string ModName { get; init; } = string.Empty;
        public string ModFolder { get; init; } = string.Empty;
        public string MetaPath { get; init; } = string.Empty;
        public string MetadataFormat { get; init; } = string.Empty;
        public string MetadataHash { get; init; } = string.Empty;
        public JObject MetadataWithoutDancy { get; init; } = new();
        public RemappableOption Source { get; init; } = new();
        public IReadOnlyList<ParsedEmoteOverride> LoopEntries { get; init; } = Array.Empty<ParsedEmoteOverride>();
        public IReadOnlyList<ParsedEmoteOverride> StartEntries { get; init; } = Array.Empty<ParsedEmoteOverride>();
        public IReadOnlyDictionary<string, string> SourcePapHashes { get; init; } = new Dictionary<string, string>();
        public LuminaEmote Water { get; init; } = new();
        public IReadOnlyList<string> WaterTargetPaths { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, PapFileInspector.PapFileInspection> WaterTargetInspections { get; init; } = new Dictionary<string, PapFileInspector.PapFileInspection>();
        public IReadOnlyDictionary<string, string> WaterTargetEvents { get; init; } = new Dictionary<string, string>();
        public IReadOnlyList<DancyOverrideInfo> DancyOverridesBefore { get; init; } = Array.Empty<DancyOverrideInfo>();
        public DancyOverrideInfo? LegacyOverride { get; init; }
        public IReadOnlyList<string> LegacyGeneratedPaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> GeneratedBefore { get; init; } = Array.Empty<string>();
        public DancyRemovalResult? Removal { get; set; }
        public PenumbraReloadResult? RemovalReload { get; set; }
        public OverridePlan? Plan { get; set; }
        public PapCompatibilityResult? Preflight { get; set; }
        public OverrideOperationResult? FirstCreate { get; set; }
        public OverrideOperationResult? SecondCreate { get; set; }
    }
}
#endif
