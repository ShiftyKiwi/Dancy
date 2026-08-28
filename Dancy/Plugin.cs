using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using Dancy.Penumbra;
using Dancy.Windows;
#if DEBUG
using Dancy.Diagnostics;
#endif
using ECommons;
using ECommons.DalamudServices;

namespace Dancy;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/dancy";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("Dancy");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
#if DEBUG
    internal DancyAgentBridge AgentBridge { get; }
#endif

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        ECommonsMain.Init(PluginInterface, this);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
#if DEBUG
        AgentBridge = new DancyAgentBridge(this);
#endif
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Dancy override wizard",
        });

        PluginInterface.UiBuilder.Draw += DrawWindows;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Core.EmoteLibrary.Initialize();
        TryLoadPenumbraPath();
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawWindows;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        MainWindow.Dispose();
#if DEBUG
        AgentBridge.Dispose();
#endif
        CommandManager.RemoveHandler(CommandName);
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void OpenConfigUi() => ConfigWindow.IsOpen = true;
    public void ToggleMainUi() => MainWindow.Toggle();

    private void TryLoadPenumbraPath()
    {
        var result = PenumbraPathResolver.ResolvePenumbraModDirectory(PluginInterface.ConfigDirectory.FullName);
        if (!string.IsNullOrEmpty(result))
        {
            Configuration.PenumbraPath = result;
            Configuration.Save();
            return;
        }

        Svc.Chat.PrintError("[Dancy] Could not locate Penumbra mod directory.");
    }

    private void OnCommand(string command, string arguments)
    {
#if DEBUG
        if (string.Equals(arguments.Trim(), "debug selftest", StringComparison.OrdinalIgnoreCase))
        {
            AgentBridge.StartSelfTest();
            return;
        }
#endif
        MainWindow.Toggle();
    }

    private void DrawWindows()
    {
#if DEBUG
        AgentBridge.BeginUiFrame();
        try
        {
            WindowSystem.Draw();
        }
        finally
        {
            AgentBridge.EndUiFrame();
        }
#else
        WindowSystem.Draw();
#endif
    }
}
