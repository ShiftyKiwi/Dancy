#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dancy.Core;
using Dancy.Core.Models;
using Dancy.Domain;
using Dancy.Pap;
using ECommons.DalamudServices;

namespace Dancy.Diagnostics;

/// <summary>
/// Read-only validation of the current game-data target catalog. It neither
/// invokes Penumbra nor writes a file.
/// </summary>
internal sealed class DancyTargetCatalogInspector
{
    public DancySelfTestResult Run()
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();

        RunCase(cases, "Looping emote target", "catalog", "Water is a normal looping-emote target.", () =>
            Inspect("Water", TargetBehavior.LoopingEmote, TargetContext.Emote, minVariants: 1, requireSingleSection: true));
        RunCase(cases, "Standing idle target", "catalog", "Standing Idle is discovered as a persistent state and reported as structurally unsupported.", () =>
            InspectStandingIdle());
        RunCase(cases, "Chair sit target", "catalog", "Sit is a persistent chair-state target with current PAP variants.", () =>
            Inspect("Sit", TargetBehavior.PersistentPose, TargetContext.ChairSit, minVariants: 18, requireSingleSection: true));
        RunCase(cases, "Ground sit target", "catalog", "Sit on Ground is a persistent ground-state target with current PAP variants.", () =>
            Inspect("Sit on Ground", TargetBehavior.PersistentPose, TargetContext.GroundSit, minVariants: 18, requireSingleSection: true));
        RunCase(cases, "Sleep target", "catalog", "Sleep is a persistent bed or inn state with current PAP variants.", () =>
            Inspect("Sleep", TargetBehavior.PersistentPose, TargetContext.SleepOrLie, minVariants: 18, requireSingleSection: true));
        RunCase(cases, "Change Pose state families", "catalog", "Current pose, s_pose, j_pose, and l_pose entries retain their evidenced state contexts without guessing pose05 or pose06.", () =>
            InspectPoseFamilies());
        RunCase(cases, "One-shot target", "catalog", "Wave remains an advanced one-shot target.", () =>
            Inspect("Wave", TargetBehavior.OneShot, TargetContext.Emote, minVariants: 1, requireSingleSection: false));

        return new DancySelfTestResult
        {
            Schema = "dancy.target-catalog.v1",
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
        };
    }

    private static string Inspect(string name, TargetBehavior behavior, TargetContext context, int minVariants, bool requireSingleSection)
        => OnFramework(() =>
        {
            var target = EmoteLibrary.AllEmotes.SingleOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
                && candidate.Behavior == behavior
                && candidate.Context == context)
                ?? throw new InvalidOperationException($"Current target catalog does not contain {name} as {behavior}/{context}.");
            var paths = PapResolver.ResolvePapFiles(target.PrimaryTimelineKey);
            if (paths.Count < minVariants)
                throw new InvalidOperationException($"{name} resolved {paths.Count} PAP variants; expected at least {minVariants}.");
            var inspections = paths.Select(PapEditor.InspectTargetPap).ToList();
            if (requireSingleSection && inspections.Any(inspection => inspection.AnimationCount != 1 || inspection.TimelineSectionSizes.Count != 1))
                throw new InvalidOperationException($"{name} has a target PAP outside Dancy's one-animation, one-TMB-section writer scope.");
            var events = inspections.SelectMany(inspection => inspection.AnimationNames).Distinct(StringComparer.OrdinalIgnoreCase);
            return $"{target.PrimaryTimelineKey}; {paths.Count} variant(s); events {string.Join(", ", events)}.";
        });

    private static string InspectStandingIdle()
        => OnFramework(() =>
        {
            var target = EmoteLibrary.AllEmotes.SingleOrDefault(candidate => candidate.Context == TargetContext.StandingIdle
                && string.Equals(candidate.PrimaryTimelineKey, "normal/idle", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Current target catalog does not contain normal/idle.");
            var paths = PapResolver.ResolvePapFiles(target.PrimaryTimelineKey);
            if (paths.Count == 0)
                throw new InvalidOperationException("normal/idle did not resolve any current player PAPs.");
            var inspection = PapEditor.InspectTargetPap(paths[0]);
            if (inspection.AnimationCount <= 1 || inspection.TimelineSectionSizes.Count <= 1)
                throw new InvalidOperationException("normal/idle no longer demonstrates the expected multi-section structural preflight boundary.");
            return $"{paths.Count} variant(s); {inspection.AnimationCount} animation/header sections and {inspection.TimelineSectionSizes.Count} TMB sections on {paths[0]}.";
        });

    private static string InspectPoseFamilies()
        => OnFramework(() =>
        {
            var expected = new[]
            {
                ("emote/pose01_loop", TargetContext.StandingIdle),
                ("emote/pose02_loop", TargetContext.StandingIdle),
                ("emote/pose03_loop", TargetContext.StandingIdle),
                ("emote/pose04_loop", TargetContext.StandingIdle),
                ("emote/pose05_loop", TargetContext.OtherPersistentPose),
                ("emote/pose06_loop", TargetContext.OtherPersistentPose),
                ("emote/s_pose01_loop", TargetContext.ChairSit),
                ("emote/s_pose02_loop", TargetContext.ChairSit),
                ("emote/s_pose03_loop", TargetContext.ChairSit),
                ("emote/s_pose04_loop", TargetContext.ChairSit),
                ("emote/j_pose01_loop", TargetContext.GroundSit),
                ("emote/j_pose02_loop", TargetContext.GroundSit),
                ("emote/j_pose03_loop", TargetContext.GroundSit),
                ("emote/l_pose01_loop", TargetContext.SleepOrLie),
                ("emote/l_pose02_loop", TargetContext.SleepOrLie),
            };
            var summaries = new List<string>();
            foreach (var (timeline, context) in expected)
            {
                var target = EmoteLibrary.AllEmotes.SingleOrDefault(candidate => string.Equals(candidate.PrimaryTimelineKey, timeline, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Current target catalog does not contain {timeline}.");
                if (target.Behavior != TargetBehavior.PersistentPose || target.Context != context)
                    throw new InvalidOperationException($"{timeline} was classified {target.Behavior}/{target.Context}, expected PersistentPose/{context}.");
                var paths = PapResolver.ResolvePapFiles(timeline);
                if (paths.Count == 0)
                    throw new InvalidOperationException($"{timeline} did not resolve any current PAP variants.");
                var inspection = PapEditor.InspectTargetPap(paths[0]);
                if (inspection.AnimationCount != 1 || inspection.TimelineSectionSizes.Count != 1)
                    throw new InvalidOperationException($"{timeline} is outside Dancy's one-section PAP writer scope.");
                summaries.Add($"{timeline}: {paths.Count}");
            }

            return string.Join("; ", summaries);
        });

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
