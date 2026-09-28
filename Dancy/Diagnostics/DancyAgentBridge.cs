#if DEBUG
using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.Dalamud.AgentBridge;
using ECommons.DalamudServices;

namespace Dancy.Diagnostics;

/// <summary>
/// Debug-only local DAB endpoint. It exposes provenance and a single reviewed semantic action.
/// No arbitrary bridge command can write to the game or to Penumbra.
/// </summary>
internal sealed class DancyAgentBridge : IDisposable
{
    private const string ReviewSurfaceId = "dancy.debug";
    private const string SelfTestControlId = "dancy.debug.run-selftest";
    private const string PushupsWaterRegressionControlId = "dancy.debug.run-pushups-water-regression";
    private const string TargetCatalogInspectionControlId = "dancy.debug.inspect-target-catalog";
    private const string MultiSectionResearchControlId = "dancy.debug.inspect-multisection-paps";
    private readonly Plugin plugin;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry = new();
    private readonly AgentBridgeSurfaceRegistry surfaceRegistry = new();
    private readonly AgentBridgeOperationRegistry operations = new();
    private readonly AgentBridgeRuntimeIdentity runtime;
    private readonly AgentBridgeHost host;
    private int selfTestRunning;
    private DancySelfTestResult? lastSelfTest;
    private DancySelfTestResult? lastPushupsWaterRegression;
    private DancySelfTestResult? lastTargetCatalogInspection;
    private DancySelfTestResult? lastMultiSectionResearch;

    public DancyAgentBridge(Plugin plugin)
    {
        this.plugin = plugin;
        if (string.IsNullOrWhiteSpace(plugin.Configuration.AgentBridgeInstanceId))
        {
            plugin.Configuration.AgentBridgeInstanceId = Guid.NewGuid().ToString("N");
            plugin.Configuration.Save();
        }

        var profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(Plugin.PluginInterface.ConfigDirectory.FullName);
        runtime = AgentBridgeRuntimeIdentity.FromAssembly("Dancy", typeof(Plugin).Assembly, Plugin.PluginInterface.AssemblyLocation.FullName);
        surfaceRegistry.Register(
            new AgentBridgeReviewSurfaceDescriptor(ReviewSurfaceId, "Dancy developer validation", "present-surface", ReviewSurfaceId, 0),
            plugin.OpenConfigUi);

        var router = new AgentBridgeCommandRouter()
            .Register("get-snapshot", _ => AgentBridgeResponse.Ok("Dancy snapshot captured.", CreateSnapshot()))
            .Register("get-operation", request => GetOperation(request.OperationId))
            .Register("get-review-surfaces", _ => AgentBridgeResponse.Ok("Dancy review surfaces captured.", surfaceRegistry.Snapshot()))
            .Register("open-main-window", OpenConfigUiAsync)
            .Register("present-surface", PresentSurfaceAsync)
            .Register("get-control", ReviewControlAsync)
            .Register("invoke-control", InvokeControlAsync);

        host = new AgentBridgeHost(new AgentBridgeHostOptions
        {
            ConfigDirectory = Plugin.PluginInterface.ConfigDirectory.FullName,
            PluginInstanceId = plugin.Configuration.AgentBridgeInstanceId,
            PipeName = $"dancy.agentbridge.{Environment.ProcessId}.{plugin.Configuration.AgentBridgeInstanceId}",
            GetProtectedAccessToken = () => plugin.Configuration.AgentBridgeProtectedAccessToken,
            SetProtectedAccessToken = value => plugin.Configuration.AgentBridgeProtectedAccessToken = value,
            SaveConfiguration = plugin.Configuration.Save,
            CreateManifest = CreateManifest,
            HandleRequestAsync = router.HandleAsync,
        });
        host.Start();
    }

    public bool IsSelfTestRunning => Volatile.Read(ref selfTestRunning) != 0;

