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

        Size = new Vector2(360, 120);
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
        var showNonLoopTargets = configuration.ShowNonLoopTargets;
        if (ImGui.Checkbox("Include non-loop target emotes", ref showNonLoopTargets))
        {
            configuration.ShowNonLoopTargets = showNonLoopTargets;
            configuration.Save();
        }

        ImGui.TextDisabled("Normal overrides use loop-capable targets by default.");
        ImGui.TextDisabled("Cross-rig skeletal retargeting is not enabled in this build.");

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
#endif

        if (ImGui.Button("Discord Server"))
            Dalamud.Utility.Util.OpenLink("https://discord.gg/asDM4dh4gz");
    }
}
