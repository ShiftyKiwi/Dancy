using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
using Dancy.Pap;
using Dancy.Files;

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
        private bool showUnsafeOptions = false;
        private RemappableOption? selectedOption = null;

        // State: target emote
        private string emoteSearch = string.Empty;
        private LuminaEmote? selectedReplacementEmote = null;

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
            penRoot = PenumbraDirectoryResolver.GetPenumbraDirectory();
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
            selectedReplacementEmote = null;

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

                    var modFolder = Path.Combine(penRoot, selectedModDirectory);
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
            var filtered = remappableOptions.Where(o => showUnsafeOptions || o.IsSafeToRemap).ToList();
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
                        selectedReplacementEmote = null;
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

            if (DancyFileManager.DancyExists(Path.Combine(penRoot, selectedModDirectory)))
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
                    bool isDeleted = DancyFileManager.RemoveDancy(Path.Combine(penRoot, selectedModDirectory));
                    if (isDeleted)
                        Svc.Chat.Print("[Dancy] Removed existing Dancy overrides from the selected mod.");
                    else
                        Svc.Chat.Print("[Dancy] No existing Dancy overrides found in the selected mod.");
                    _ = reloadMod.Invoke(selectedModDirectory);
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

            //ImGui.Checkbox("Show unsafe / multi-source options", ref showUnsafeOptions);
            ImGui.Spacing();

            var filtered = remappableOptions
                .Where(o => showUnsafeOptions || o.IsSafeToRemap)
                .ToList();

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
                        selectedReplacementEmote = null;
                        currentStep = WizardStep.SelectTarget;
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

            ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Selected option");
            ImGui.Text($"Group:  {selected.GroupName}");
            ImGui.Text($"Option: {selected.OptionName}");

            ImGui.Spacing();
            ImGui.Text("Affected emotes / files:");

            var flagsDetails = ImGuiTableFlags.RowBg
                               | ImGuiTableFlags.BordersInnerV
                               | ImGuiTableFlags.SizingStretchSame;

            if (ImGui.BeginTable("DancySelectedOptionTable", 4, flagsDetails))
            {
                ImGui.TableSetupColumn("Emote");
                ImGui.TableSetupColumn("Command");
                ImGui.TableSetupColumn("Game path");
                ImGui.TableSetupColumn("Modded PAP");
                ImGui.TableHeadersRow();

                foreach (var e in selected.Entries)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(e.EmoteName);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(e.EmoteCommand);

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(e.GamePath);

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(e.ModdedPapPath);
                }

                ImGui.EndTable();
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

            var filtered = remappableOptions
                .Where(o => showUnsafeOptions || o.IsSafeToRemap)
                .ToList();

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

            ImGui.Text($"Source: ({opt.GroupName}) {opt.OptionName}");
            ImGui.Spacing();

            ImGui.PushItemWidth(320f);
            ImGui.InputText("Search target emote", ref emoteSearch, 100);
            ImGui.PopItemWidth();

            ImGui.Spacing();

            var results = EmoteLibrary.AllEmotes
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
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(emote.Command);
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();

            if (selectedReplacementEmote != null)
            {
                // Simple heuristic: does this look like a looped dance?
                bool looksLikeLoop = false;
                var key = selectedReplacementEmote.PrimaryTimelineKey ?? string.Empty;
                if (key.IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("dance", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    looksLikeLoop = true;
                }

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
                ImGui.Text($"Target: {selectedReplacementEmote.Name} ({selectedReplacementEmote.Command})");

                ImGui.Spacing();

                if (ImGui.Button("Create Dancy override"))
                {
                    _ = CreateOverrideAsync(opt, selectedReplacementEmote);
                }

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
                ImGui.TextDisabled("Creates a new Dancy group and PAP copy. Original files remain untouched.");
                ImGui.PopStyleColor();
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
        private async Task CreateOverrideAsync(RemappableOption source, LuminaEmote target)
        {
            if (selectedModDirectory == null)
                return;

            if (!modList.TryGetValue(selectedModDirectory, out var _))
                return;

            await Task.Run(() =>
            {
                try
                {
                    if (selectedModDirectory == null)
                        return;

                    var penumbraRoot = PenumbraDirectoryResolver.GetPenumbraDirectory();
                    var modFolder = Path.Combine(penumbraRoot, selectedModDirectory);

                    if (!Directory.Exists(modFolder))
                    {
                        Svc.Chat.PrintError($"[Dancy] Mod folder does not exist: {modFolder}");
                        return;
                    }

                    // 1) Ensure Dancy folders exist
                    string dancyRoot = Path.Combine(modFolder, "yucksdancy");
                    string papDir = Path.Combine(dancyRoot, "paps");

                    Directory.CreateDirectory(dancyRoot);
                    Directory.CreateDirectory(papDir);

                    // 2) Determine source PAP (assume safe option: one modded PAP source)
                    var firstEntry = source.Entries.FirstOrDefault();
                    if (firstEntry == null)
                    {
                        Svc.Chat.PrintError("[Dancy] No entries in selected option.");
                        return;
                    }

                    var relPap = firstEntry.ModdedPapPath.Replace('/', Path.DirectorySeparatorChar);
                    var srcPap = Path.Combine(modFolder, relPap);

                    if (!File.Exists(srcPap))
                    {
                        Svc.Chat.PrintError($"[Dancy] Source PAP not found: {srcPap}");
                        return;
                    }

                    string newPapName = Guid.NewGuid().ToString("N") + ".pap";
                    string newPapAbs = Path.Combine(papDir, newPapName);
                    string newPapRel = Path.Combine("yucksdancy", "paps", newPapName).Replace('\\', '/');

                    foreach (var e in source.Entries)
                        e.NewPapPath = newPapRel;

                    var targetGamePaths = PapResolver.ResolvePapFiles(target.PrimaryTimelineKey);
                    if (targetGamePaths.Count == 0)
                    {
                        Svc.Chat.PrintError("[Dancy] No game PAP paths found for target emote.");
                        return;
                    }

                    Svc.Framework.RunOnFrameworkThread(() =>
                    {
                        PapEditor.ApplyOverride(targetGamePaths[0], srcPap, newPapAbs);
                    });

                    Penumbra.PenumbraGroupWriter.CreateOrUpdateDancyGroupOld(
                        modFolder,
                        source,
                        target,
                        targetGamePaths,
                        newPapRel
                    );

                    try
                    {
                        reloadMod.Invoke(selectedModDirectory);
                    }
                    catch (Exception ex)
                    {
                        Svc.Chat.PrintError($"[Dancy] Warning: Failed to reload mod in Penumbra: {ex.Message}");
                    }

                    Svc.Chat.Print("[Dancy] Override created successfully.");
                }
                catch (Exception ex)
                {
                    Svc.Chat.PrintError($"[Dancy] Override creation failed: {ex.Message}");
                }
            });
        }
    }
}
