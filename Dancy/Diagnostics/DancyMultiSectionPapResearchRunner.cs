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
using Dancy.Persistence;
using ECommons.DalamudServices;
using Penumbra.Api.IpcSubscribers;

namespace Dancy.Diagnostics;

/// <summary>
/// Captures current-game PAP structure for the multi-section research milestone.
/// It is deliberately read-only: no PAP writer, Penumbra mutation, or redirect
/// is reachable from this runner.
/// </summary>
internal sealed class DancyMultiSectionPapResearchRunner
{
    private const string SourceModName = "[HS] Warrior of Lift (Default)";
    private const string SourceGroupName = "Bench Press - /pushups";
    private const string SourceOptionName = "Enable";

    public DancySelfTestResult Run()
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();

        RunCase(cases, "Water structural control", "structure", "Every current Water PAP has one header, one TMB section, and a single explicit Havok binding.",
            () => OnFramework(InspectWater));
        RunCase(cases, "Standing Idle matrix", "structure", "Every current normal/idle variant is inspected without assigning an unproven semantic role.",
            () => OnFramework(InspectStandingIdle));
        RunCase(cases, "Push-ups to Standing Idle dry run", "planner", "The planner reads the installed Push-ups Loop source and refuses the current Unknown Standing Idle topology without writing.",
            () => OnFramework(InspectPushupsStandingIdlePlan));
        RunCase(cases, "Other multi-section target inventory", "catalog", "The existing target catalog is surveyed for structurally multi-section PAPs without enabling any target.",
            () => OnFramework(InspectOtherMultiSectionTargets));

