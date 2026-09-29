using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;

namespace Dancy.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Plugin plugin;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("Dancy Support")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

#if DEBUG
        Size = new Vector2(420, 640);
#else
        Size = new Vector2(360, 245);
#endif
        SizeCondition = ImGuiCond.Always;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
#if DEBUG
        ImGui.Separator();
        var canRunSelfTest = !plugin.AgentBridge.IsSelfTestRunning;
        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button("Run Dancy integration self-test");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterSelfTestControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (clicked)
            plugin.AgentBridge.StartSelfTest();
        var selfTestSummary = plugin.AgentBridge.LastSelfTestSummary;
        if (!string.IsNullOrWhiteSpace(selfTestSummary))
            ImGui.TextWrapped(selfTestSummary);

        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var ranPushupsWaterRegression = ImGui.Button("Run Push-ups to Water production regression");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterPushupsWaterRegressionControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (ranPushupsWaterRegression)
            plugin.AgentBridge.StartPushupsWaterRegression();

        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var inspectedTargetCatalog = ImGui.Button("Inspect current target catalog");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterTargetCatalogInspectionControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (inspectedTargetCatalog)
            plugin.AgentBridge.StartTargetCatalogInspection();

        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var inspectedMultiSectionPaps = ImGui.Button("Inspect multi-section PAP research fixtures");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterMultiSectionResearchControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (inspectedMultiSectionPaps)
            plugin.AgentBridge.StartMultiSectionResearch();

        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var indexedCorpus = ImGui.Button("Index configured animation corpus");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterCorpusResearchControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (indexedCorpus)
            plugin.AgentBridge.StartCorpusResearch();

        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var fingerprintedStandingIdle = ImGui.Button("Fingerprint Standing Idle Havok motions");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterMotionFingerprintResearchControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (fingerprintedStandingIdle)
            plugin.AgentBridge.StartMotionFingerprintResearch();

        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var indexedPerMotionCorpus = ImGui.Button("Index Standing Idle per-motion corpus");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterPerMotionCorpusResearchControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (indexedPerMotionCorpus)
            plugin.AgentBridge.StartPerMotionCorpusResearch();

        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var validateStandingIdleCandidateOne = ImGui.Button("Validate Male Miqo Standing Idle candidate");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterStandingIdleCandidateOneControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (validateStandingIdleCandidateOne)
            plugin.AgentBridge.StartStandingIdleCandidateOne();

        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var inspectActiveStandingIdleCandidateOne = ImGui.Button("Inspect active ogRayrei Male Miqo idle");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterStandingIdleActiveCandidateOneInspectionControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (inspectActiveStandingIdleCandidateOne)
            plugin.AgentBridge.StartActiveStandingIdleCandidateOneInspection();

        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var validateStandingIdleCandidateTwo = ImGui.Button("Validate second Male Miqo Standing Idle candidate");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterStandingIdleCandidateTwoControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (validateStandingIdleCandidateTwo)
            plugin.AgentBridge.StartStandingIdleCandidateTwo();

        if (!canRunSelfTest)
            ImGui.BeginDisabled();
        var runStandingIdleProduction = ImGui.Button("Run c0701 Standing Idle production test");
        if (!canRunSelfTest)
            ImGui.EndDisabled();
        plugin.AgentBridge.RegisterStandingIdleProductionControl(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), canRunSelfTest);
        if (runStandingIdleProduction)
            plugin.AgentBridge.StartStandingIdleProduction();
#endif

        if (ImGui.Button("Discord Server"))
            Dalamud.Utility.Util.OpenLink("https://discord.gg/asDM4dh4gz");
    }
}
