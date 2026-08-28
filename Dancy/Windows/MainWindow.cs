using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ECommons.DalamudServices;
using Penumbra.Api.IpcSubscribers;
using Dancy.Core;
using Dancy.Core.Models;
using Dancy.Domain;
using Dancy.Pap;
using Dancy.Files;
using Dancy.Persistence;
using Dancy.Services;

namespace Dancy.Windows
{
    public class MainWindow : Window, IDisposable
    {
        private enum WizardStep
        {
            SelectMod = 0,
            SelectSource = 1,
            SelectTarget = 2
        }

        private readonly Plugin plugin;

        // Penumbra IPC
        private readonly GetModList getModList;
        private readonly ReloadMod reloadMod;

        // State: mods
        private Dictionary<string, string> modList = new();
        private string modSearch = string.Empty;
        private string? selectedModDirectory;
        private string? selectedModName;
        private bool isLoadingMods;
        private bool isScanningMod;
        private string? lastScanError;
        string penRoot = String.Empty;

        // State: emote scanning
        private List<RemappableOption> remappableOptions = new();
        private RemappableOption? selectedOption = null;
        private readonly HashSet<string> selectedSourceGamePaths = new(StringComparer.OrdinalIgnoreCase);

        // State: target emote
        private string emoteSearch = string.Empty;
        private LuminaEmote? selectedReplacementEmote = null;
        private OverridePlan? previewPlan;
        private bool isCreatingOverride;
        private string? lastDiagnostics;
        private readonly OverrideService overrideService = new();

        // Step navigation
        private WizardStep currentStep = WizardStep.SelectMod;

        public MainWindow(Plugin pl)
            : base("Dancy", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
        {
            plugin = pl;

            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(900, 550),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };

            getModList = new GetModList(Plugin.PluginInterface);
            reloadMod = new ReloadMod(Plugin.PluginInterface);

            _ = LoadModListAsync();
        }

        public void Dispose()
        {
        }

