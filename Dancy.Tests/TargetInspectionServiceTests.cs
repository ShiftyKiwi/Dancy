using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dancy.Domain;
using Dancy.Pap;
using Dancy.Services;
using Xunit;

namespace Dancy.Tests;

public sealed class TargetInspectionServiceTests
{
    [Fact]
    public void RequestingAndReadingPendingStateDoNotAcquireGameData()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = CreateService(data);

        service.RequestTarget(Request("water"));

        Assert.True(service.TryGetTarget("water", out var pending));
        Assert.Equal(TargetInspectionState.Pending, pending!.State);
        Assert.Equal(0, data.BeginResolutionCalls);
        Assert.Equal(0, data.ReadCalls);
    }

    [Fact]
    public void DuplicateRequestsCoalesceAndReadyStructureIsReused()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = CreateService(data);

        service.RequestTarget(Request("water"));
        service.RequestTarget(Request("water"));
        PumpUntilReady(service, "water");

        Assert.True(service.TryGetTarget("water", out var ready));
        Assert.Equal(TargetInspectionState.Ready, ready!.State);
        Assert.Equal(1, data.BeginResolutionCalls);
        Assert.Equal(1, data.ReadCalls);

        service.RequestTarget(Request("water"));
        service.Update();
        Assert.Equal(1, data.BeginResolutionCalls);
        Assert.Equal(1, data.ReadCalls);
    }

    [Fact]
    public void InvalidatingTargetCatalogDiscardsQueuedWork()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = CreateService(data);

        service.RequestTarget(Request("water"));
        service.InvalidateTargetCatalog();
        service.Update();

        Assert.False(service.TryGetTarget("water", out _));
        Assert.Equal(0, data.BeginResolutionCalls);
        Assert.Equal(0, data.ReadCalls);
    }

    [Fact]
    public void FrameworkUpdateConsumesFastAcquisitionUnitsUntilTheTimeBudgetExpires()
    {
        var resolution = new DelayedResolution("target.pap", stepsUntilComplete: 5);
        var data = new DelayedDataSource(resolution, PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = new TargetInspectionService(data, (action, _) =>
        {
            action();
            return Task.CompletedTask;
        });

        service.RequestTarget(Request("water"));
        service.Update();

        Assert.Equal(5, resolution.AdvanceCalls);
        Assert.Equal(1, data.ReadCalls);
        Assert.True(service.TryGetTarget("water", out var ready));
        Assert.Equal(TargetInspectionState.Ready, ready!.State);
    }

    [Fact]
    public void FrameworkUpdateChecksTheDeadlineBeforeStartingAnotherSlowAcquisitionUnit()
    {
        var resolution = new SlowResolution();
        var data = new DelayedDataSource(resolution, PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = new TargetInspectionService(data, (action, _) =>
        {
            action();
            return Task.CompletedTask;
        });

        service.RequestTarget(Request("water"));
        service.Update();

        Assert.Equal(1, resolution.AdvanceCalls);
        Assert.Equal(0, data.ReadCalls);
        Assert.True(service.TryGetTarget("water", out var pending));
        Assert.Equal(TargetInspectionState.Pending, pending!.State);
    }

    [Fact]
    public void VisibleSearchTargetRunsBeforeEarlierBackgroundWork()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = CreateService(data);

        service.RequestTarget(Request("background"), TargetInspectionPriority.Background);
        service.RequestTarget(Request("cheer"), TargetInspectionPriority.VisibleSearch);
        service.Update();

        Assert.NotEmpty(data.RequestedTimelineKeys);
        Assert.Equal("emote/cheer", data.RequestedTimelineKeys[0]);
        var state = service.GetDebugState();
        var cheer = Assert.Single(state.Targets, target => target.Id == "cheer");
        Assert.Equal(TargetInspectionPriority.VisibleSearch, cheer.Priority);
    }

    [Fact]
    public void SearchPromotionMovesExistingPendingWorkAheadOfBackground()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = CreateService(data);

        service.RequestTarget(Request("first"), TargetInspectionPriority.Background);
        service.RequestTarget(Request("cheer"), TargetInspectionPriority.Background);
        service.RequestTarget(Request("cheer"), TargetInspectionPriority.VisibleSearch);
        service.Update();

        Assert.NotEmpty(data.RequestedTimelineKeys);
        Assert.Equal("emote/cheer", data.RequestedTimelineKeys[0]);
        var cheer = Assert.Single(service.GetDebugState().Targets, target => target.Id == "cheer");
        Assert.Equal(TargetInspectionPriority.VisibleSearch, cheer.Priority);
    }

    [Fact]
    public void SelectedPendingTargetHasHighestPriorityWithoutDuplicatingWork()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = CreateService(data);

        service.RequestTarget(Request("search"), TargetInspectionPriority.VisibleSearch);
        service.RequestTarget(Request("selected"), TargetInspectionPriority.VisibleCurrentTab);
        service.RequestTarget(Request("selected"), TargetInspectionPriority.Selected);
        service.Update();

        Assert.NotEmpty(data.RequestedTimelineKeys);
        Assert.Equal("emote/selected", data.RequestedTimelineKeys[0]);
        Assert.Equal(1, service.GetDebugState().Targets.Count(target => target.Id == "selected"));
    }

    [Fact]
    public void SearchPriorityDoesNotInvalidateCompletedTargetOrSourceGeneration()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = CreateService(data);

        service.RequestTarget(Request("water"), TargetInspectionPriority.Background);
        PumpUntilReady(service, "water");
        var before = service.GetDebugState();

        service.RequestTarget(Request("water"), TargetInspectionPriority.VisibleSearch);
        service.Update();

        Assert.True(service.TryGetTarget("water", out var ready));
        Assert.Equal(TargetInspectionState.Ready, ready!.State);
        var after = service.GetDebugState();
        Assert.Equal(before.TargetGeneration, after.TargetGeneration);
        Assert.Equal(before.SourceGeneration, after.SourceGeneration);
        Assert.Equal(1, data.BeginResolutionCalls);
        Assert.Equal(1, data.ReadCalls);
    }

    [Fact]
    public void WorkerSchedulingFailureTransitionsPendingTargetToFailed()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = new TargetInspectionService(data, (_, _) => throw new InvalidOperationException("fixture worker failure"));

        service.RequestTarget(Request("water"));
        for (var tick = 0; tick < 4; ++tick)
            service.Update();

        Assert.True(service.TryGetTarget("water", out var failed));
        Assert.Equal(TargetInspectionState.Failed, failed!.State);
        Assert.Equal(1, service.GetDebugState().FailedResults);
    }

    [Fact]
    public async Task CancellingActiveWorkCannotLeaveAPermanentPendingTarget()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        Task? activeWorker = null;
        using var service = new TargetInspectionService(data, (_, cancellationToken) =>
        {
            activeWorker = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return activeWorker;
        });

        service.RequestTarget(Request("water"));
        service.Update();
        service.Update();
        Assert.NotNull(activeWorker);

        service.InvalidateTargetCatalog();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeWorker!);
        service.Update();

        var state = service.GetDebugState();
        Assert.Equal(0, state.PendingRequests);
        Assert.Equal(0, state.FrameworkAcquisitionQueueLength);
        Assert.False(service.TryGetTarget("water", out _));
    }

    [Fact]
    public void SourceGenerationInvalidationDiscardsStaleCompatibilityResult()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = CreateService(data);
        var target = new TargetStructureSnapshot(TargetInspectionState.Ready, ["target.pap"], Array.Empty<PapTargetInspection>());
        var invalidPlan = new OverridePlan { Errors = ["fixture"] };

        service.RequestCompatibility("source-a|water", "C:\\fixture", invalidPlan, target);
        service.InvalidateSourceCompatibility();
        service.Update();

        Assert.False(service.TryGetCompatibility("source-a|water", out _));
    }

    [Fact]
    public void SourceAndUserAddedMappingChangesClearCompatibilityButReuseTargetStructure()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        using var service = CreateService(data);
        service.RequestTarget(Request("water"));
        PumpUntilReady(service, "water");
        Assert.True(service.TryGetTarget("water", out var target));

        service.RequestCompatibility("source-a|water", "C:\\fixture", new OverridePlan { Errors = ["fixture"] }, target!);
        service.Update();
        Assert.True(service.TryGetCompatibility("source-a|water", out var compatibility));
        Assert.Equal(TargetInspectionState.Ready, compatibility!.State);

        service.InvalidateSourceCompatibility();

        Assert.False(service.TryGetCompatibility("source-a|water", out _));
        Assert.True(service.TryGetTarget("water", out var cachedTarget));
        Assert.Equal(TargetInspectionState.Ready, cachedTarget!.State);
        Assert.Equal(1, data.BeginResolutionCalls);
        Assert.Equal(1, data.ReadCalls);
    }

    [Fact]
    public void DisposeCancelsPendingWorkWithoutThrowingFromUpdate()
    {
        var data = new FakeDataSource("target.pap", PapFileInspectorTests.CreateValidPap("fixture"));
        var service = CreateService(data);
        service.RequestTarget(Request("water"));

        service.Dispose();
        service.Update();

        Assert.Equal(0, data.BeginResolutionCalls);
        Assert.Equal(0, data.ReadCalls);
    }

    private static TargetInspectionService CreateService(FakeDataSource data)
        => new(data, (action, _) =>
        {
            action();
            return Task.CompletedTask;
        });

    private static TargetInspectionRequest Request(string id)
        => new(id, $"emote/{id}", "Fixture", IsStandingIdle: false);

    private static void PumpUntilReady(TargetInspectionService service, string id)
    {
        for (var tick = 0; tick < 16; ++tick)
        {
            service.Update();
            if (service.TryGetTarget(id, out var snapshot) && snapshot!.State == TargetInspectionState.Ready)
                return;
        }

        Assert.Fail($"Target {id} did not become ready within the bounded framework ticks.");
    }

    private sealed class FakeDataSource(string path, byte[] bytes) : ITargetInspectionDataSource
    {
        public int BeginResolutionCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public List<string> RequestedTimelineKeys { get; } = [];

        public IIncrementalTargetPathResolution BeginResolution(string timelineKey)
        {
            BeginResolutionCalls++;
            RequestedTimelineKeys.Add(timelineKey);
            return new FakeResolution(path);
        }

        public byte[] ReadTargetPapBytes(string requestedPath)
        {
            Assert.Equal(path, requestedPath);
            ReadCalls++;
            return bytes;
        }
    }

    private sealed class FakeResolution(string path) : IIncrementalTargetPathResolution
    {
        private bool completed;
        public bool IsCompleted => completed;
        public IReadOnlyList<string> Results => completed ? [path] : Array.Empty<string>();
        public int FileExistsRequests => completed ? 1 : 0;
        public int FileExistsCacheHits => 0;
        public int UnderlyingFileExistsProbes => completed ? 1 : 0;

        public bool TryAdvance()
        {
            completed = true;
            return true;
        }
    }

    private sealed class DelayedDataSource(IIncrementalTargetPathResolution resolution, byte[] bytes) : ITargetInspectionDataSource
    {
        public int ReadCalls { get; private set; }

        public IIncrementalTargetPathResolution BeginResolution(string _) => resolution;

        public byte[] ReadTargetPapBytes(string _)
        {
            ReadCalls++;
            return bytes;
        }
    }

    private sealed class DelayedResolution(string path, int stepsUntilComplete) : IIncrementalTargetPathResolution
    {
        public int AdvanceCalls { get; private set; }
        public bool IsCompleted => AdvanceCalls >= stepsUntilComplete;
        public IReadOnlyList<string> Results => IsCompleted ? [path] : Array.Empty<string>();
        public int FileExistsRequests => AdvanceCalls;
        public int FileExistsCacheHits => 0;
        public int UnderlyingFileExistsProbes => AdvanceCalls;

        public bool TryAdvance()
        {
            AdvanceCalls++;
            return true;
        }
    }

    private sealed class SlowResolution : IIncrementalTargetPathResolution
    {
        public int AdvanceCalls { get; private set; }
        public bool IsCompleted => false;
        public IReadOnlyList<string> Results => Array.Empty<string>();
        public int FileExistsRequests => AdvanceCalls;
        public int FileExistsCacheHits => 0;
        public int UnderlyingFileExistsProbes => AdvanceCalls;

        public bool TryAdvance()
        {
            AdvanceCalls++;
            Thread.Sleep(4);
            return true;
        }
    }
}
