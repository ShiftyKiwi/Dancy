using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dancy.Penumbra;
using Dancy.Windows;
using ECommons;
using ECommons.DalamudServices;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Penumbra;
using System.Collections.Generic;
using System.IO;

namespace Dancy;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/dancy";


    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("Dancy");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ECommonsMain.Init(PluginInterface, this);
        //PenumbraIpc.Initialize(PluginInterface);

        // You might normally want to embed resources and load them from the manifest stream

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Main Window"
        });

        // Tell the UI system that we want our windows to be drawn throught he window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [Dancy] ===A cool log message from Dancy===

        Core.EmoteLibrary.Initialize();

        TryLoadPenumbraPath();
    }

    private void TryLoadPenumbraPath()
    {
        var configDir = PluginInterface.ConfigDirectory.FullName;
        var result = PenumbraPathResolver.ResolvePenumbraModDirectory(configDir);

        if (!string.IsNullOrEmpty(result))
        {
            Configuration.PenumbraPath = result;
            Configuration.Save();
        }
        else
        {
            // Optional: notify user
            Svc.Chat.PrintError("[Dancy] Could not locate Penumbra mod directory.");
        }
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anythign during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }

    //public void LoadEmotes()
    //{
    //    // Holen des ExcelSheets mit Emotes aus dem DataManager
    //    ExcelSheet<Emote> emotes = DataManager.GetExcelSheet<Emote>(null, null);

    //    // Überprüfen, ob das ExcelSheet erfolgreich geladen wurde
    //    if (emotes != null)
    //    {
    //        // Durchlaufen aller Emotes im ExcelSheet
    //        foreach (Emote emote in emotes)
    //        {
    //            // Standardwert für den Emote-Befehl
    //            string emoteCommand = "Unknown";

    //            // Überprüfen, ob der Emote einen gültigen TextCommand hat
    //            if (emote.TextCommand.ValueNullable.HasValue)
    //            {
    //                // Extrahieren des Befehls aus dem TextCommand
    //                emoteCommand = emote.TextCommand.Value.Command.ExtractText();
    //            }

    //            // Extrahieren der RowId des Emotes
    //            uint rowId = emote.RowId;

    //            // Extrahieren des Namens des Emotes
    //            string emoteName = emote.Name.ExtractText();

    //            var emotePaths = emote.

    //            // Erstellen einer Liste mit dem Emote-Namen und -Befehl
    //            List<string> emoteDetails = new List<string> { emoteName, emoteCommand };

    //            // Hinzufügen der Emote-Details zur Dictionary mit RowId als Schlüssel
    //            emoteNames[rowId] = emoteDetails;
    //        }
    //    }
    //}

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
