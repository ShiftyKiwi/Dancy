using Dalamud.Configuration;
using System;

namespace Dancy;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool ShowNonLoopTargets { get; set; }

    public string PenumbraPath { get; set; } = string.Empty;

    // Debug-only DAB bridge identity. The protected token is DPAPI encrypted by AgentBridge.
    public string AgentBridgeInstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public string AgentBridgeProtectedAccessToken { get; set; } = string.Empty;

    // The below exist just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
