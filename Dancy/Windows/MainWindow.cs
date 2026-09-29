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
        private readonly Dictionary<string, IReadOnlyList<string>> targetPathsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TargetPapStatus> targetPapStatusById = new(StringComparer.OrdinalIgnoreCase);

        // State: target emote
        private string emoteSearch = string.Empty;
        private TargetSelectionCategory targetSelectionCategory = TargetSelectionCategory.LoopingEmotes;
        private LuminaEmote? selectedReplacementEmote = null;
        private OverridePlan? previewPlan;
        private PapCompatibilityResult? previewCompatibility;
        private bool isCreatingOverride;
        private string? lastDiagnostics;
        private readonly OverrideService overrideService = new();

        private sealed record TargetPapStatus(
            bool IsSupported,
            string Summary,
            string Detail,
            int SupportedVariantCount = 0,
            int TotalVariantCount = 0,
            IReadOnlyList<string>? SupportedGamePaths = null,
            bool UsesStandingIdleWriter = false);

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
            previewCompatibility = null;

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
                        previewCompatibility = null;
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
                ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Existing Dancy overrides");
                var existingOverrides = DancyFileManager.GetDancyOverrides(selectedModPath);
                foreach (var existing in existingOverrides)
                {
                    ImGui.PushID(existing.Id);
                    ImGui.TextWrapped($"{existing.Name} ({existing.Mappings.Count} target path{(existing.Mappings.Count == 1 ? string.Empty : "s")})");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Remove"))
                        RemoveDancyOverride(selectedModPath, existing.Id);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(existing.Description);
                    ImGui.PopID();
                }

                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.80f, 0.10f, 0.15f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.90f, 0.20f, 0.20f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.70f, 0.05f, 0.10f, 1.0f));

                if (ImGui.Button("Remove all Dancy overrides"))
                    RemoveAllDancyOverrides(selectedModPath);

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
                "Loop PAPs are the normal Dancy source. Start and transition PAPs stay visible for context, but are never silently selected.");

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

            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("DancyOverrideTable", 4, flags))
            {
                ImGui.TableSetupColumn("Group");
                ImGui.TableSetupColumn("Option");
                ImGui.TableSetupColumn("Animation");
                ImGui.TableSetupColumn("Loop paths");
                ImGui.TableHeadersRow();

                foreach (var opt in filtered)
                {
                    bool isSelected = ReferenceEquals(selectedOption, opt);

                    string animationsDisplay = string.Join(", ", opt.LogicalAnimations.Select(animation =>
                        string.IsNullOrEmpty(animation.Command) ? animation.Name : $"{animation.Name} ({animation.Command})"));

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    var label = $"{opt.GroupName}##{opt.GroupName}_{opt.OptionName}";

                    if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        selectedOption = opt;
                        ResetSelectedSourceGamePaths(opt);
                        selectedReplacementEmote = null;
                        previewPlan = null;
                        previewCompatibility = null;
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(opt.OptionName);

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(animationsDisplay);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(opt.LoopEntries.Count.ToString());
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
            ImGui.Text($"Group: {selected.GroupName}");
            ImGui.Text($"Option: {selected.OptionName}");
            var animationSummary = selected.LogicalAnimations
                .Select(animation => string.IsNullOrWhiteSpace(animation.Command) ? animation.Name : $"{animation.Name} ({animation.Command})")
                .ToList();
            ImGui.TextWrapped($"Animation: {string.Join(", ", animationSummary)}");
            ImGui.Text($"Paths: {selected.Entries.Count}");
            ImGui.Text($"Physical PAPs: {selected.Entries.Select(entry => entry.ModdedPapPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
            ImGui.Text($"Phases: {string.Join(" + ", selected.Entries.Select(entry => entry.AppliesTo.Phase).Distinct().OrderBy(SourceSelectionPolicy.PhaseOrder))}");

            ImGui.Spacing();
            var loops = SourceSelectionPolicy.OrderForDisplay(selected.LoopEntries);
            var starts = SourceSelectionPolicy.OrderForDisplay(selected.Entries.Where(entry => entry.AppliesTo.Phase == AnimationPhase.Start));
            var ends = SourceSelectionPolicy.OrderForDisplay(selected.Entries.Where(entry => entry.AppliesTo.Phase == AnimationPhase.End));
            var unknown = SourceSelectionPolicy.OrderForDisplay(selected.Entries.Where(entry => entry.AppliesTo.Phase == AnimationPhase.Unknown));
            var selectedLoopCount = GetSelectedSourceEntries(selected).Count;
            ImGui.Text($"{selectedLoopCount} / {loops.Count} Loop paths selected");
            if (starts.Count > 0)
                ImGui.TextDisabled($"{starts.Count} transition path{(starts.Count == 1 ? string.Empty : "s")} ignored");

            if (ImGui.Button("Select all loops"))
            {
                selectedSourceGamePaths.Clear();
                foreach (var entry in loops)
                    selectedSourceGamePaths.Add(entry.GamePath);
                previewPlan = null;
                previewCompatibility = null;
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear loop selection"))
            {
                foreach (var entry in loops)
                    selectedSourceGamePaths.Remove(entry.GamePath);
                previewPlan = null;
                previewCompatibility = null;
            }

            DrawSourcePathSection("Loop paths", loops, selectable: true, defaultOpen: true);
            DrawSourcePathSection("Start / transition paths", starts, selectable: false, defaultOpen: false);
            DrawSourcePathSection("End / other paths", ends, selectable: false, defaultOpen: false);
            DrawSourcePathSection("Unknown paths", unknown, selectable: false, defaultOpen: false);
            var selectedSourceEntries = GetSelectedSourceEntries(selected);
            ImGui.Spacing();

            if (selectedSourceEntries.Count == 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.45f, 1f));
                ImGui.Text("Select at least one source path to continue.");
                ImGui.PopStyleColor();
            }
            else if (ImGui.Button($"Continue with {selectedSourceEntries.Count} Loop path{(selectedSourceEntries.Count == 1 ? string.Empty : "s")}"))
            {
                selectedReplacementEmote = null;
                previewPlan = null;
                previewCompatibility = null;
                currentStep = WizardStep.SelectTarget;
            }

            EndCard();
        }

        private void DrawSourcePathSection(string label, IReadOnlyList<ParsedEmoteOverride> entries, bool selectable, bool defaultOpen)
        {
            if (entries.Count == 0)
                return;

            var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.TreeNodeEx($"{label} ({entries.Count})##{label}", flags))
                return;

            var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
            if (ImGui.BeginTable($"DancySourcePaths_{label}", 5, tableFlags))
            {
                ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, selectable ? 106f : 72f);
                ImGui.TableSetupColumn("Applies to");
                ImGui.TableSetupColumn("PAP origin");
                ImGui.TableSetupColumn("Phase", ImGuiTableColumnFlags.WidthFixed, 86f);
                ImGui.TableSetupColumn("Details");
                ImGui.TableHeadersRow();

                foreach (var (entry, index) in entries.Select((entry, index) => (entry, index)))
                {
                    ImGui.TableNextRow();
                    ImGui.PushID($"DancySourceEntry_{label}_{index}");
                    ImGui.TableNextColumn();
                    if (selectable)
                    {
                        var include = selectedSourceGamePaths.Contains(entry.GamePath);
                        if (ImGui.Checkbox("##UseSourcePath", ref include))
                        {
                            if (include)
                                selectedSourceGamePaths.Add(entry.GamePath);
                            else
                                selectedSourceGamePaths.Remove(entry.GamePath);
                            previewPlan = null;
                            previewCompatibility = null;
                        }
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Select only"))
                        {
                            selectedSourceGamePaths.Clear();
                            selectedSourceGamePaths.Add(entry.GamePath);
                            previewPlan = null;
                            previewCompatibility = null;
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("Ignored");
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.AppliesTo.Character.DisplayName);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.PapOrigin.IsKnown ? entry.PapOrigin.DisplayName : "Mod-relative PAP");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.AppliesTo.Phase.ToString());
                    ImGui.TableNextColumn();
                    var detail = $"{Path.GetFileName(entry.GamePath)} -> {Path.GetFileName(entry.ModdedPapPath)}";
                    if (ImGui.Selectable($"{detail}##CopyPaths", false))
                        ImGui.SetClipboardText($"Game path: {entry.GamePath}\nModded PAP path: {entry.ModdedPapPath}");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Game path:\n{entry.GamePath}\n\nModded PAP path:\n{entry.ModdedPapPath}\n\nClick to copy both paths.");
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            ImGui.TreePop();
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
            ImGui.Text($"Selected Loop paths: {selectedSourceEntries.Count} / {opt.LoopEntries.Count}");
            ImGui.Spacing();

            ImGui.PushItemWidth(320f);
            ImGui.InputText("Search targets", ref emoteSearch, 100);
            ImGui.PopItemWidth();

            if (ImGui.BeginTabBar("DancyTargetTypes"))
            {
                if (ImGui.BeginTabItem("Looped Emotes"))
                {
                    targetSelectionCategory = TargetSelectionCategory.LoopingEmotes;
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Poses & Idles"))
                {
                    targetSelectionCategory = TargetSelectionCategory.PosesAndIdles;
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("One-shot / Advanced"))
                {
                    targetSelectionCategory = TargetSelectionCategory.Advanced;
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }

            ImGui.Spacing();

            var isSearchingTargets = !string.IsNullOrWhiteSpace(emoteSearch);
            var results = EmoteLibrary.AllEmotes
                .Where(emote => isSearchingTargets
                    ? TargetEmotePolicy.IsSearchResult(emote.Behavior)
                    : TargetEmotePolicy.IsVisible(targetSelectionCategory, emote.Behavior))
                .Where(emote => TargetSemantics.MatchesSearch(
                    emoteSearch,
                    emote.Name,
                    emote.Command,
                    emote.Trigger,
                    TargetSemantics.DisplayName(emote.Behavior),
                    TargetSemantics.DisplayName(emote.Context)))
                .Take(50)
                .ToList();

            if (results.Count == 0)
            {
                ImGui.Text(isSearchingTargets ? "No matching targets." : "No matching targets in this category.");
                EndCard();
                return;
            }

            var flags = ImGuiTableFlags.RowBg
                        | ImGuiTableFlags.BordersInnerV
                        | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("DancyTargetTable", 4, flags))
            {
                ImGui.TableSetupColumn("Target");
                ImGui.TableSetupColumn("Trigger / context");
                ImGui.TableSetupColumn("Variants", ImGuiTableColumnFlags.WidthFixed, 72f);
                ImGui.TableSetupColumn("Compatibility", ImGuiTableColumnFlags.WidthFixed, 130f);
                ImGui.TableHeadersRow();

                foreach (var emote in results)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    bool isSelected = ReferenceEquals(selectedReplacementEmote, emote);
                    string label = $"{emote.Name}##{emote.TargetId}";
                    var targetStatus = GetTargetPapStatus(emote);

                    if (!targetStatus.IsSupported && isSelected)
                    {
                        selectedReplacementEmote = null;
                        previewPlan = null;
                        previewCompatibility = null;
                        isSelected = false;
                    }

                    if (!targetStatus.IsSupported)
                        ImGui.BeginDisabled();
                    if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        selectedReplacementEmote = emote;
                        previewPlan = null;
                        previewCompatibility = null;
                    }
                    if (!targetStatus.IsSupported)
                        ImGui.EndDisabled();

                    ImGui.TextDisabled($"{TargetSemantics.DisplayName(emote.Behavior)} · {TargetSemantics.DisplayName(emote.Context)}");

                    ImGui.TableNextColumn();
                    var trigger = string.IsNullOrWhiteSpace(emote.Trigger) ? emote.Command : emote.Trigger;
                    ImGui.TextUnformatted(string.IsNullOrWhiteSpace(trigger)
                        ? TargetSemantics.DisplayName(emote.Context)
                        : trigger);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(targetStatus.UsesStandingIdleWriter
                        ? $"{targetStatus.SupportedVariantCount} / {targetStatus.TotalVariantCount}"
                        : GetTargetPaths(emote).Count.ToString());
                    ImGui.TableNextColumn();
                    if (!targetStatus.IsSupported)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.62f, 0.45f, 1f));
                    ImGui.TextWrapped(targetStatus.Summary);
                    if (!targetStatus.IsSupported)
                    {
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(targetStatus.Detail);
                    }
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();

            if (selectedReplacementEmote != null)
            {
                previewPlan ??= CreatePreviewPlan(opt, selectedReplacementEmote, selectedSourceEntries);
                previewCompatibility ??= InspectCompatibility(previewPlan);

                var behaviorNotice = TargetSemantics.BehaviorNotice(selectedReplacementEmote.Behavior);
                if (!string.IsNullOrWhiteSpace(behaviorNotice))
                {
                    var warning = selectedReplacementEmote.Behavior is TargetBehavior.OneShot or TargetBehavior.Unknown;
                    ImGui.PushStyleColor(ImGuiCol.Text, warning
                        ? new Vector4(1f, 0.78f, 0.45f, 1f)
                        : new Vector4(0.7f, 0.86f, 0.8f, 1f));
                    ImGui.TextWrapped(behaviorNotice);
                    ImGui.PopStyleColor();
                    ImGui.Spacing();
                }

                ImGui.Spacing();

                ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Summary");
                ImGui.Text($"Source: ({opt.GroupName}) {opt.OptionName}");
                ImGui.Text($"Source Loop paths: {selectedSourceEntries.Count} selected");
                ImGui.Text($"Target: {selectedReplacementEmote.Name}");
                ImGui.Text($"Type: {TargetSemantics.DisplayName(selectedReplacementEmote.Behavior)}");
                ImGui.Text($"Context: {TargetSemantics.DisplayName(selectedReplacementEmote.Context)}");
                var selectedTrigger = string.IsNullOrWhiteSpace(selectedReplacementEmote.Trigger)
                    ? selectedReplacementEmote.Command
                    : selectedReplacementEmote.Trigger;
                ImGui.TextWrapped($"Trigger: {(string.IsNullOrWhiteSpace(selectedTrigger) ? "State-managed" : selectedTrigger)}");
                var targetStatus = GetTargetPapStatus(selectedReplacementEmote);
                var targetPaths = GetTargetPaths(selectedReplacementEmote);
                ImGui.Text(targetStatus.UsesStandingIdleWriter
                    ? $"Variants: {targetStatus.SupportedVariantCount} supported / {targetStatus.TotalVariantCount} current"
                    : $"Variants: {targetPaths.Count}");
                var compatibilityColor = previewCompatibility.Status == PapCompatibilityStatus.Unsupported
                    ? new Vector4(1f, 0.5f, 0.45f, 1f)
                    : previewCompatibility.Status == PapCompatibilityStatus.CompatibleWithWarning
                        ? new Vector4(1f, 0.85f, 0.5f, 1f)
                        : new Vector4(0.65f, 0.9f, 0.7f, 1f);
                ImGui.TextColored(compatibilityColor, $"Compatibility: {previewCompatibility.Status}");
                ImGui.TextWrapped(previewCompatibility.Reason);

                if (ImGui.TreeNode("Details"))
                {
                    ImGui.TextWrapped($"Timeline: {selectedReplacementEmote.PrimaryTimelineKey}");
                    if (!string.IsNullOrWhiteSpace(selectedReplacementEmote.ClassificationEvidence))
                        ImGui.TextWrapped($"Classification evidence: {selectedReplacementEmote.ClassificationEvidence}");
                    if (targetStatus.UsesStandingIdleWriter)
                    {
                        ImGui.TextUnformatted("Structure: Multi-section");
                        ImGui.TextUnformatted("Dancy strategy: Replace primary idle motion");
                        ImGui.TextUnformatted("Preserve: Target-native auxiliary motion and timelines");
                    }
                    foreach (var path in targetPaths)
                    {
                        var character = GamePathIdentity.Parse(path).Character;
                        ImGui.BulletText($"{(character.IsKnown ? character.DisplayName : "Unclassified variant")}: {path}");
                    }
                    ImGui.TreePop();
                }

                DrawMappingPreview(previewPlan, selectedReplacementEmote, previewCompatibility);

                ImGui.Spacing();

                if (selectedSourceEntries.Count == 0)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.45f, 1f));
                    ImGui.Text("Select at least one source path in Step 2.");
                    ImGui.PopStyleColor();
                }
                else if (!previewPlan.IsValid || !previewCompatibility.CanCreate)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.45f, 1f));
                    ImGui.TextWrapped(!previewPlan.IsValid ? string.Join("\n", previewPlan.Errors) : previewCompatibility.Reason);
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
            foreach (var entry in source.LoopEntries)
                selectedSourceGamePaths.Add(entry.GamePath);
        }

        private void EnsureSelectedSourceGamePaths(RemappableOption source)
        {
            var validPaths = SourceSelectionPolicy.DefaultLoopGamePaths(source.Entries);

            selectedSourceGamePaths.RemoveWhere(path => !validPaths.Contains(path));

            if (selectedSourceGamePaths.Count == 0 && source.LoopEntries.Count == 1)
                selectedSourceGamePaths.Add(source.LoopEntries[0].GamePath);
        }

        private List<ParsedEmoteOverride> GetSelectedSourceEntries(RemappableOption source)
            => SourceSelectionPolicy.SelectedLoopEntries(source.Entries, selectedSourceGamePaths).ToList();

        private OverridePlan CreatePreviewPlan(
            RemappableOption source,
            LuminaEmote target,
            IReadOnlyList<ParsedEmoteOverride> sourceEntries)
        {
            var targetPaths = GetCompatibleTargetPaths(target);
            var sourceAnimation = source.LogicalAnimations.FirstOrDefault(animation => animation.Paths.Any(sourceEntries.Contains))
                ?? source.LogicalAnimations.FirstOrDefault();
            return OverridePlanner.Create(new OverridePlanRequest
            {
                ModIdentity = selectedModDirectory ?? string.Empty,
                SourceGroupName = source.GroupName,
                SourceOptionName = source.OptionName,
                SourceAnimationName = sourceAnimation?.Name ?? string.Empty,
                SourceAnimationCommand = sourceAnimation?.Command ?? string.Empty,
                TargetTimelineKey = target.PrimaryTimelineKey,
                TargetName = target.Name,
                TargetCommand = target.Command,
                Sources = sourceEntries
                    .Select(entry => new OverridePlanSource(entry.GamePath, entry.ModdedPapPath))
                    .ToList(),
                TargetGamePaths = targetPaths,
            });
        }

        private IReadOnlyList<string> GetTargetPaths(LuminaEmote target)
        {
            var targetId = string.IsNullOrWhiteSpace(target.TargetId) ? target.PrimaryTimelineKey : target.TargetId;
            if (!targetPathsById.TryGetValue(targetId, out var paths))
            {
                paths = PapResolver.ResolvePapFiles(target.PrimaryTimelineKey)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                targetPathsById[targetId] = paths;
            }

            return paths;
        }

        private IReadOnlyList<string> GetCompatibleTargetPaths(LuminaEmote target)
        {
            var status = GetTargetPapStatus(target);
            return status.UsesStandingIdleWriter && status.SupportedGamePaths is { Count: > 0 }
                ? status.SupportedGamePaths
                : GetTargetPaths(target);
        }

        private TargetPapStatus GetTargetPapStatus(LuminaEmote target)
        {
            var targetId = string.IsNullOrWhiteSpace(target.TargetId) ? target.PrimaryTimelineKey : target.TargetId;
            if (targetPapStatusById.TryGetValue(targetId, out var status))
                return status;

            var paths = GetTargetPaths(target);
            if (paths.Count == 0)
                return targetPapStatusById[targetId] = new TargetPapStatus(
                    false,
                    "Unavailable: no PAPs",
                    $"Dancy recognizes {target.Name}, but could not resolve current target PAP variants. Refresh Data or choose another target.");

            try
            {
                var inspections = paths
                    .Select(path => new PapTargetInspection(path, PapEditor.InspectTargetPap(path)))
                    .ToList();
                if (IsCanonicalStandingIdleTarget(target))
                {
                    var variants = StandingIdleVariantCatalog.Analyze(inspections);
                    var detail = variants.AllVariantsSupported
                        ? "This target uses Dancy's validated multi-section Standing Idle strategy: replace the primary idle motion while preserving target-native auxiliary motion and timelines."
                        : $"Only {variants.SupportedVariantCount} of {variants.TotalVariantCount} current variants match Dancy's exact Standing Idle topology. Unsupported variants remain excluded.";
                    return targetPapStatusById[targetId] = new TargetPapStatus(
                        variants.HasSupportedVariants,
                        variants.HasSupportedVariants ? "Compatible" : "Unsupported: topology mismatch",
                        detail,
                        variants.SupportedVariantCount,
                        variants.TotalVariantCount,
                        variants.SupportedGamePaths,
                        UsesStandingIdleWriter: true);
                }

                if (inspections.All(inspection => inspection.Inspection.AnimationCount == 1 && inspection.Inspection.TimelineSectionSizes.Count == 1))
                {
                    return targetPapStatusById[targetId] = new TargetPapStatus(
                        true,
                        "PAP ready",
                        "This target's current PAP variants satisfy Dancy's one-animation, one-TMB-section preflight.");
                }

                var complex = inspections.Select(inspection => inspection.Inspection)
                    .FirstOrDefault(inspection => inspection.AnimationCount != 1 || inspection.TimelineSectionSizes.Count != 1);
                return targetPapStatusById[targetId] = new TargetPapStatus(
                    false,
                    $"Unsupported: {complex?.AnimationCount ?? 0} animation / {complex?.TimelineSectionSizes.Count ?? 0} TMB",
                    $"Dancy recognizes {target.Name} as {TargetSemantics.DisplayName(target.Behavior)} / {TargetSemantics.DisplayName(target.Context)}, but its PAP does not match either Dancy's one-section structure or the fixed Standing Idle motion-0 topology. Dancy will not rewrite an arbitrary multi-section target.");
            }
            catch (Exception exception)
            {
                return targetPapStatusById[targetId] = new TargetPapStatus(
                    false,
                    $"Unavailable: {exception.GetType().Name}",
                    $"Dancy could not inspect {target.Name}'s PAP structure. Refresh Data or choose another target.");
            }
        }

        private PapCompatibilityResult InspectCompatibility(OverridePlan plan)
        {
            if (!plan.IsValid)
                return new PapCompatibilityResult(PapCompatibilityStatus.Unknown, "Select valid Loop source paths before compatibility can be inspected.");

            try
            {
                var root = PenumbraDirectoryResolver.GetPenumbraDirectory();
                if (string.IsNullOrWhiteSpace(root) || selectedModDirectory is null
                    || !PathSafety.TryResolveInsideRoot(root, selectedModDirectory, out var modFolder))
                {
                    return new PapCompatibilityResult(PapCompatibilityStatus.Unknown, "Dancy could not resolve the selected mod folder for PAP inspection.");
                }

                var results = plan.PapCopies.Select(copy =>
                {
                    if (!PathSafety.TryResolveInsideRoot(modFolder, copy.SourcePapPath, out var sourcePath))
                        throw new InvalidOperationException($"Dancy refused an unsafe source PAP path: {copy.SourcePapPath}");
                    var source = PapFileInspector.InspectFile(sourcePath);
                    var targets = copy.TargetGamePaths
                        .Select(path => new PapTargetInspection(path, PapEditor.InspectTargetPap(path)))
                        .ToList();
                    return PapCompatibilityPreflight.Evaluate(source, targets);
                });
                return PapCompatibilityPreflight.Combine(results);
            }
            catch (Exception exception)
            {
                return new PapCompatibilityResult(PapCompatibilityStatus.Unknown, $"Dancy could not inspect compatibility yet: {exception.Message}");
            }
        }

        private static void DrawMappingPreview(OverridePlan plan, LuminaEmote target, PapCompatibilityResult compatibility)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Mapping preview");
            if (!plan.IsValid)
            {
                ImGui.TextDisabled("No metadata will be changed until this plan is valid.");
                return;
            }

            if (IsCanonicalStandingIdleTarget(target) && compatibility.WriteStrategy == PapOverrideWriteStrategy.StandingIdleMotion0)
            {
                ImGui.TextUnformatted($"Target: {target.Name}");
                ImGui.TextUnformatted("Strategy: Target-native multi-section shell");
            }

            foreach (var copy in plan.PapCopies)
            {
                var strategies = string.Join(", ", copy.MatchResults
                    .Select(result => result.Strategy.ToString())
                    .Distinct(StringComparer.Ordinal));
                var appliesTo = copy.TargetGamePaths
                    .Select(path => GamePathIdentity.Parse(path).Character)
                    .Where(character => character.IsKnown)
                    .Select(character => character.DisplayName)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                ImGui.TextWrapped($"Source: {copy.SourcePapPath}");
                ImGui.TextWrapped($"Applies to: {string.Join(", ", appliesTo)} [{strategies}]");
            }

            foreach (var warning in plan.Warnings)
                ImGui.TextWrapped($"Warning: {warning}");
        }

        private static bool IsCanonicalStandingIdleTarget(LuminaEmote target)
            => string.Equals(target.PrimaryTimelineKey, "normal/idle", StringComparison.OrdinalIgnoreCase);

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
                    lastDiagnostics = BuildDiagnostics(source, target, plan, operation.Execution, operation.Write, operation.Reload);
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
            PenumbraReloadResult reload)
        {
            var diagnostics = new StringBuilder();
            diagnostics.AppendLine("Dancy diagnostics");
            diagnostics.AppendLine($"Build: {typeof(Plugin).Assembly.GetName().Version}");
            diagnostics.AppendLine($"Build MVID: {typeof(Plugin).Module.ModuleVersionId}");
            diagnostics.AppendLine($"Override: {plan.OverrideId}");
            diagnostics.AppendLine($"Source: ({source.GroupName}) {source.OptionName}");
            diagnostics.AppendLine($"Target: {target.Name} ({target.Command}) [{target.PrimaryTimelineKey}]");
            diagnostics.AppendLine($"Metadata: {write.Format} ({Path.GetFileName(write.MetadataPath)})");
            diagnostics.AppendLine($"Penumbra reload: {reload.UserFacingOutcome}");

            foreach (var copy in plan.PapCopies)
            {
                diagnostics.AppendLine($"Source PAP: {copy.SourcePapPath}");
                diagnostics.AppendLine($"  paths: {string.Join(", ", copy.SourceGamePaths)}");
                diagnostics.AppendLine($"  strategy: {string.Join(", ", copy.MatchResults.Select(result => result.Strategy).Distinct())}");
            }

            foreach (var patch in execution.PapResults)
            {
                diagnostics.AppendLine($"Patched: {patch.TargetGamePath} -> {patch.EventIdentifier} ({patch.PatchedTimelineEntries} timeline entries; {patch.WriteStrategy})");
                if (patch.WriteStrategy == PapOverrideWriteStrategy.StandingIdleMotion0)
                {
                    diagnostics.AppendLine($"  source motion 0 fingerprint: {patch.SourceMotionFingerprint}");
                    diagnostics.AppendLine($"  preserved target motion 1 fingerprint: {patch.PreservedTargetMotionFingerprint}");
                    diagnostics.AppendLine($"  preserved target TMB hashes: {string.Join(", ", patch.PreservedTargetTimelineHashes)}");
                }
            }
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

        private void RemoveDancyOverride(string modFolder, string overrideId)
        {
            try
            {
                var removal = DancyFileManager.RemoveDancyOverride(modFolder, overrideId);
                var reload = TryReloadMod(selectedModDirectory ?? string.Empty);
                var runtime = QueryChangedItemsRuntimeState();
                lastDiagnostics = $"Dancy removal\nOverride: {overrideId}\nRemoved options: {removal.RemovedOverrideCount}\nRemoved generated files: {removal.GarbageCollection.RemovedFiles.Count}\n{removal.Verification}\nPenumbra runtime reload: {reload.UserFacingOutcome}\n{runtime}";
                Svc.Chat.Print(removal.RequestedOverrideCleanupSucceeded
                    ? "[Dancy] Removed the selected override. Disk verification is clean."
                    : "[Dancy] Removed the selected override, but Dancy disk verification found remaining owned content.");
                if (!reload.Succeeded)
                    Svc.Chat.Print("[Dancy] The override was removed and disk cleanup completed, but Penumbra refresh did not complete after one retry. Reload the mod or use Refresh Data if its Changed Items UI looks stale.");
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Failed to remove the selected override.");
                Svc.Chat.PrintError($"[Dancy] Failed to remove the selected override: {exception.Message}");
            }
        }

        private void RemoveAllDancyOverrides(string modFolder)
        {
            try
            {
                var removal = DancyFileManager.RemoveAllDancyOverrides(modFolder);
                var reload = TryReloadMod(selectedModDirectory ?? string.Empty);
                var runtime = QueryChangedItemsRuntimeState();
                lastDiagnostics = $"Dancy remove all\nRemoved options: {removal.RemovedOverrideCount}\nRemoved generated files: {removal.GarbageCollection.RemovedFiles.Count}\n{removal.Verification}\nPenumbra runtime reload: {reload.UserFacingOutcome}\n{runtime}";
                Svc.Chat.Print(removal.DiskClean
                    ? "[Dancy] Removed all Dancy overrides. Disk verification is clean."
                    : "[Dancy] Dancy removal completed, but owned metadata or generated files remain on disk.");
                if (!reload.Succeeded)
                    Svc.Chat.Print("[Dancy] Metadata is clean on disk, but Penumbra reload did not complete. Its Changed Items UI may remain stale until it reloads the mod.");
            }
            catch (Exception exception)
            {
                Svc.Log.Error(exception, "[Dancy] Failed to remove Dancy overrides.");
                Svc.Chat.PrintError($"[Dancy] Failed to remove Dancy overrides: {exception.Message}");
            }
        }

        private PenumbraReloadResult TryReloadMod(string modDirectory)
        {
            try
            {
                var modName = modList.TryGetValue(modDirectory, out var name)
                    ? name
                    : selectedModName ?? string.Empty;

                return PenumbraReloadCapture.ExecuteWithSingleRetry(() => reloadMod.Invoke(modDirectory, modName).ToString());
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, $"[Dancy] Failed to reload Penumbra mod {modDirectory}.");
                return new PenumbraReloadResult
                {
                    Succeeded = false,
                    Actual = $"{ex.GetType().Name}: {ex.Message}",
                };
            }
        }

        private string QueryChangedItemsRuntimeState()
        {
            try
            {
                if (selectedModDirectory is null)
                    return "PENUMBRA RUNTIME: no selected mod for GetChangedItems verification.";
                var modName = modList.TryGetValue(selectedModDirectory, out var name) ? name : selectedModName ?? string.Empty;
                var items = new GetChangedItems(Plugin.PluginInterface).Invoke(selectedModDirectory, modName);
                return $"PENUMBRA RUNTIME: GetChangedItems returned {items.Count} item(s). The supported API cannot attribute one changed item to an individual Dancy option.";
            }
            catch (Exception exception)
            {
                return $"PENUMBRA RUNTIME: GetChangedItems was unavailable ({exception.GetType().Name}: {exception.Message}).";
            }
        }
    }
}
