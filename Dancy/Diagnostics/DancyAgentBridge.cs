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
    private const string TreadmillSelectorRegressionControlId = "dancy.debug.run-treadmill-selector-regression";
    private const string UserAddedCompatibleMappingRegressionControlId = "dancy.debug.run-user-added-compatible-mapping-regression";
    private const string TargetCatalogInspectionControlId = "dancy.debug.inspect-target-catalog";
    private const string MultiSectionResearchControlId = "dancy.debug.inspect-multisection-paps";
    private const string CorpusResearchControlId = "dancy.debug.index-animation-corpus";
    private const string MotionFingerprintResearchControlId = "dancy.debug.fingerprint-standing-idle-motions";
    private const string PerMotionCorpusResearchControlId = "dancy.debug.index-standing-idle-motion-corpus";
    private const string StandingIdleActiveCandidateOneInspectionControlId = "dancy.debug.inspect-active-ograyrei-standing-idle";
    private const string StandingIdleCandidateOneControlId = "dancy.debug.validate-standing-idle-candidate-one";
    private const string StandingIdleCandidateTwoControlId = "dancy.debug.validate-standing-idle-candidate-two";
    private const string StandingIdleProductionControlId = "dancy.debug.run-standing-idle-production-e2e";
    private const string Step3PerformanceControlId = "dancy.debug.profile-step3-performance";
    private const string TargetInspectionPriorityControlId = "dancy.debug.run-target-inspection-priority-regression";
    private readonly Plugin plugin;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry = new();
    private readonly AgentBridgeSurfaceRegistry surfaceRegistry = new();
    private readonly AgentBridgeOperationRegistry operations = new();
    private readonly AgentBridgeRuntimeIdentity runtime;
    private readonly AgentBridgeHost host;
    private int selfTestRunning;
    private DancySelfTestResult? lastSelfTest;
    private DancySelfTestResult? lastPushupsWaterRegression;
    private DancySelfTestResult? lastTreadmillSelectorRegression;
    private DancySelfTestResult? lastUserAddedCompatibleMappingRegression;
    private DancySelfTestResult? lastTargetCatalogInspection;
    private DancySelfTestResult? lastMultiSectionResearch;
    private DancySelfTestResult? lastCorpusResearch;
    private DancySelfTestResult? lastMotionFingerprintResearch;
    private DancySelfTestResult? lastPerMotionCorpusResearch;
    private DancySelfTestResult? lastStandingIdleActiveCandidateOneInspection;
    private DancySelfTestResult? lastStandingIdleCandidateOne;
    private DancySelfTestResult? lastStandingIdleCandidateTwo;
    private DancySelfTestResult? lastStandingIdleProduction;
    private DancySelfTestResult? lastStep3PerformanceProfile;
    private DancySelfTestResult? lastTargetInspectionPriorityRegression;

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

    public void RegisterTreadmillSelectorRegressionControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            TreadmillSelectorRegressionControlId,
            "Run Treadmill selector production regression",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastTreadmillSelectorRegression?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: true,
            completionOperationKind: "dancy.debug.treadmill-selector-regression",
            _ => StartTreadmillSelectorRegression());
    }

    public void RegisterUserAddedCompatibleMappingRegressionControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            UserAddedCompatibleMappingRegressionControlId,
            "Run user-added c0501 Treadmill to Water regression",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastUserAddedCompatibleMappingRegression?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: true,
            completionOperationKind: "dancy.debug.user-added-compatible-mapping-regression",
            _ => StartUserAddedCompatibleMappingRegression());
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

    public void RegisterCorpusResearchControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            CorpusResearchControlId,
            "Index configured Penumbra animation corpus",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastCorpusResearch?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: true,
            completionOperationKind: "dancy.debug.animation-corpus-research",
            _ => StartCorpusResearch());
    }

    public void RegisterMotionFingerprintResearchControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            MotionFingerprintResearchControlId,
            "Fingerprint Standing Idle Havok motions",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastMotionFingerprintResearch?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: false,
            completionOperationKind: "dancy.debug.standing-idle-motion-fingerprint-research",
            _ => StartMotionFingerprintResearch());
    }

    public void RegisterPerMotionCorpusResearchControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            PerMotionCorpusResearchControlId,
            "Index Standing Idle per-motion corpus",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastPerMotionCorpusResearch?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: false,
            completionOperationKind: "dancy.debug.standing-idle-per-motion-corpus-research",
            _ => StartPerMotionCorpusResearch());
    }

    public void RegisterStandingIdleCandidateOneControl(Vector2 min, Vector2 max, bool enabled)
        => RegisterStandingIdleCandidateControl(
            StandingIdleCandidateOneControlId,
            "Validate ogRayrei Male Miqo Standing Idle",
            min,
            max,
            enabled,
            () => lastStandingIdleCandidateOne?.Summary,
            StartStandingIdleCandidateOne);

    public void RegisterStandingIdleActiveCandidateOneInspectionControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            StandingIdleActiveCandidateOneInspectionControlId,
            "Inspect active ogRayrei Male Miqo Standing Idle",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastStandingIdleActiveCandidateOneInspection?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: false,
            completionOperationKind: "dancy.debug.standing-idle-active-inspection",
            _ => StartActiveStandingIdleCandidateOneInspection());
    }

    public void RegisterStandingIdleCandidateTwoControl(Vector2 min, Vector2 max, bool enabled)
        => RegisterStandingIdleCandidateControl(
            StandingIdleCandidateTwoControlId,
            "Validate Rust Idle Male Miqo Standing Idle",
            min,
            max,
            enabled,
            () => lastStandingIdleCandidateTwo?.Summary,
            StartStandingIdleCandidateTwo);

    public void RegisterStandingIdleProductionControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            StandingIdleProductionControlId,
            "Run c0701 Standing Idle production end-to-end test",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastStandingIdleProduction?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: true,
            completionOperationKind: "dancy.debug.standing-idle-production-e2e",
            _ => StartStandingIdleProduction());
    }

    public void RegisterStep3PerformanceControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            Step3PerformanceControlId,
            "Profile Step 3 target work",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastStep3PerformanceProfile?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: false,
            completionOperationKind: "dancy.debug.step3-performance",
            _ => StartStep3PerformanceProfile());
    }

    public void RegisterTargetInspectionPriorityControl(Vector2 min, Vector2 max, bool enabled)
    {
        reviewRegistry.Register(
            TargetInspectionPriorityControlId,
            "Run Step 3 Cheer Wave priority regression",
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: lastTargetInspectionPriorityRegression?.Summary,
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: false,
            completionOperationKind: "dancy.debug.target-inspection-priority",
            _ => StartTargetInspectionPriorityRegression());
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

    public AgentBridgeUiActionResult StartStep3PerformanceProfile()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.step3-performance", "Step 3 performance profile queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Step 3 target work is being profiled on the framework thread.");
                var result = plugin.ProfileStep3TargetCatalog();
                lastStep3PerformanceProfile = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Step 3 performance profile reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "Step3PerformanceProfileFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["sideEffects"] = "none; only Dancy in-memory resolver and inspection caches were cleared",
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Step 3 performance profile crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledStep3PerformanceProfileException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Step 3 performance profile started. It reads game data only and does not write to Penumbra.", operation.Id);
    }

    public AgentBridgeUiActionResult StartTargetInspectionPriorityRegression()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.target-inspection-priority", "Step 3 Cheer Wave priority regression queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "The read-only Cheer Wave queue-priority regression is running against current game data.");
                var result = new DancyTargetInspectionPriorityRunner().Run();
                lastTargetInspectionPriorityRegression = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Step 3 Cheer Wave priority regression reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "TargetInspectionPriorityRegressionFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["penumbraMutated"] = "false",
                        ["dancyPersistentStateMutated"] = "false",
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Step 3 Cheer Wave priority regression crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledTargetInspectionPriorityRegressionException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Step 3 Cheer Wave priority regression started. It uses a temporary in-memory service and does not change Penumbra or Dancy options.", operation.Id);
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

    public AgentBridgeUiActionResult StartTreadmillSelectorRegression()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.treadmill-selector-regression", "Treadmill selector production regression queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Treadmill selector production regression is running.");
                var result = new DancyTreadmillSelectorRegressionRunner().Run();
                lastTreadmillSelectorRegression = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Treadmill selector production regression reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "TreadmillSelectorRegressionFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["dancyTestOptionCleaned"] = result.Passed ? "true" : "unknown",
                        ["conduitTrigger"] = result.Passed ? "completed" : "unknown",
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Treadmill selector production regression crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledTreadmillSelectorRegressionException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Treadmill selector production regression started. It will create, resolve, trigger through Conduit, and remove only its test override.", operation.Id);
    }

    public AgentBridgeUiActionResult StartUserAddedCompatibleMappingRegression()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.user-added-compatible-mapping-regression", "User-added c0501 Treadmill to Water regression queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Dancy is validating the user-added c0501 Treadmill mapping.");
                var result = new DancyTreadmillSelectorRegressionRunner().RunUserAddedC0501WaterRegression();
                lastUserAddedCompatibleMappingRegression = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "User-added compatible mapping regression reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "UserAddedCompatibleMappingRegressionFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["dancyTestOptionCleaned"] = result.Passed ? "true" : "unknown",
                        ["conduitTrigger"] = result.Passed ? "completed" : "unknown",
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] User-added compatible mapping regression crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledUserAddedCompatibleMappingRegressionException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("User-added c0501 Treadmill regression started. It will clean up only its generated Dancy option.", operation.Id);
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

    public AgentBridgeUiActionResult StartCorpusResearch()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.animation-corpus-research", "Read-only Penumbra animation corpus indexing queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Read-only Penumbra animation corpus indexing is running.");
                var result = new DancyStandingIdleCorpusResearchRunner().Run();
                lastCorpusResearch = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Read-only Penumbra animation corpus indexing reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "AnimationCorpusResearchFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["artifactsRetained"] = string.Join(";", result.RetainedArtifacts),
                        ["corpusMutated"] = "false",
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Read-only Penumbra animation corpus indexing crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledAnimationCorpusResearchException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Dancy read-only Penumbra animation corpus indexing started.", operation.Id);
    }

    public AgentBridgeUiActionResult StartMotionFingerprintResearch()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.standing-idle-motion-fingerprint-research", "Read-only Standing Idle motion fingerprint research queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Read-only Standing Idle motion fingerprint research is running.");
                var result = new DancyStandingIdleFingerprintResearchRunner().Run();
                lastMotionFingerprintResearch = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Standing Idle motion fingerprint research reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "StandingIdleMotionFingerprintResearchFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["workspaceRetained"] = "false",
                        ["penumbraMutated"] = "false",
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Standing Idle motion fingerprint research crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledStandingIdleMotionFingerprintResearchException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Dancy read-only Standing Idle motion fingerprint research started.", operation.Id);
    }

    public AgentBridgeUiActionResult StartPerMotionCorpusResearch()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.standing-idle-per-motion-corpus-research", "Read-only Standing Idle per-motion corpus research queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Read-only Standing Idle per-motion corpus research is running.");
                var result = new DancyStandingIdlePerMotionCorpusResearchRunner().Run();
                lastPerMotionCorpusResearch = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Standing Idle per-motion corpus research reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "StandingIdlePerMotionCorpusResearchFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["workspaceRetained"] = "false",
                        ["penumbraMutated"] = "false",
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Standing Idle per-motion corpus research crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledStandingIdlePerMotionCorpusResearchException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Dancy read-only Standing Idle per-motion corpus research started.", operation.Id);
    }

    private void RegisterStandingIdleCandidateControl(
        string id,
        string name,
        Vector2 min,
        Vector2 max,
        bool enabled,
        Func<string?> value,
        Func<AgentBridgeUiActionResult> start)
    {
        reviewRegistry.Register(
            id,
            name,
            AgentBridgeUiControlKind.Button,
            min,
            max,
            enabled,
            selected: false,
            value: value(),
            arguments: null,
            surfaceId: ReviewSurfaceId,
            mutating: true,
            completionOperationKind: "dancy.debug.standing-idle-existing-mod-validation",
            _ => start());
    }

    public AgentBridgeUiActionResult StartStandingIdleCandidateOne()
        => StartStandingIdleCandidateValidation(DancyStandingIdleExistingModRunner.CandidateOne, result => lastStandingIdleCandidateOne = result);

    public AgentBridgeUiActionResult StartActiveStandingIdleCandidateOneInspection()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var candidate = DancyStandingIdleExistingModRunner.CandidateOne;
        var operation = operations.Begin("dancy.debug.standing-idle-active-inspection", $"Read-only active Standing Idle inspection queued for {candidate.ModName}.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, $"Read-only active Standing Idle inspection is running for {candidate.ModName}.");
                var result = new DancyStandingIdleExistingModRunner().InspectActive(candidate);
                lastStandingIdleActiveCandidateOneInspection = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Active Standing Idle inspection reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "StandingIdleActiveInspectionFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["penumbraStateMutated"] = "false",
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Active Standing Idle inspection crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledStandingIdleActiveInspectionException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok($"Read-only active Standing Idle inspection started for {candidate.ModName}.", operation.Id);
    }

    public AgentBridgeUiActionResult StartStandingIdleCandidateTwo()
        => StartStandingIdleCandidateValidation(DancyStandingIdleExistingModRunner.CandidateTwo, result => lastStandingIdleCandidateTwo = result);

    public AgentBridgeUiActionResult StartStandingIdleProduction()
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.standing-idle-production-e2e", "c0701 Standing Idle production test queued.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, "Dancy is generating and verifying the fixed c0701 Standing Idle output before the external runtime observation window.");
                var result = new DancyStandingIdleProductionRunner().Run();
                lastStandingIdleProduction = result;
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed
                        ? "Standing Idle production output passed structural checks and was cleaned up; independent runtime resource observation is reported separately."
                        : "Standing Idle production end-to-end test reported a failure.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "StandingIdleProductionE2EFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["externalRuntimeObservation"] = "pending",
                        ["dancyTestOptionCleaned"] = result.Passed ? "true" : "unknown",
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Standing Idle production end-to-end test crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledStandingIdleProductionE2EException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok("Standing Idle production end-to-end test started. It will temporarily activate the generated c0701 override for independent runtime observation, then clean up only the test option.", operation.Id);
    }

    private AgentBridgeUiActionResult StartStandingIdleCandidateValidation(
        DancyStandingIdleExistingModRunner.Candidate candidate,
        Action<DancySelfTestResult> setResult)
    {
        if (Interlocked.CompareExchange(ref selfTestRunning, 1, 0) != 0)
            return AgentBridgeUiActionResult.Fail("A Dancy integration test is already running.");

        var operation = operations.Begin("dancy.debug.standing-idle-existing-mod-validation", $"Standing Idle validation queued for {candidate.ModName}.");
        _ = Task.Run(() =>
        {
            try
            {
                operations.Update(operation.Id, AgentBridgeOperationState.Running, $"Standing Idle validation is running for {candidate.ModName}.");
                var result = new DancyStandingIdleExistingModRunner().Run(candidate);
                setResult(result);
                operations.Update(
                    operation.Id,
                    result.Passed ? AgentBridgeOperationState.Succeeded : AgentBridgeOperationState.Failed,
                    result.Passed ? result.Summary : "Standing Idle existing-mod validation reported failures.",
                    current: result.Cases.Count(test => test.Status == DancySelfTestStatus.Passed),
                    total: result.Cases.Count,
                    errorCode: result.Passed ? null : "StandingIdleExistingModValidationFailed",
                    postconditions: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["summary"] = result.Summary,
                        ["penumbraPersistentStateMutated"] = "false",
                        ["temporarySettingRestored"] = result.Passed ? "true" : "unknown",
                    });
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Standing Idle existing-mod validation crashed.");
                operations.Update(operation.Id, AgentBridgeOperationState.Failed, exception.Message, errorCode: "UnhandledStandingIdleExistingModValidationException");
            }
            finally
            {
                Interlocked.Exchange(ref selfTestRunning, 0);
            }
        });

        return AgentBridgeUiActionResult.Ok($"Standing Idle validation started for {candidate.ModName}.", operation.Id);
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
            new AgentBridgeCapabilityDescriptor("dancy.debug.treadmill-selector-regression"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.user-added-compatible-mapping-regression"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.target-catalog"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.multisection-pap-research"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.animation-corpus-research"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.standing-idle-motion-fingerprint-research"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.standing-idle-per-motion-corpus-research"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.standing-idle-active-inspection"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.standing-idle-existing-mod-validation"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.standing-idle-production-e2e"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.step3-performance"),
            new AgentBridgeCapabilityDescriptor("dancy.debug.target-inspection-priority"),
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
        treadmillSelectorRegression = lastTreadmillSelectorRegression,
        userAddedCompatibleMappingRegression = lastUserAddedCompatibleMappingRegression,
        targetCatalogInspection = lastTargetCatalogInspection,
        multiSectionPapResearch = lastMultiSectionResearch,
        animationCorpusResearch = lastCorpusResearch,
        standingIdleMotionFingerprintResearch = lastMotionFingerprintResearch,
        standingIdlePerMotionCorpusResearch = lastPerMotionCorpusResearch,
        standingIdleActiveCandidateOneInspection = lastStandingIdleActiveCandidateOneInspection,
        standingIdleCandidateOne = lastStandingIdleCandidateOne,
        standingIdleCandidateTwo = lastStandingIdleCandidateTwo,
        standingIdleProduction = lastStandingIdleProduction,
        step3PerformanceProfile = lastStep3PerformanceProfile,
        targetInspectionPriorityRegression = lastTargetInspectionPriorityRegression,
        targetInspection = plugin.TargetInspections.GetDebugState(),
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