        // ======================================
        // Backend: Load mod list
        // ======================================
        private async Task LoadModListAsync()
        {
            penRoot = PenumbraDirectoryResolver.GetPenumbraDirectory() ?? string.Empty;
            isLoadingMods = true;
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        modList = getModList.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Svc.Chat.PrintError($"[Dancy] Failed to load mod list: {ex.Message}");
                        modList = new Dictionary<string, string>();
                    }
                });
            }
            finally
            {
                isLoadingMods = false;
            }
        }

        // ======================================
        // Backend: Scan selected mod
        // ======================================
        private async Task ScanSelectedModAsync()
        {
            if (selectedModDirectory == null)
                return;

            if (!modList.TryGetValue(selectedModDirectory, out var _))
                return;

            isScanningMod = true;
            lastScanError = null;
            remappableOptions.Clear();
            selectedOption = null;
            selectedSourceGamePaths.Clear();
            selectedReplacementEmote = null;
            previewPlan = null;

            await Task.Run(() =>
            {
                try
                {
                    var penRoot = PenumbraDirectoryResolver.GetPenumbraDirectory();
                    if (string.IsNullOrEmpty(penRoot))
                    {
                        lastScanError = "Could not locate Penumbra mod directory (Penumbra.json missing or invalid).";
                        return;
                    }

                    if (!PathSafety.TryResolveInsideRoot(penRoot, selectedModDirectory, out var modFolder))
                    {
                        lastScanError = "Dancy refused an unsafe Penumbra mod directory.";
                        return;
                    }
                    if (!Directory.Exists(modFolder))
                    {
                        lastScanError = $"Mod folder does not exist: {modFolder}";
                        return;
                    }

                    var options = EmoteOverrideScanner.ScanMod(modFolder);
                    remappableOptions = options;

                    if (remappableOptions.Count > 0)
                        currentStep = WizardStep.SelectSource;
                }
                catch (Exception ex)
                {
                    lastScanError = ex.Message;
                }
            });

            isScanningMod = false;
        }

        public override void OnOpen()
        {
            base.OnOpen();
            _ = LoadModListAsync();
        }

        // ======================================
        // Draw root
        // ======================================
        public override void Draw()
        {
            var totalHeight = ImGui.GetContentRegionAvail().Y;

            float headerHeight = totalHeight * 0.15f;
            float contentHeight = totalHeight * 0.70f;
            float footerHeight = totalHeight * 0.15f;

            // =========================
            // HEADER (15%)
            // =========================
            using (ImRaii.Child("DancyHeader", new Vector2(-1, headerHeight), false))
            {
                DrawHeader();
                ImGui.Spacing();
                DrawStepNavigation();
            }

            // =========================
            // MAIN CONTENT (70%)
            // =========================
            using (ImRaii.Child("DancyContent", new Vector2(-1, contentHeight), true))
            {
                switch (currentStep)
                {
                    case WizardStep.SelectMod:
                        DrawStepCard_SelectMod();
                        break;

                    case WizardStep.SelectSource:
                        DrawStepCard_SelectSource();
                        break;

                    case WizardStep.SelectTarget:
                        DrawStepCard_SelectTarget();
                        break;
                }
            }

            // =========================
            // FOOTER (15%)
            // =========================
            using (ImRaii.Child("DancyFooter", new Vector2(-1, footerHeight), false))
            {
                DrawFooter();
            }
        }

        private void DrawFooter()
        {
            ImGui.Separator();

            if (!string.IsNullOrWhiteSpace(lastDiagnostics) && ImGui.Button("Copy diagnostics"))
                ImGui.SetClipboardText(lastDiagnostics);

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.65f, 0.0f, 1.0f));
            ImGui.TextWrapped(
                "Dancy is currently in Early Access. "
              + "Some mods or emotes may not fully work yet. "
              + "Please join the Discord if you encounter issues or have suggestions."
            );
            ImGui.PopStyleColor();

            ImGui.Spacing();

            if (ImGui.Button("Join the Dancy Discord"))
            {
                Util.OpenLink("https://discord.gg/asDM4dh4gz");
            }
        }



        // ======================================
        // Header + links
        // ======================================
        private void DrawHeader()
        {
            // Title
            ImGui.TextColored(new Vector4(0.85f, 0.9f, 1.0f, 1.0f), "Dancy – Emote Override Wizard");

            // Ko-fi + Discord aligned right
            float rightWidth = 170f;
            float regionMaxX = ImGui.GetWindowContentRegionMax().X;
            float rightX = regionMaxX - rightWidth;
            if (rightX > ImGui.GetCursorPosX())
                ImGui.SameLine(rightX);
            else
                ImGui.SameLine();

            // Ko-fi heart
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.Button(FontAwesomeIcon.Heart.ToIconString()))
                {
                    Util.OpenLink("https://ko-fi.com/kkcuy");
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Support Dancy on Ko-fi ♥");

            ImGui.SameLine();

            // Discord "badge"
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.8f, 1f, 1f));
            ImGui.Text("[Discord]");
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open Dancy support / feedback Discord");
            if (ImGui.IsItemClicked())
            {
                Util.OpenLink("https://discord.gg/asDM4dh4gz");
            }

            ImGui.Spacing();

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
            ImGui.TextWrapped("Dancy creates its own override group and PAP copies inside each mod. Original options and files stay untouched.");
            ImGui.PopStyleColor();

            if (!string.IsNullOrEmpty(lastScanError))
            {
                ImGui.Spacing();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
                ImGui.TextWrapped($"Error: {lastScanError}");
                ImGui.PopStyleColor();
            }
        }

        // ======================================
        // Step navigation bar
        // ======================================
        private void DrawStepNavigation()
        {
            bool hasMod = selectedModDirectory != null;
            var filtered = remappableOptions;
            bool hasSource = hasMod && filtered.Count > 0;
            bool hasSelectedSource = hasSource && selectedOption != null && filtered.Contains(selectedOption);
            bool hasTarget = hasSelectedSource && selectedReplacementEmote != null;

            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 5f));

            DrawStepButton("1. Mod", WizardStep.SelectMod, true, hasMod);
            ImGui.SameLine();
            DrawStepButton("2. Source option", WizardStep.SelectSource, hasSource, hasSelectedSource);
            ImGui.SameLine();
            DrawStepButton("3. Target emote", WizardStep.SelectTarget, hasSelectedSource, hasTarget);

            ImGui.PopStyleVar(2);
        }

        private void DrawStepButton(string label, WizardStep step, bool enabled, bool done)
        {
            var accent = new Vector4(0.25f, 0.55f, 0.95f, 1f);
            var accentDone = new Vector4(0.25f, 0.7f, 0.4f, 1f);
            var baseColor = new Vector4(0.18f, 0.18f, 0.22f, 1f);

            float alpha = enabled ? 1f : 0.4f;
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);

            bool isCurrent = currentStep == step;
            var color = isCurrent ? accent : (done ? accentDone : baseColor);

            ImGui.PushStyleColor(ImGuiCol.Button, color);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);

            if (ImGui.Button(label) && enabled)
                currentStep = step;

            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar();
        }

        // ======================================
        // Card helpers
        // ======================================
        private void BeginCard(string id, string title, string subtitle = "")
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10f);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.07f, 0.07f, 0.11f, 1f));

            ImGui.BeginChild(id, new Vector2(0, 0), true);

            ImGui.TextColored(new Vector4(0.9f, 0.9f, 1f, 1f), title);
            if (!string.IsNullOrEmpty(subtitle))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.75f, 0.75f, 0.8f, 1f));
                ImGui.TextWrapped(subtitle);
                ImGui.PopStyleColor();
            }

            ImGui.Separator();
            ImGui.Spacing();
        }

        private void EndCard()
        {
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);
        }

        // ======================================
        // STEP 1 – Mod selection
        // ======================================
        private void DrawStepCard_SelectMod()
        {
            BeginCard("DancyStepMod", "Step 1 – Select source mod",
                "Select the Penumbra mod that contains the dance / emote you want to rebind.");

            if (isLoadingMods)
            {
                ImGui.Text("Loading mods...");
                EndCard();
                return;
            }

            ImGui.PushItemWidth(320f);
            ImGui.InputText("Search mods", ref modSearch, 200);
            ImGui.PopItemWidth();
            ImGui.SameLine();
            if (ImGui.Button("Refresh list"))
            {
                _ = LoadModListAsync();
            }

            ImGui.Spacing();

            var flags = ImGuiTableFlags.RowBg
                        | ImGuiTableFlags.BordersInnerV
                        | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("DancyModTable", 2, flags))
            {
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Folder");
                ImGui.TableHeadersRow();

                foreach (var kv in modList)
                {
                    var dir = kv.Key;
                    var name = kv.Value;

                    if (!string.IsNullOrEmpty(modSearch)
                        && !name.Contains(modSearch, StringComparison.OrdinalIgnoreCase)
                        && !dir.Contains(modSearch, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    bool isSelected = selectedModDirectory == dir;
                    string label = $"{name}##{dir}";

                    if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        selectedModDirectory = dir;
                        selectedModName = name;
                        remappableOptions.Clear();
                        selectedOption = null;
                        selectedSourceGamePaths.Clear();
                        selectedReplacementEmote = null;
                        previewPlan = null;
                        lastScanError = null;
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(dir);
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();

            if (selectedModDirectory == null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.75f, 0.75f, 0.75f, 1f));
                ImGui.Text("Select a mod above to continue.");
                ImGui.PopStyleColor();
                EndCard();
                return;
            }

            ImGui.Text($"Selected: {selectedModName} ({selectedModDirectory})");

            if (ImGui.Button(isScanningMod ? "Scanning..." : "Scan mod for emote overrides"))
            {
                if (!isScanningMod)
                    _ = ScanSelectedModAsync();
            }

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
            ImGui.TextDisabled("Reads JSON + PAP in this mod to find emote-based overrides.");
            ImGui.PopStyleColor();

            if (PathSafety.TryResolveInsideRoot(penRoot, selectedModDirectory, out var selectedModPath)
                && DancyFileManager.DancyExists(selectedModPath))
            {
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.Spacing();
                // Make the remove button red
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.80f, 0.10f, 0.15f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.90f, 0.20f, 0.20f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.70f, 0.05f, 0.10f, 1.0f));

                if (ImGui.Button("Remove Dancy from mod"))
                {
                    bool isDeleted = DancyFileManager.RemoveDancy(selectedModPath);
                    if (isDeleted)
                        Svc.Chat.Print("[Dancy] Removed existing Dancy overrides from the selected mod.");
                    else
                        Svc.Chat.Print("[Dancy] No existing Dancy overrides found in the selected mod.");

                    if (!TryReloadMod(selectedModDirectory))
                        Svc.Chat.Print("[Dancy] Penumbra reload did not complete. The Changed Items tab may stay stale until you use Refresh Data, reload the mod, or restart.");
                }

                ImGui.PopStyleColor(3); // Button, ButtonHovered, ButtonActive
            }

            EndCard();
        }

        // ======================================
        // STEP 2 – Source option selection
        // ======================================
        private void DrawStepCard_SelectSource()
        {
            BeginCard("DancyStepSource", "Step 2 – Choose source option",
                "Pick the mod option whose emote animation you want to remap.");

            if (selectedModDirectory == null)
            {
                ImGui.Text("No mod selected yet (Step 1).");
                EndCard();
                return;
            }

            if (isScanningMod)
            {
                ImGui.Text("Scanning mod...");
                EndCard();
                return;
            }

            if (remappableOptions.Count == 0)
            {
                ImGui.Text("No emote-based PAP overrides detected in this mod.");
                EndCard();
                return;
            }

            ImGui.Spacing();

            var filtered = remappableOptions;

            if (filtered.Count == 0)
            {
                ImGui.Text("No safe options found with single PAP source.");
                EndCard();
                return;
            }

            var flags = ImGuiTableFlags.RowBg
                        | ImGuiTableFlags.BordersInnerV
                        | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("DancyOverrideTable", 3, flags))
            {
                ImGui.TableSetupColumn("Group");
                ImGui.TableSetupColumn("Option");
                ImGui.TableSetupColumn("Emotes");
                ImGui.TableHeadersRow();

                foreach (var opt in filtered)
                {
                    bool isSelected = ReferenceEquals(selectedOption, opt);

                    string emotesDisplay = string.Join(", ",
                        opt.Entries.Select(e =>
                            string.IsNullOrEmpty(e.EmoteCommand)
                                ? e.EmoteName
                                : $"{e.EmoteName} ({e.EmoteCommand})"));

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    var label = $"{opt.GroupName}##{opt.GroupName}_{opt.OptionName}";

                    if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        selectedOption = opt;
                        ResetSelectedSourceGamePaths(opt);
                        selectedReplacementEmote = null;
                        previewPlan = null;
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(opt.OptionName);

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(emotesDisplay);
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();

            if (selectedOption == null || !filtered.Contains(selectedOption))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
                ImGui.Text("Select a row above to see details and continue to Step 3.");
                ImGui.PopStyleColor();
                EndCard();
                return;
            }

            var selected = selectedOption;
            EnsureSelectedSourceGamePaths(selected);

            ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Selected option");
            ImGui.Text($"Group:  {selected.GroupName}");
            ImGui.Text($"Option: {selected.OptionName}");

            ImGui.Spacing();
            ImGui.Text("Affected emotes / files:");

            var flagsDetails = ImGuiTableFlags.RowBg
                               | ImGuiTableFlags.BordersInnerV
                               | ImGuiTableFlags.SizingStretchSame;

            if (ImGui.BeginTable("DancySelectedOptionTable", 6, flagsDetails))
            {
                ImGui.TableSetupColumn("Use");
                ImGui.TableSetupColumn("Emote");
                ImGui.TableSetupColumn("Applies to");
                ImGui.TableSetupColumn("PAP origin");
                ImGui.TableSetupColumn("Phase");
                ImGui.TableSetupColumn("Game path");
                ImGui.TableHeadersRow();

                foreach (var (e, index) in selected.Entries.Select((Entry, Index) => (Entry, Index)))
                {
                    ImGui.TableNextRow();
                    ImGui.PushID($"DancySourceEntry_{index}");

                    ImGui.TableNextColumn();
                    var include = selectedSourceGamePaths.Contains(e.GamePath);
                    if (ImGui.Checkbox("##UseSourcePath", ref include))
                    {
                        if (include)
                            selectedSourceGamePaths.Add(e.GamePath);
                        else
                            selectedSourceGamePaths.Remove(e.GamePath);

                        previewPlan = null;
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton("Only"))
                    {
                        selectedSourceGamePaths.Clear();
                        selectedSourceGamePaths.Add(e.GamePath);
                        previewPlan = null;
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(string.IsNullOrWhiteSpace(e.EmoteCommand)
                        ? e.EmoteName
                        : $"{e.EmoteName}\n{e.EmoteCommand}");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(e.AppliesTo.Character.DisplayName);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(e.PapOrigin.IsKnown ? e.PapOrigin.DisplayName : "Mod-relative PAP");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(e.AppliesTo.Phase.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped($"{e.GamePath}\n{e.ModdedPapPath}");
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            var selectedSourceEntries = GetSelectedSourceEntries(selected);
            ImGui.Spacing();

            if (selectedSourceEntries.Count == 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.45f, 1f));
                ImGui.Text("Select at least one source path to continue.");
                ImGui.PopStyleColor();
            }
            else if (ImGui.Button($"Continue with {selectedSourceEntries.Count} source path{(selectedSourceEntries.Count == 1 ? string.Empty : "s")}"))
            {
                selectedReplacementEmote = null;
                previewPlan = null;
                currentStep = WizardStep.SelectTarget;
            }

            EndCard();
        }

        // ======================================
        // STEP 3 – Target emote selection
        // ======================================
        private void DrawStepCard_SelectTarget()
        {
            BeginCard("DancyStepTarget", "Step 3 – Choose target emote and create override",
                "Select the emote you want to use as the new trigger, then let Dancy generate an override option.");

            if (selectedModDirectory == null)
            {
                ImGui.Text("No mod selected yet (Step 1).");
                EndCard();
                return;
            }

            var filtered = remappableOptions;

            if (filtered.Count == 0)
            {
                ImGui.Text("No source option available. Run Step 2 first.");
                EndCard();
                return;
            }

            if (selectedOption == null || !filtered.Contains(selectedOption))
            {
                ImGui.Text("No source option selected (Step 2).");
                EndCard();
                return;
            }

            var opt = selectedOption;
            EnsureSelectedSourceGamePaths(opt);
            var selectedSourceEntries = GetSelectedSourceEntries(opt);

            ImGui.Text($"Source: ({opt.GroupName}) {opt.OptionName}");
            ImGui.Text($"Selected source paths: {selectedSourceEntries.Count} / {opt.Entries.Count}");
            ImGui.Spacing();

            ImGui.PushItemWidth(320f);
            ImGui.InputText("Search target emote", ref emoteSearch, 100);
            ImGui.PopItemWidth();

            var showNonLoopTargets = plugin.Configuration.ShowNonLoopTargets;
            if (ImGui.Checkbox("Include non-loop targets", ref showNonLoopTargets))
            {
                plugin.Configuration.ShowNonLoopTargets = showNonLoopTargets;
                plugin.Configuration.Save();
            }

            ImGui.Spacing();

            var targetCatalog = EmoteLibrary.AllEmotes
                .Where(emote => TargetEmotePolicy.IsSelectable(plugin.Configuration.ShowNonLoopTargets, emote.IsLoopCapable))
                .ToList();
            if (!plugin.Configuration.ShowNonLoopTargets
                && selectedReplacementEmote != null
                && !selectedReplacementEmote.IsLoopCapable)
            {
                selectedReplacementEmote = null;
                previewPlan = null;
            }

            var results = targetCatalog
                .Where(e => string.IsNullOrEmpty(emoteSearch)
                         || e.Name.Contains(emoteSearch, StringComparison.OrdinalIgnoreCase)
                         || e.Command.Contains(emoteSearch, StringComparison.OrdinalIgnoreCase))
                .Take(50)
                .ToList();

            if (results.Count == 0)
            {
                ImGui.Text("No matching emotes found.");
                EndCard();
                return;
            }

            var flags = ImGuiTableFlags.RowBg
                        | ImGuiTableFlags.BordersInnerV
                        | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("DancyEmoteTable", 2, flags))
            {
                ImGui.TableSetupColumn("Emote");
                ImGui.TableSetupColumn("Command");
                ImGui.TableHeadersRow();

                foreach (var emote in results)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    bool isSelected = ReferenceEquals(selectedReplacementEmote, emote);
                    string label = $"{emote.Name}##{emote.RowId}";

                    if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        selectedReplacementEmote = emote;
                        previewPlan = null;
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(emote.Command);
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();

            if (selectedReplacementEmote != null)
            {
                var looksLikeLoop = selectedReplacementEmote.IsLoopCapable;
                previewPlan ??= CreatePreviewPlan(opt, selectedReplacementEmote, selectedSourceEntries);

                // Warning / explanation box
                ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
                ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.18f, 0.15f, 0.05f, 0.6f));
                ImGui.BeginChild("DancyWarningBox", new Vector2(0, 70), true);

                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.9f, 0.6f, 1f));
                if (looksLikeLoop)
                {
                    ImGui.TextWrapped(
                        "Note: Dancy does not change how long the emote runs.\n" +
                        "Looped dances are ideal targets. One-shot emotes like /wave or /love will still stop after their normal short duration.\n"+
                                "If you notice timing issues at the start of some animations: this is a known limitation and I am actively working on a solution.\n" +
        "As a workaround, try using looped dances like Beesknees or Gold Dance.");
                }
                else
                {
                    ImGui.TextWrapped(
                        "Warning: This target does not look like a looped dance.\n" +
                        "If you map a full dance mod to a one-shot emote (e.g. /wave, /blowkiss, /love), " +
                        "the animation will still end quickly. That is normal game behavior, not a Dancy bug.\n"+
                                "If you notice timing issues at the start of some animations: this is a known limitation and I am actively working on a solution.\n" +
        "As a workaround, try using looped dances like Beesknees or Gold Dance.");
                }
                ImGui.PopStyleColor();

                ImGui.EndChild();
                ImGui.PopStyleColor();
                ImGui.PopStyleVar();

                ImGui.Spacing();

                ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Summary");
                ImGui.Text($"Source: ({opt.GroupName}) {opt.OptionName}");
                ImGui.Text($"Source paths: {selectedSourceEntries.Count} selected");
                ImGui.Text($"Target: {selectedReplacementEmote.Name} ({selectedReplacementEmote.Command})");
                ImGui.TextWrapped($"Target timeline: {selectedReplacementEmote.PrimaryTimelineKey}");

                DrawMappingPreview(previewPlan);

                ImGui.Spacing();

                if (selectedSourceEntries.Count == 0)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.45f, 1f));
                    ImGui.Text("Select at least one source path in Step 2.");
                    ImGui.PopStyleColor();
                }
                else if (!previewPlan.IsValid)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.45f, 1f));
                    ImGui.TextWrapped(string.Join("\n", previewPlan.Errors));
                    ImGui.PopStyleColor();
                }
                else if (ImGui.Button(isCreatingOverride ? "Creating override..." : "Create Dancy override") && !isCreatingOverride)
                {
                    _ = CreateOverrideAsync(opt, selectedReplacementEmote, selectedSourceEntries, previewPlan);
                }

                if (selectedSourceEntries.Count > 0)
                {
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
                    ImGui.TextDisabled("Creates a new Dancy group and PAP copy. Original files remain untouched.");
                    ImGui.PopStyleColor();
                }
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
                ImGui.Text("Select a target emote above to continue.");
                ImGui.PopStyleColor();
            }

            EndCard();
        }

        // ======================================
        // Override creation backend
        // ======================================
        private void ResetSelectedSourceGamePaths(RemappableOption source)
        {
            selectedSourceGamePaths.Clear();
            foreach (var entry in source.Entries)
                selectedSourceGamePaths.Add(entry.GamePath);
        }

        private void EnsureSelectedSourceGamePaths(RemappableOption source)
        {
            var validPaths = source.Entries
                .Select(e => e.GamePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            selectedSourceGamePaths.RemoveWhere(path => !validPaths.Contains(path));

            if (selectedSourceGamePaths.Count == 0 && source.Entries.Count == 1)
                selectedSourceGamePaths.Add(source.Entries[0].GamePath);
        }

        private List<ParsedEmoteOverride> GetSelectedSourceEntries(RemappableOption source)
            => source.Entries
                .Where(e => selectedSourceGamePaths.Contains(e.GamePath))
                .ToList();

        private OverridePlan CreatePreviewPlan(
            RemappableOption source,
            LuminaEmote target,
            IReadOnlyList<ParsedEmoteOverride> sourceEntries)
        {
            var targetPaths = PapResolver.ResolvePapFiles(target.PrimaryTimelineKey);
            return OverridePlanner.Create(new OverridePlanRequest
            {
                ModIdentity = selectedModDirectory ?? string.Empty,
                SourceGroupName = source.GroupName,
                SourceOptionName = source.OptionName,
                TargetTimelineKey = target.PrimaryTimelineKey,
                TargetName = target.Name,
                TargetCommand = target.Command,
                Sources = sourceEntries
                    .Select(entry => new OverridePlanSource(entry.GamePath, entry.ModdedPapPath))
                    .ToList(),
                TargetGamePaths = targetPaths,
            });
        }

        private static void DrawMappingPreview(OverridePlan plan)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Mapping preview");
            if (!plan.IsValid)
            {
                ImGui.TextDisabled("No metadata will be changed until this plan is valid.");
                return;
            }

            foreach (var copy in plan.PapCopies)
            {
                var strategies = string.Join(", ", copy.MatchResults
                    .Select(result => result.Strategy.ToString())
                    .Distinct(StringComparer.Ordinal));
                ImGui.TextWrapped($"{copy.SourcePapPath} -> {copy.TargetGamePaths.Count} target path(s) [{strategies}]");
            }

            foreach (var warning in plan.Warnings)
                ImGui.TextWrapped($"Warning: {warning}");
        }

        private async Task CreateOverrideAsync(
            RemappableOption source,
            LuminaEmote target,
            IReadOnlyList<ParsedEmoteOverride> sourceEntries,
            OverridePlan plan)
        {
            if (selectedModDirectory == null || isCreatingOverride)
                return;

            if (!modList.ContainsKey(selectedModDirectory) || !plan.IsValid)
                return;

            isCreatingOverride = true;
            try
            {
                await Task.Run(() =>
                {
                    var modDirectory = selectedModDirectory;
                    if (modDirectory == null)
                        return;

                    if (sourceEntries.Count == 0)
                        throw new InvalidOperationException("No source paths were selected.");

                    var penumbraRoot = PenumbraDirectoryResolver.GetPenumbraDirectory();
                    if (string.IsNullOrWhiteSpace(penumbraRoot))
                        throw new InvalidOperationException("Could not locate the Penumbra mod directory.");
                    if (!PathSafety.TryResolveInsideRoot(penumbraRoot, modDirectory, out var modFolder))
                        throw new InvalidOperationException("Dancy refused an unsafe Penumbra mod directory.");
                    if (!Directory.Exists(modFolder))
                        throw new DirectoryNotFoundException("The selected Penumbra mod folder does not exist.");

                    var operation = overrideService.CreateOrUpdate(modFolder, modDirectory, selectedModName ?? string.Empty, plan);
                    lastDiagnostics = BuildDiagnostics(source, target, plan, operation.Execution, operation.Write, operation.Reload.Succeeded);
                    Svc.Chat.Print(operation.Reload.Succeeded
                        ? "[Dancy] Override created successfully."
                        : "[Dancy] Override created successfully, but Penumbra reload did not complete. Reload the mod manually if its UI still looks stale.");
                });
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[Dancy] Override creation failed.");
                lastDiagnostics = BuildFailureDiagnostics(source, target, plan, ex);
                Svc.Chat.PrintError($"[Dancy] Override creation failed: {ex.Message}");
            }
            finally
            {
                isCreatingOverride = false;
            }
        }

        private string BuildDiagnostics(
            RemappableOption source,
            LuminaEmote target,
            OverridePlan plan,
            OverrideExecutionResult execution,
            Penumbra.PenumbraWriteResult write,
            bool reloaded)
        {
            var diagnostics = new StringBuilder();
            diagnostics.AppendLine("Dancy diagnostics");
            diagnostics.AppendLine($"Build: {typeof(Plugin).Assembly.GetName().Version}");
            diagnostics.AppendLine($"Build MVID: {typeof(Plugin).Module.ModuleVersionId}");
            diagnostics.AppendLine($"Override: {plan.OverrideId}");
            diagnostics.AppendLine($"Source: ({source.GroupName}) {source.OptionName}");
            diagnostics.AppendLine($"Target: {target.Name} ({target.Command}) [{target.PrimaryTimelineKey}]");
            diagnostics.AppendLine($"Metadata: {write.Format} ({Path.GetFileName(write.MetadataPath)})");
            diagnostics.AppendLine($"Penumbra reload: {(reloaded ? "succeeded" : "not confirmed")}");

            foreach (var copy in plan.PapCopies)
            {
                diagnostics.AppendLine($"Source PAP: {copy.SourcePapPath}");
                diagnostics.AppendLine($"  paths: {string.Join(", ", copy.SourceGamePaths)}");
                diagnostics.AppendLine($"  strategy: {string.Join(", ", copy.MatchResults.Select(result => result.Strategy).Distinct())}");
            }

            foreach (var patch in execution.PapResults)
                diagnostics.AppendLine($"Patched: {patch.TargetGamePath} -> {patch.EventIdentifier} ({patch.PatchedTimelineEntries} timeline entries)");
            foreach (var warning in plan.Warnings)
                diagnostics.AppendLine($"Warning: {warning}");

            return diagnostics.ToString();
        }

        private string BuildFailureDiagnostics(RemappableOption source, LuminaEmote target, OverridePlan plan, Exception exception)
        {
            var diagnostics = new StringBuilder();
            diagnostics.AppendLine("Dancy diagnostics");
            diagnostics.AppendLine($"Build: {typeof(Plugin).Assembly.GetName().Version}");
            diagnostics.AppendLine($"Build MVID: {typeof(Plugin).Module.ModuleVersionId}");
            diagnostics.AppendLine($"Override: {plan.OverrideId}");
            diagnostics.AppendLine($"Source: ({source.GroupName}) {source.OptionName}");
            diagnostics.AppendLine($"Target: {target.Name} ({target.Command}) [{target.PrimaryTimelineKey}]");
            diagnostics.AppendLine("Result: failed before metadata was committed.");
            foreach (var warning in plan.Warnings)
                diagnostics.AppendLine($"Warning: {warning}");
            diagnostics.AppendLine(exception.ToString());
            return diagnostics.ToString();
        }

        private bool TryReloadMod(string modDirectory)
        {
            try
            {
                var modName = modList.TryGetValue(modDirectory, out var name)
                    ? name
                    : selectedModName ?? string.Empty;

                reloadMod.Invoke(modDirectory, modName);
                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, $"[Dancy] Failed to reload Penumbra mod {modDirectory}.");
                return false;
            }
        }
    }
}