        return new DancySelfTestResult
        {
            Schema = "dancy.multisection-pap-research.v1",
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
        };
    }

    private static string InspectWater()
    {
        var water = EmoteLibrary.AllEmotes.SingleOrDefault(emote => string.Equals(emote.Command, "/water", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The current target catalog does not contain /water.");
        var structures = PapResolver.ResolvePapFiles(water.PrimaryTimelineKey)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(PapStructureInspector.InspectTarget)
            .ToList();
        if (structures.Count == 0 || structures.Any(structure => structure.Topology != PapTopology.SingleSection || structure.Sections.Count != 1 || structure.TimelineSections.Count != 1))
            throw new InvalidOperationException("Water no longer provides the expected one-animation, one-TMB control topology.");

        return string.Join(" | ", structures.Select(DescribeStructure));
    }

    private static string InspectStandingIdle()
    {
        var idle = GetStandingIdleTarget();
        var structures = PapResolver.ResolvePapFiles(idle.PrimaryTimelineKey)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(PapStructureInspector.InspectTarget)
            .ToList();
        if (structures.Count == 0)
            throw new InvalidOperationException("normal/idle did not resolve any current player PAPs.");
        if (structures.Any(structure => structure.Sections.Count != 2 || structure.TimelineSections.Count != 2))
            throw new InvalidOperationException("At least one current normal/idle variant no longer has the researched 2-header/2-TMB structure.");
        if (structures.Any(structure => structure.Topology != PapTopology.Unknown))
            throw new InvalidOperationException("A multi-section Standing Idle variant was classified beyond the available evidence.");

        return string.Join(" | ", structures.Select(DescribeStructure));
    }

    private static string InspectPushupsStandingIdlePlan()
    {
        var mod = new GetModList(Plugin.PluginInterface).Invoke()
            .FirstOrDefault(pair => string.Equals(pair.Value, SourceModName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(mod.Key))
            throw new InvalidOperationException($"{SourceModName} is not installed in the current Penumbra library.");

        var root = PenumbraDirectoryResolver.GetPenumbraDirectory();
        if (string.IsNullOrWhiteSpace(root) || !PathSafety.TryResolveInsideRoot(root, mod.Key, out var modFolder))
            throw new InvalidOperationException("Dancy could not resolve the installed Push-ups source folder safely.");
        var source = EmoteOverrideScanner.ScanMod(modFolder)
            .SingleOrDefault(option => string.Equals(option.GroupName, SourceGroupName, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(option.OptionName, SourceOptionName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The installed Warrior of Lift mod has no Bench Press /pushups Enable source option.");
        var loops = source.LoopEntries;
        if (loops.Count == 0)
            throw new InvalidOperationException("The Push-ups source has no Loop PAPs.");

        var targetByRace = PapResolver.ResolvePapFiles(GetStandingIdleTarget().PrimaryTimelineKey)
            .ToDictionary(path => GamePathIdentity.Parse(path).Character.Code, path => path, StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        foreach (var loop in loops.OrderBy(entry => entry.GamePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!PathSafety.TryResolveInsideRoot(modFolder, loop.ModdedPapPath, out var sourcePath) || !File.Exists(sourcePath))
                throw new InvalidOperationException($"The Push-ups source PAP is unsafe or missing: {loop.ModdedPapPath}");
            var race = GamePathIdentity.Parse(loop.GamePath).Character.Code;
            if (!targetByRace.TryGetValue(race, out var targetPath))
                throw new InvalidOperationException($"Standing Idle has no current target PAP for Push-ups source race {race}.");

            var sourceStructure = PapStructureInspector.InspectFile(sourcePath);
            var targetStructure = PapStructureInspector.InspectTarget(targetPath);
            var plan = PapStructuralPlanner.Plan(sourceStructure, targetStructure);
            if (plan.Status != PapStructuralPlanStatus.Unsupported || !plan.IsDryRun)
                throw new InvalidOperationException($"The Standing Idle research planner did not fail closed for {race}.");
            results.Add($"{race}: source {loop.ModdedPapPath} [{sourceStructure.Topology}] -> target {targetPath} [{targetStructure.Topology}]; replacement none; preserved none; {plan.Status}: {plan.Reason}");
        }

        return string.Join(" | ", results);
    }

    private static string InspectOtherMultiSectionTargets()
    {
        var findings = new List<string>();
        var catalog = EmoteLibrary.AllEmotes
            .GroupBy(target => target.PrimaryTimelineKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var target in catalog)
        {
            var structures = PapResolver.ResolvePapFiles(target.PrimaryTimelineKey)
                .Select(PapStructureInspector.InspectTarget)
                .ToList();
            if (structures.Count == 0 || structures.All(structure => structure.Sections.Count == 1 && structure.TimelineSections.Count == 1))
                continue;

            var shapes = structures
                .GroupBy(structure => $"{structure.Sections.Count} animation / {structure.TimelineSections.Count} TMB")
                .Select(group => $"{group.Key} ({group.Count()} variant(s))");
            findings.Add($"{target.Name} [{target.PrimaryTimelineKey}]: {string.Join(", ", shapes)}; research-only, not enabled");
        }

        return findings.Count == 0
            ? "No structurally multi-section PAPs were found in the current Dancy target catalog."
            : string.Join(" | ", findings);
    }

    private static Dancy.Core.Models.LuminaEmote GetStandingIdleTarget()
        => EmoteLibrary.AllEmotes.SingleOrDefault(target => target.Context == TargetContext.StandingIdle
            && string.Equals(target.PrimaryTimelineKey, "normal/idle", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The current target catalog does not contain normal/idle.");

    private static string DescribeStructure(PapStructure structure)
    {
        var sections = string.Join(", ", structure.Sections.Select(section =>
            $"A{section.AnimationIndex}:{section.AnimationName};type={section.AnimationType};face={section.IsFaceAnimation};havok={section.HavokMotionIndex}({section.HavokBindingEvidence});tmb={section.TimelineSectionIndex}({section.TimelineBindingEvidence});role={section.Role}"));
        var timelines = string.Join(", ", structure.TimelineSections.Select(section =>
            $"T{section.Index}:offset={section.Offset};size={section.Size};sha256={section.ContentSha256};c009={string.Join("+", section.EventIdentifiers)}{(section.EventInspectionIssue is null ? string.Empty : $";inspection={section.EventInspectionIssue}")}"));
        return $"{structure.SourceIdentity}; topology={structure.Topology}; {sections}; {timelines}";
    }

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

    private static void RunCase(List<DancySelfTestCase> cases, string testName, string stage, string expected, Func<string> action)
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
            });
        }
    }
}
#endif
