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
using Dancy.Services;
using ECommons.DalamudServices;

namespace Dancy.Diagnostics;

/// <summary>
/// Reproduces Step 3's initial catalog queue followed by a Cheer Wave search.
/// The runner uses real game data but owns a temporary in-memory inspection
/// service, so it cannot change the user's Dancy or Penumbra state.
/// </summary>
internal sealed class DancyTargetInspectionPriorityRunner
{
    private const string CheerWaveQuery = "cheerw";
    private const int InitialVisibleTargetCount = 50;
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    public DancySelfTestResult Run()
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<DancySelfTestCase>();
        TargetInspectionService? service = null;
        try
        {
            var catalog = OnFramework(BuildCatalog);
            var cheerTargets = catalog
                .Where(entry => TargetEmotePolicy.IsSearchResult(entry.Behavior))
                .Where(entry => entry.SearchText.Contains(CheerWaveQuery, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var expectedNames = new[] { "Cheer Wave: Pink", "Cheer Wave: Violet", "Cheer Wave: Yellow" };
            var backgroundTargets = catalog
                .Where(entry => TargetEmotePolicy.IsVisible(TargetSelectionCategory.LoopingEmotes, entry.Behavior))
                .Where(entry => cheerTargets.All(cheer => !string.Equals(cheer.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(InitialVisibleTargetCount)
                .ToList();

            AddCase(
                cases,
                "Cheer Wave search catalog",
                "catalog",
                "Current game data exposes Pink, Violet, and Yellow for cheerw.",
                cheerTargets.Count == expectedNames.Length && expectedNames.All(name => cheerTargets.Any(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))),
                string.Join("; ", cheerTargets.Select(target => $"{target.Name} [{target.Id}]")));

            service = new TargetInspectionService(new DalamudTargetInspectionDataSource());
            var beforeSearch = OnFramework(() =>
            {
                PapResolver.ClearCache();
                DancyStep3PerformanceTelemetry.Reset();
                foreach (var target in backgroundTargets)
                    service.RequestTarget(CreateRequest(target), TargetInspectionPriority.VisibleCurrentTab);
                return service.GetDebugState();
            });

            var afterSearch = OnFramework(() =>
            {
                foreach (var target in cheerTargets)
                    service.RequestTarget(CreateRequest(target), TargetInspectionPriority.VisibleSearch);
                return service.GetDebugState();
            });
            var initialQueuePositions = cheerTargets
                .Select((target, index) => new QueuePosition(target.Name, backgroundTargets.Count + index, FindTarget(afterSearch, target.Id).QueuePosition))
                .ToList();
            var prioritized = initialQueuePositions.All(position => position.Current == initialQueuePositions.IndexOf(position));
            AddCase(
                cases,
                "Visible search outranks initial Step 3 rows",
                "scheduler",
                "Cheer Wave requests enter the visible-search queue at positions 0-2 instead of waiting behind the initial 50 current-tab targets.",
                prioritized,
                $"Initial targets queued {beforeSearch.FrameworkAcquisitionQueueLength}; legacy FIFO equivalent positions {string.Join(", ", initialQueuePositions.Select(position => $"{position.Name}={position.LegacyFifo}"))}; current positions {string.Join(", ", initialQueuePositions.Select(position => $"{position.Name}={position.Current}"))}.");

            var stopwatch = Stopwatch.StartNew();
            TargetInspectionServiceDebugState final = afterSearch;
            while (stopwatch.Elapsed < CompletionTimeout)
            {
                OnFramework(service.Update);
                final = service.GetDebugState();
                if (cheerTargets.All(target => IsTerminal(final, target.Id)))
                    break;
                Task.Delay(16).GetAwaiter().GetResult();
            }

            var cheerStates = cheerTargets.Select(target => FindTarget(final, target.Id)).ToList();
            var terminal = cheerTargets.All(target => IsTerminal(final, target.Id));
            var cheerTelemetry = DancyStep3PerformanceTelemetry.Snapshot();
            var cheerCache = PapResolver.GetCacheStatistics();
            AddCase(
                cases,
                "Cheer Wave foreground completion",
                "scheduler",
                "Every visible Cheer Wave request reaches a terminal published state within 10 seconds without processing the background queue first.",
                terminal,
                string.Join("; ", cheerStates.Select(DescribeTarget)));

            AddCase(
                cases,
                "Framework acquisition progress",
                "framework",
                "Framework updates continuously consume the visible-search queue while background targets remain deferred.",
                final.FrameworkUpdateCallbacks > 0 && final.AcquisitionSteps > 0 && terminal,
                $"Callbacks {final.FrameworkUpdateCallbacks}; acquisition steps {final.AcquisitionSteps}; queue start/end {afterSearch.FrameworkAcquisitionQueueLength}/{final.FrameworkAcquisitionQueueLength}; active acquisition {final.ActiveAcquisition ?? "none"}; active analysis {final.ActiveBackgroundAnalysis}.");

            var requested = cheerTelemetry.GameFileExistsCacheHits + cheerTelemetry.GameFileExistsCacheMisses;
            var cheerRequested = cheerStates.Sum(state => state.FileExistsRequests);
            AddCase(
                cases,
                "Cheer Wave PAP probe accounting",
                "game data",
                "Probe counters distinguish requested paths, unique cached paths, cache hits, and underlying DataManager probes.",
                terminal,
                $"Requested {requested}; unique paths {cheerCache.UniqueGamePathCount}; cache hits {cheerTelemetry.GameFileExistsCacheHits}; underlying probes {cheerTelemetry.GameFileExistsCalls}; Cheer foreground {cheerRequested}; incidental lower-priority work {requested - cheerRequested}; per target {string.Join("; ", cheerStates.Select(state => $"{state.DisplayName}: {state.FileExistsRequests}/{state.FileExistsCacheHits}/{state.UnderlyingFileExistsProbes}"))} (requested/hit/underlying). ");

            var acquisitionStep = cheerTelemetry.Timings.GetValueOrDefault("TargetInspectionAcquisitionStep");
            var acquisitionUpdate = cheerTelemetry.Timings.GetValueOrDefault("TargetInspectionAcquisitionUpdate");
            var averageUnusedBudget = acquisitionUpdate is null
                ? 0
                : Math.Max(0, 2 - acquisitionUpdate.AverageMilliseconds);
            var typicalStepsWithinBudget = acquisitionStep is null || acquisitionStep.MedianMilliseconds <= 0
                ? 0
                : (int)Math.Floor(2 / acquisitionStep.MedianMilliseconds);
            AddCase(
                cases,
                "Acquisition throughput profile",
                "framework",
                "Foreground acquisition uses the 2 ms framework deadline rather than a routine fixed-step ceiling, without moving work into Draw.",
                acquisitionStep is not null
                    && acquisitionUpdate is not null
                    && cheerTelemetry.TargetInspectionAcquisitionStepCapLimitedUpdates == 0
                    && cheerTelemetry.TargetInspectionAcquisitionEmergencyCapHits == 0
                    && cheerTelemetry.TargetInspectionAcquisitionFrameHitchesOver16Milliseconds == 0,
                $"Step average/median/max {acquisitionStep?.AverageMilliseconds:F3}/{acquisitionStep?.MedianMilliseconds:F3}/{acquisitionStep?.MaxMilliseconds:F3} ms; update average/median/max {acquisitionUpdate?.AverageMilliseconds:F3}/{acquisitionUpdate?.MedianMilliseconds:F3}/{acquisitionUpdate?.MaxMilliseconds:F3} ms; average unused 2 ms budget {averageUnusedBudget:F3} ms; typical median-sized steps within 2 ms {typicalStepsWithinBudget}; budget overruns {cheerTelemetry.TargetInspectionAcquisitionBudgetOverruns}; hard-cap-limited updates {cheerTelemetry.TargetInspectionAcquisitionStepCapLimitedUpdates}; emergency cap hits {cheerTelemetry.TargetInspectionAcquisitionEmergencyCapHits}; Dancy update hitches >16.7/>50/>100 ms {cheerTelemetry.TargetInspectionAcquisitionFrameHitchesOver16Milliseconds}/{cheerTelemetry.TargetInspectionAcquisitionFrameHitchesOver50Milliseconds}/{cheerTelemetry.TargetInspectionAcquisitionFrameHitchesOver100Milliseconds}." );

            var waterTarget = catalog.Single(entry => string.Equals(entry.Name, "Water", StringComparison.OrdinalIgnoreCase)
                && entry.Behavior == TargetBehavior.LoopingEmote);
            var waterQueued = OnFramework(() =>
            {
                service.RequestTarget(CreateRequest(waterTarget), TargetInspectionPriority.VisibleSearch);
                return service.GetDebugState();
            });
            var waterCompleted = PumpUntilTerminal(service, [waterTarget.Id]);
            var waterState = FindTarget(waterCompleted, waterTarget.Id);
            AddCase(
                cases,
                "Second cold visible search",
                "scheduler",
                "A new visible looping target outranks the still-pending initial Step 3 rows and reaches a terminal state without source invalidation.",
                waterState.Priority == TargetInspectionPriority.VisibleSearch
                    && FindTarget(waterQueued, waterTarget.Id).QueuePosition == 0
                    && waterState.PublishedAtUtc is not null,
                $"Water queue position {FindTarget(waterQueued, waterTarget.Id).QueuePosition}; {DescribeTarget(waterState)}");

            var sitTargets = catalog
                .Where(entry => TargetEmotePolicy.IsSearchResult(entry.Behavior))
                .Where(entry => entry.SearchText.Contains("sit", StringComparison.OrdinalIgnoreCase))
                .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(InitialVisibleTargetCount)
                .ToList();
            var sitQueued = OnFramework(() =>
            {
                foreach (var target in sitTargets)
                    service.RequestTarget(CreateRequest(target), TargetInspectionPriority.VisibleSearch);
                return service.GetDebugState();
            });
            var sitStates = sitTargets.Select(target => FindTarget(sitQueued, target.Id)).ToList();
            AddCase(
                cases,
                "Poses and Idles sit search priority",
                "scheduler",
                "A tab/search change promotes every visible sit result ahead of deferred looping-emote prewarm work without invalidating completed target structures.",
                sitStates.Select((state, index) => state.Priority == TargetInspectionPriority.VisibleSearch && state.QueuePosition == index).All(value => value),
                $"Visible sit targets {sitStates.Count}; positions {string.Join(", ", sitStates.Select(state => $"{state.DisplayName}={state.QueuePosition}"))}; completed Cheer retained {cheerStates.All(state => state.PublishedAtUtc is not null)}; Water retained {waterState.PublishedAtUtc is not null}.");

            AddCase(
                cases,
                "Generation safety during search",
                "scheduler",
                "A search changes only request priority; it does not create a source generation or discard valid current-generation results.",
                afterSearch.SourceGeneration == 0 && sitQueued.SourceGeneration == 0 && sitQueued.DiscardedStaleResults == 0,
                $"Target generation {sitQueued.TargetGeneration}; source generation {sitQueued.SourceGeneration}; completed {sitQueued.CompletedResults}; failed {sitQueued.FailedResults}; discarded stale {sitQueued.DiscardedStaleResults}.");
        }
        catch (Exception exception)
        {
            cases.Add(new DancySelfTestCase
            {
                TestName = "Cheer Wave priority regression",
                Stage = "runner",
                Expected = "The read-only scheduler regression completes against current game data.",
                Actual = exception.Message,
                FailureReason = exception.ToString(),
                Status = DancySelfTestStatus.Failed,
            });
        }
        finally
        {
            if (service is not null)
                OnFramework(service.Dispose);
        }

        return new DancySelfTestResult
        {
            Schema = "dancy.target-inspection-priority.v1",
            StartedAtUtc = started,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Cases = cases,
        };
    }

    private static List<TargetCatalogPresentation<LuminaEmote>> BuildCatalog()
        => EmoteLibrary.AllEmotes.Select(target => new TargetCatalogPresentation<LuminaEmote>(
                target,
                GetTargetId(target),
                target.Name,
                target.Command,
                target.Trigger,
                target.Behavior,
                target.Context,
                TargetSemantics.DisplayName(target.Behavior),
                TargetSemantics.DisplayName(target.Context)))
            .ToList();

    private static TargetInspectionRequest CreateRequest(TargetCatalogPresentation<LuminaEmote> presentation)
    {
        var target = presentation.Target;
        var standingIdle = target.Context == TargetContext.StandingIdle
            && string.Equals(target.PrimaryTimelineKey, "normal/idle", StringComparison.OrdinalIgnoreCase);
        return new TargetInspectionRequest(presentation.Id, target.PrimaryTimelineKey, target.Name, standingIdle);
    }

    private static string GetTargetId(LuminaEmote target)
        => string.IsNullOrWhiteSpace(target.TargetId) ? target.PrimaryTimelineKey : target.TargetId;

    private static TargetInspectionDebugEntry FindTarget(TargetInspectionServiceDebugState state, string id)
        => state.Targets.Single(target => string.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool IsTerminal(TargetInspectionServiceDebugState state, string id)
        => state.Targets.Any(target => string.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase)
            && target.PublishedAtUtc is not null);

    private static TargetInspectionServiceDebugState PumpUntilTerminal(TargetInspectionService service, IReadOnlyList<string> targetIds)
    {
        var stopwatch = Stopwatch.StartNew();
        var state = service.GetDebugState();
        while (stopwatch.Elapsed < CompletionTimeout)
        {
            OnFramework(service.Update);
            state = service.GetDebugState();
            if (targetIds.All(id => IsTerminal(state, id)))
                return state;
            Task.Delay(16).GetAwaiter().GetResult();
        }

        return state;
    }

    private static string DescribeTarget(TargetInspectionDebugEntry target)
    {
        var elapsed = target.PublishedAtUtc is { } published
            ? $"{(published - target.RequestedAtUtc).TotalMilliseconds:F0} ms"
            : "not published";
        return $"{target.DisplayName}: priority {target.Priority}; acquisition {target.AcquisitionState}; analysis {target.AnalysisState}; completed {target.CompletedAtUtc:O}; published {target.PublishedAtUtc:O}; elapsed {elapsed}.";
    }

    private static void AddCase(
        ICollection<DancySelfTestCase> cases,
        string testName,
        string stage,
        string expected,
        bool passed,
        string actual)
        => cases.Add(new DancySelfTestCase
        {
            TestName = testName,
            Stage = stage,
            Expected = expected,
            Actual = actual,
            Status = passed ? DancySelfTestStatus.Passed : DancySelfTestStatus.Failed,
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

    private static void OnFramework(Action work)
        => OnFramework(() =>
        {
            work();
            return true;
        });

    private sealed record QueuePosition(string Name, int LegacyFifo, int? Current);
}
#endif