    public string? LastSelfTestSummary => lastSelfTest is null
        ? null
        : $"Integration self-test: {lastSelfTest.Summary}{(lastSelfTest.Passed ? string.Empty : " (see DAB snapshot for failures)")}";

    public void BeginUiFrame() => reviewRegistry.BeginFrame();
    public void EndUiFrame() => reviewRegistry.EndFrame();

    public void RegisterSelfTestControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            SelfTestControlId,
            "Run Dancy integration self-test",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: LastSelfTestSummary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: true,
            completionOperationKind: "dancy.debug.selftest",
            _ => StartSelfTest());
    }

    public void RegisterPushupsWaterRegressionControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            PushupsWaterRegressionControlId,
            "Run Push-ups to Water production regression",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastPushupsWaterRegression?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: true,
            completionOperationKind: "dancy.debug.pushups-water-regression",
            _ => StartPushupsWaterRegression());
    }

    public void RegisterTargetCatalogInspectionControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            TargetCatalogInspectionControlId,
            "Inspect current target catalog",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastTargetCatalogInspection?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: false,
            completionOperationKind: "dancy.debug.target-catalog",
            _ => StartTargetCatalogInspection());
    }

    public void RegisterMultiSectionResearchControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            MultiSectionResearchControlId,
            "Inspect multi-section PAP research fixtures",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastMultiSectionResearch?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: false,
            completionOperationKind: "dancy.debug.multisection-pap-research",
            _ => StartMultiSectionResearch());
    }

    public AgentBridgeUiActionResult StartSelfTest()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("Dancy's integration self-test is already running.");

        var operation = operations.Begin("dancy.debug.selftest", "Dancy integration self-test queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Dancy integration self-test is running.");
                var result = new DancyLiveSelfTestRunner().Run();
                lastSelfTest = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Dancy integration self-test reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "SelfTestFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["artifactsRetained"] = string.Join(";", result.RetainedArtifacts),
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Debug self-test crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledSelfTestException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Dancy integration self-test started.", operation.Id);
    }

    public AgentBridgeUiActionResult StartPushupsWaterRegression()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.pushups-water-regression", "Push-ups to Water production regression queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Push-ups to Water production regression is running.");
                var result = new DancyPushupsWaterRegressionRunner().Run();
                lastPushupsWaterRegression = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Push-ups to Water production regression reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "PushupsWaterRegressionFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["artifactsRetained"] = string.Join(";", result.RetainedArtifacts),
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Push-ups to Water production regression crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledPushupsWaterRegressionException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Push-ups to Water production regression started.", operation.Id);
    }

    public AgentBridgeUiActionResult StartTargetCatalogInspection()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.target-catalog", "Current target catalog inspection queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Current target catalog inspection is running.");
                var result = new DancyTargetCatalogInspector().Run();
                lastTargetCatalogInspection = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Current target catalog inspection reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "TargetCatalogInspectionFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["artifactsRetained"] = string.Join(";", result.RetainedArtifacts),
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Target catalog inspection crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledTargetCatalogInspectionException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Current target catalog inspection started.", operation.Id);
    }

    public AgentBridgeUiActionResult StartMultiSectionResearch()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.multisection-pap-research", "Multi-section PAP inspection queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Multi-section PAP inspection is running.");
                var result = new DancyMultiSectionPapResearchRunner().Run();
                lastMultiSectionResearch = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Multi-section PAP inspection reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "MultiSectionPapResearchFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["artifactsRetained"] = string.Join(";", result.RetainedArtifacts),
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Multi-section PAP inspection crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledMultiSectionPapResearchException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Dancy multi-section PAP inspection started.", operation.Id);
    }

    public void Dispose() => host.Dispose();

    private AgentBridgeManifest CreateManifest() => new(
        ProtocolVersion: 1,
        Runtime: runtime,
        ProfileId: AgentBridgeProfileIdentity.FromPluginConfigDirectory(Plugin.PluginInterface.ConfigDirectory.FullName).Id,
        ProfileAlias: AgentBridgeProfileIdentity.FromPluginConfigDirectory(Plugin.PluginInterface.ConfigDirectory.FullName).Alias,
        SnapshotSchema: "dancy.snapshot.v1",
        Capabilities:
        [
            new AgentBridgeCapabilityDescriptor("snapshot.read"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.selftest"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.pushups-water-regression"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.target-catalog"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.multisection-pap-research"),
        ],
        ReviewSurfaces: surfaceRegistry.Snapshot(),
        CaptureSurfaces: Array.Empty<AgentBridgeCaptureSurfaceDescriptor>(),
        Actions: reviewRegistry.ActionCatalog(),
        CatalogRevision: Math.Max(surfaceRegistry.CatalogRevision, reviewRegistry.CatalogRevision));

    private object CreateSnapshot() => new
    {
        schema = "dancy.snapshot.v1",
        runtime,
        selfTestRunning = IsSelfTestRunning,
        selfTest = lastSelfTest,
        pushupsWaterRegression = lastPushupsWaterRegression,
        targetCatalogInspection = lastTargetCatalogInspection,
        multiSectionPapResearch = lastMultiSectionResearch,
        operations = operations.Snapshot(),
        bridge = new
        {
            host.IsRunning,
            reviewControls = reviewRegistry.Snapshot(),
        },
    };

    private AgentBridgeResponse GetOperation(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            return AgentBridgeResponse.Fail("An operation ID is required.");
        var operation = operations.Get(operationId);
        return operation is null
            ? AgentBridgeResponse.Fail("The requested operation was not found.")
            : AgentBridgeResponse.Ok("Dancy operation captured.", operation);
    }

    private async ValueTask<AgentBridgeResponse> PresentSurfaceAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Target, ReviewSurfaceId, StringComparison.Ordinal))
            return AgentBridgeResponse.Fail("The requested Dancy review surface is not registered.");
        var presented = await OnFrameworkAsync(() => surfaceRegistry.TryPresent(ReviewSurfaceId), cancellationToken).ConfigureAwait(false);
        return presented ? AgentBridgeResponse.Ok("Dancy developer validation surface presented.") : AgentBridgeResponse.Fail("The Dancy review surface could not be presented.");
    }

    private async ValueTask<AgentBridgeResponse> OpenConfigUiAsync(AgentBridgeRequest _, CancellationToken cancellationToken)
    {
        await OnFrameworkAsync(() =>
        {
            plugin.OpenConfigUi();
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return AgentBridgeResponse.Ok("Dancy developer validation window opened.");
    }

    private async ValueTask<AgentBridgeResponse> ReviewControlAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Target))
            return AgentBridgeResponse.Fail("A Dancy control ID is required.");
        var review = await OnFrameworkAsync(() => reviewRegistry.Review(request.Target), cancellationToken).ConfigureAwait(false);
        return review.Control is null
            ? AgentBridgeResponse.Fail("The requested Dancy control is not rendered.")
            : AgentBridgeResponse.Ok("Dancy control reviewed.", review);
    }

    private async ValueTask<AgentBridgeResponse> InvokeControlAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Target) || request.FrameId is not { } frameId)
            return AgentBridgeResponse.Fail("A Dancy control ID and reviewed frame ID are required.");
        var invocation = await OnFrameworkAsync(() => reviewRegistry.Invoke(request.Target, frameId, request.Arguments), cancellationToken).ConfigureAwait(false);
        return invocation.Success
            ? AgentBridgeResponse.Ok(invocation.Message, invocation, invocation.Action?.OperationId)
            : AgentBridgeResponse.Fail(invocation.Message);
    }

    private static Task<T> OnFrameworkAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Svc.Framework.RunOnFrameworkThread(() =>
        {
            try { completion.TrySetResult(work()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task.WaitAsync(cancellationToken);
    }
}
#endif
