using System;
using System.Collections.Generic;
using System.Diagnostics;
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
#if DEBUG
using Dancy.Diagnostics;
#endif

namespace Dancy.Windows
{
    public class MainWindow : Window, IDisposable
    {
        private static readonly Vector4 ErrorTextColor = new(1f, 0.35f, 0.35f, 1f);
        private static readonly Vector4 CautionTextColor = new(1f, 0.82f, 0.45f, 1f);

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
        private string? selectedModFolder;
        private bool isLoadingMods;
        private bool isScanningMod;
        private string? lastScanError;
        string penRoot = String.Empty;

        // State: emote scanning
        private List<RemappableOption> remappableOptions = new();
        private RemappableOption? selectedOption = null;
        private readonly HashSet<string> selectedSourceGamePaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly AdditionalCompatibleMappingSet additionalCompatibleMappings = new();
        private string? selectedCompatiblePhysicalSourceKey;
        private string? additionalMappingError;
        private TargetCatalogPresentationCache<LuminaEmote>? targetCatalog;
#if DEBUG
        private long step3TransitionStartedAt;
#endif

        // State: target emote
        private string emoteSearch = string.Empty;
        private TargetSelectionCategory targetSelectionCategory = TargetSelectionCategory.LoopingEmotes;
        private LuminaEmote? selectedReplacementEmote = null;
        private OverridePlan? previewPlan;
        private PapCompatibilityResult? previewCompatibility;
        private readonly Dictionary<string, TargetSourceSelectionPreview> previewSourceSelections = new(StringComparer.OrdinalIgnoreCase);
        private bool isCreatingOverride;
        private string? lastDiagnostics;
        private readonly OverrideService overrideService = new();

        private sealed record ExistingOverrideSummary(
            string Source,
            string Option,
            string Target,
            string Type,
            string Context,
            string SourceMapping,
            string SourceMappingDetails,
            bool IsLegacy);

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
            selectedModFolder = null;
            remappableOptions.Clear();
            selectedOption = null;
            selectedSourceGamePaths.Clear();
            additionalCompatibleMappings.Clear();
            selectedCompatiblePhysicalSourceKey = null;
            additionalMappingError = null;
            selectedReplacementEmote = null;
            InvalidatePreviewCompatibility();

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
                    selectedModFolder = modFolder;

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
            InvalidateTargetCatalog();
            _ = LoadModListAsync();
        }

        // ======================================
        // Draw root
        // ======================================
        public override void Draw()
        {
            var totalHeight = ImGui.GetContentRegionAvail().Y;

            // Fixed utility bands avoid wasting vertical space when the wizard is enlarged.
            const float headerHeight = 98f;
            const float footerHeight = 84f;
            var contentHeight = Math.Max(0f, totalHeight - headerHeight - footerHeight);

            // =========================
            // HEADER
            // =========================
            using (ImRaii.Child("DancyHeader", new Vector2(-1, headerHeight), false))
            {
                DrawHeader();
                ImGui.Spacing();
                DrawStepNavigation();
            }

            // =========================
            // MAIN CONTENT
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
            // FOOTER
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
            ImGui.TextWrapped("Early Access: report an issue or share feedback through the Dancy Discord.");
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

            if (ImGui.Button("Discord"))
                Util.OpenLink("https://discord.gg/asDM4dh4gz");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open Dancy support and feedback Discord");

            ImGui.Spacing();

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
            ImGui.TextWrapped("Dancy creates a Dancy-owned override option. Original mod files stay untouched.");
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
            DrawStepButton("2. Source", WizardStep.SelectSource, hasSource, hasSelectedSource);
            ImGui.SameLine();
            DrawStepButton("3. Target", WizardStep.SelectTarget, hasSelectedSource, hasTarget);

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
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
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
                        selectedModFolder = null;
                        remappableOptions.Clear();
                        selectedOption = null;
                        selectedSourceGamePaths.Clear();
                        additionalCompatibleMappings.Clear();
                        selectedCompatiblePhysicalSourceKey = null;
                        additionalMappingError = null;
                        selectedReplacementEmote = null;
                        InvalidatePreviewCompatibility();
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

            ImGui.Text($"Selected: {selectedModName}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(selectedModDirectory);

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
                DrawExistingDancyOverrides(selectedModPath);

            EndCard();
        }

        private void DrawExistingDancyOverrides(string modPath)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Existing Dancy overrides");
            ImGui.TextDisabled("Recreate the same source and target to update an existing Dancy override.");

            var existingOverrides = DancyFileManager.GetDancyOverrides(modPath);
            if (existingOverrides.Count == 0)
            {
                ImGui.TextDisabled("Dancy-owned metadata or files were found, but no current override summary is available.");
            }
            else
            {
                var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
                if (ImGui.BeginTable("DancyExistingOverrideTable", 4, flags))
                {
                    ImGui.TableSetupColumn("Source");
                    ImGui.TableSetupColumn("Target");
                    ImGui.TableSetupColumn("Affected", ImGuiTableColumnFlags.WidthFixed, 74f);
                    ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 66f);
                    ImGui.TableHeadersRow();

                    foreach (var existing in existingOverrides)
                    {
                        var summary = DescribeExistingOverride(existing);
                        ImGui.PushID(existing.Id);
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(summary.Source);
                        if (!string.IsNullOrWhiteSpace(summary.Option))
                            ImGui.TextDisabled(summary.Option);
                        ImGui.TextDisabled(summary.SourceMapping);
                        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(summary.SourceMappingDetails))
                            ImGui.SetTooltip(summary.SourceMappingDetails);

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(summary.Target);
                        ImGui.TextDisabled(summary.IsLegacy
                            ? "Legacy Dancy override"
                            : $"{summary.Type} · {summary.Context}");

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{existing.Mappings.Count} path{(existing.Mappings.Count == 1 ? string.Empty : "s")}");

                        ImGui.TableNextColumn();
                        using (ImRaii.PushFont(UiBuilder.IconFont))
                        {
                            if (ImGui.SmallButton(FontAwesomeIcon.Copy.ToIconString()))
                                CopyExistingOverrideDetails(existing, summary);
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Copy Dancy override details");

                            ImGui.SameLine();
                            if (ImGui.SmallButton(FontAwesomeIcon.Trash.ToIconString()))
                                RemoveDancyOverride(modPath, existing.Id);
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Remove this Dancy override");
                        }

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.80f, 0.10f, 0.15f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.90f, 0.20f, 0.20f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.70f, 0.05f, 0.10f, 1.0f));
            if (ImGui.Button("Remove all Dancy overrides"))
                RemoveAllDancyOverrides(modPath);
            ImGui.PopStyleColor(3);
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
                ImGui.Text("No safe source options found.");
                EndCard();
                return;
            }

            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("DancyOverrideTable", 3, flags))
            {
                ImGui.TableSetupColumn("Source option");
                ImGui.TableSetupColumn("Animation");
                ImGui.TableSetupColumn("Loops", ImGuiTableColumnFlags.WidthFixed, 52f);
                ImGui.TableHeadersRow();

                foreach (var opt in filtered)
                {
                    bool isSelected = ReferenceEquals(selectedOption, opt);

                    string animationsDisplay = string.Join(", ", opt.LogicalAnimations.Select(animation =>
                        string.IsNullOrEmpty(animation.Command) ? animation.Name : $"{animation.Name} ({animation.Command})"));

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    var label = $"{opt.OptionName}##{opt.GroupName}_{opt.OptionName}";

                    if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        selectedOption = opt;
                        ResetSelectedSourceGamePaths(opt);
                        selectedReplacementEmote = null;
                        InvalidatePreviewCompatibility();
                    }

                    ImGui.TextDisabled(opt.GroupName);

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

            ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Selected source");
            ImGui.TextWrapped($"{selected.OptionName} · {selected.GroupName}");
            ImGui.TextWrapped($"Animation: {DescribeSourceAnimation(selected)}");

            ImGui.Spacing();
            var loops = SourceSelectionPolicy.OrderForDisplay(selected.LoopEntries);
            var starts = SourceSelectionPolicy.OrderForDisplay(selected.Entries.Where(entry => entry.AppliesTo.Phase == AnimationPhase.Start));
            var ends = SourceSelectionPolicy.OrderForDisplay(selected.Entries.Where(entry => entry.AppliesTo.Phase == AnimationPhase.End));
            var unknown = SourceSelectionPolicy.OrderForDisplay(selected.Entries.Where(entry => entry.AppliesTo.Phase == AnimationPhase.Unknown));
            var selectedProvidedEntries = GetSelectedModProvidedSourceEntries(selected);
            var selectedLoopCount = selectedProvidedEntries.Count;
            ImGui.Text($"{selectedLoopCount} / {loops.Count} source-provided Loop paths selected");
            if (additionalCompatibleMappings.Values.Count > 0)
                ImGui.TextDisabled($"{additionalCompatibleMappings.Values.Count} user-added compatible mapping{(additionalCompatibleMappings.Values.Count == 1 ? string.Empty : "s")} selected");
            if (starts.Count > 0)
                ImGui.TextDisabled($"{starts.Count} start / transition path{(starts.Count == 1 ? string.Empty : "s")} shown for context");
            if (ends.Count > 0 || unknown.Count > 0)
                ImGui.TextDisabled($"{ends.Count + unknown.Count} end or unclassified path{(ends.Count + unknown.Count == 1 ? string.Empty : "s")} shown for context");

            if (loops.Count == 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.68f, 0.38f, 1f));
                ImGui.TextWrapped("This option has no Loop sources. Its remaining files are available for context, but cannot create a normal Dancy loop override.");
                ImGui.PopStyleColor();
            }
            else
            {
                if (ImGui.Button("Select all loops"))
                {
                    selectedSourceGamePaths.Clear();
                    foreach (var entry in loops)
                        selectedSourceGamePaths.Add(entry.GamePath);
                    InvalidatePreviewCompatibility();
                }
                ImGui.SameLine();
                if (ImGui.Button("Clear loop selection"))
                {
                    foreach (var entry in loops)
                        selectedSourceGamePaths.Remove(entry.GamePath);
                    InvalidatePreviewCompatibility();
                }
            }

            DrawSourcePathSection("Source-provided Loop sources", loops, selectable: true, defaultOpen: true);
            DrawAdditionalCompatibleMappings(selected);
            DrawSourcePathSection("Start / transition", starts, selectable: false, defaultOpen: false);
            DrawSourcePathSection("End / other", ends, selectable: false, defaultOpen: false);
            DrawSourcePathSection("Unknown", unknown, selectable: false, defaultOpen: false);
            var selectedSourceEntries = GetSelectedSourceEntries(selected);
            ImGui.Spacing();

            if (loops.Count == 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ErrorTextColor);
                ImGui.Text("Choose a source option with Loop sources to continue.");
                ImGui.PopStyleColor();
            }
            else if (selectedSourceEntries.Count == 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ErrorTextColor);
                ImGui.Text("Select at least one Loop source to continue.");
                ImGui.PopStyleColor();
            }
            else if (ImGui.Button($"Continue with {selectedSourceEntries.Count} Loop path{(selectedSourceEntries.Count == 1 ? string.Empty : "s")}"))
            {
#if DEBUG
                var continueStartedAt = Stopwatch.GetTimestamp();
#endif
                selectedReplacementEmote = null;
                InvalidatePreviewCompatibility();
                currentStep = WizardStep.SelectTarget;
#if DEBUG
                step3TransitionStartedAt = continueStartedAt;
                DancyStep3PerformanceTelemetry.RecordElapsed("Step2Continue", continueStartedAt);
#endif
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
                ImGui.TableSetupColumn("Logical race");
                ImGui.TableSetupColumn("Physical source");
                ImGui.TableSetupColumn("Mapping", ImGuiTableColumnFlags.WidthFixed, 148f);
                ImGui.TableSetupColumn("Copy", ImGuiTableColumnFlags.WidthFixed, 48f);
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
                            InvalidatePreviewCompatibility();
                        }
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Select only"))
                        {
                            selectedSourceGamePaths.Clear();
                            selectedSourceGamePaths.Add(entry.GamePath);
                            InvalidatePreviewCompatibility();
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("Context only");
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.AppliesTo.Character.DisplayName);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Path.GetFileName(entry.ModdedPapPath));
                    ImGui.TextDisabled(entry.PhysicalSourceOrigin.IsKnown ? entry.PhysicalSourceOrigin.DisplayName : "Mod-relative file");
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(entry.MappingOrigin.DisplayName());
                    ImGui.TableNextColumn();
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        if (ImGui.SmallButton(FontAwesomeIcon.Copy.ToIconString()))
                            ImGui.SetClipboardText($"Game path: {entry.GamePath}\nModded PAP path: {entry.ModdedPapPath}");
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            $"Phase: {entry.AppliesTo.Phase}\nMapping: {entry.MappingOrigin.DisplayName()}\n\nLogical game path:\n{entry.GamePath}\n\nPhysical source PAP:\n{entry.ModdedPapPath}\n\nCopy both paths.");
                    }
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            ImGui.TreePop();
        }

        private void DrawAdditionalCompatibleMappings(RemappableOption source)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Additional compatible mappings");
            ImGui.TextDisabled("Explicit user-selected logical paths. Dancy does not infer race compatibility or retarget animation content.");

            var physicalCandidates = AdditionalCompatiblePhysicalSources.Discover(source.LoopEntries);
            var hasPhysicalSource = AdditionalCompatiblePhysicalSources.TryResolve(
                physicalCandidates,
                selectedCompatiblePhysicalSourceKey,
                out var physicalCandidate,
                out var physicalSourceError);
            if (physicalCandidates.Count == 1)
            {
                selectedCompatiblePhysicalSourceKey = physicalCandidate!.Key;
                ImGui.TextDisabled($"Physical source inferred: {physicalCandidate.DisplayName}");
            }
            else if (physicalCandidates.Count > 1)
            {
                ImGui.PushItemWidth(330f);
                var physicalLabel = physicalCandidate?.DisplayName ?? "Choose physical source";
                if (ImGui.BeginCombo("Use physical source", physicalLabel))
                {
                    foreach (var candidate in physicalCandidates)
                    {
                        var isSelected = string.Equals(candidate.Key, selectedCompatiblePhysicalSourceKey, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(candidate.DisplayName, isSelected))
                        {
                            selectedCompatiblePhysicalSourceKey = candidate.Key;
                            additionalMappingError = null;
                            hasPhysicalSource = AdditionalCompatiblePhysicalSources.TryResolve(
                                physicalCandidates,
                                selectedCompatiblePhysicalSourceKey,
                                out physicalCandidate,
                                out physicalSourceError);
                        }
                        if (isSelected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                ImGui.PopItemWidth();
                if (!hasPhysicalSource)
                    ImGui.TextDisabled(physicalSourceError);
            }
            else
            {
                selectedCompatiblePhysicalSourceKey = null;
                ImGui.TextDisabled(physicalSourceError);
            }

            var representedCodes = source.Entries
                    .Concat(additionalCompatibleMappings.Values)
                    .Select(entry => entry.AppliesTo.Character.Code)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = CharacterPathIdentity.PlayableIdentities
                .Where(identity => !representedCodes.Contains(identity.Code))
                .ToList();
            ImGui.BeginDisabled(!hasPhysicalSource);
            if (ImGui.BeginCombo("+ Add race", "Add race"))
            {
                foreach (var candidate in candidates)
                {
                    if (!ImGui.Selectable(candidate.DisplayName))
                        continue;

                    if (additionalCompatibleMappings.TryAdd(physicalCandidate!.Source, candidate, source.Entries, out _, out var error))
                    {
                        additionalMappingError = null;
                        InvalidatePreviewCompatibility();
                    }
                    else
                    {
                        additionalMappingError = error;
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.EndDisabled();

            if (candidates.Count == 0)
                ImGui.TextDisabled("All playable logical race identities are already represented by this source option.");

            if (!string.IsNullOrWhiteSpace(additionalMappingError))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ErrorTextColor);
                ImGui.TextWrapped(additionalMappingError);
                ImGui.PopStyleColor();
            }

            var mappings = additionalCompatibleMappings.Values;
            if (mappings.Count == 0)
                return;

            var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
            if (ImGui.BeginTable("DancyAdditionalCompatibleMappings", 4, tableFlags))
            {
                ImGui.TableSetupColumn("Logical race");
                ImGui.TableSetupColumn("Mapping", ImGuiTableColumnFlags.WidthFixed, 180f);
                ImGui.TableSetupColumn("Uses source");
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 66f);
                ImGui.TableHeadersRow();

                foreach (var mapping in mappings)
                {
                    ImGui.PushID(mapping.GamePath);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(mapping.AppliesTo.Character.DisplayName);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted("User-added mapping");
                    ImGui.TextDisabled("Target preflight pending");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(mapping.PhysicalSourceOrigin.IsKnown
                        ? mapping.PhysicalSourceOrigin.DisplayName
                        : mapping.ModdedPapPath);
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton("Remove"))
                    {
                        additionalCompatibleMappings.Remove(mapping.GamePath);
                        InvalidatePreviewCompatibility();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Remove this user-added logical mapping");
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            if (ImGui.TreeNode("Additional mapping details"))
            {
                foreach (var mapping in mappings)
                {
                    ImGui.TextWrapped($"Logical path: {mapping.GamePath}");
                    ImGui.TextWrapped($"Physical source: {mapping.ModdedPapPath}");
                    ImGui.TextUnformatted($"Mapping origin: {mapping.MappingOrigin.DisplayName()}");
                    ImGui.Spacing();
                }
                ImGui.TreePop();
            }
        }

        // ======================================
        // STEP 3 – Target emote selection
        // ======================================
        private void DrawStepCard_SelectTarget()
        {
#if DEBUG
            using var step3DrawTiming = DancyStep3PerformanceTelemetry.Measure("DrawStep3");
#endif
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

            ImGui.Text($"Source: {DescribeSourceAnimation(opt)} · {selectedSourceEntries.Count} Loop source{(selectedSourceEntries.Count == 1 ? string.Empty : "s")} selected");
            ImGui.Spacing();

            ImGui.PushItemWidth(320f);
#if DEBUG
            using (DancyStep3PerformanceTelemetry.Measure("Step3SearchInput"))
            {
                ImGui.InputText("Find target", ref emoteSearch, 100);
            }
#else
            ImGui.InputText("Find target", ref emoteSearch, 100);
#endif
            ImGui.PopItemWidth();
            if (!string.IsNullOrWhiteSpace(emoteSearch))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear"))
                    emoteSearch = string.Empty;
                ImGui.TextDisabled($"Searching all target types for \"{emoteSearch}\".");
            }

#if DEBUG
            using (DancyStep3PerformanceTelemetry.Measure("Step3Tabs"))
            {
#endif
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
#if DEBUG
            }
#endif

            ImGui.Spacing();

            var isSearchingTargets = !string.IsNullOrWhiteSpace(emoteSearch);
            var results = GetTargetCatalogResults(targetSelectionCategory, emoteSearch);
            RequestVisibleTargetInspections(results);

            if (results.Count == 0)
            {
                ImGui.TextWrapped(isSearchingTargets
                    ? "No compatible targets match this search. Clear the search or try a different name."
                    : "No compatible targets are available in this category.");
                EndCard();
                return;
            }

            var flags = ImGuiTableFlags.RowBg
                        | ImGuiTableFlags.BordersInnerV
                        | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("DancyTargetTable", 4, flags))
            {
#if DEBUG
                using var targetRowsTiming = DancyStep3PerformanceTelemetry.Measure("DrawTargetRows");
#endif
                ImGui.TableSetupColumn("Target");
                ImGui.TableSetupColumn("Type");
                ImGui.TableSetupColumn("Variants", ImGuiTableColumnFlags.WidthFixed, 72f);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 132f);
                ImGui.TableHeadersRow();

                foreach (var presentation in results)
                {
                    var emote = presentation.Target;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    bool isSelected = ReferenceEquals(selectedReplacementEmote, emote);
                    string label = $"{emote.Name}##{emote.TargetId}";
                    var hasTargetStatus = TryGetTargetPapStatus(emote, out var targetSnapshot)
                        && targetSnapshot!.State == TargetInspectionState.Ready
                        && targetSnapshot.Status is not null;
                    var targetStatus = hasTargetStatus ? targetSnapshot!.Status! : null;

                    if (hasTargetStatus && !targetStatus!.IsSupported && isSelected)
                    {
                        selectedReplacementEmote = null;
                        InvalidatePreviewCompatibility();
                        isSelected = false;
                    }

                    if (!hasTargetStatus || !targetStatus!.IsSupported)
                        ImGui.BeginDisabled();
                    if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        selectedReplacementEmote = emote;
                        InvalidatePreviewCompatibility();
                    }
                    if (!hasTargetStatus || !targetStatus!.IsSupported)
                        ImGui.EndDisabled();

                    if (!string.IsNullOrWhiteSpace(emote.Command))
                        ImGui.TextDisabled(emote.Command);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(presentation.BehaviorDisplayName);
                    ImGui.TextDisabled(presentation.ContextDisplayName);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(hasTargetStatus ? DescribeVariantCount(targetStatus!, targetSnapshot!.Paths.Count) : "Checking...");
                    ImGui.TableNextColumn();
                    var hasPartialVariantSupport = hasTargetStatus && HasPartialVariantSupport(targetStatus!);
                    if (!hasTargetStatus)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
                    else if (!targetStatus!.IsSupported)
                        ImGui.PushStyleColor(ImGuiCol.Text, ErrorTextColor);
                    else if (hasPartialVariantSupport)
                        ImGui.PushStyleColor(ImGuiCol.Text, CautionTextColor);
                    ImGui.TextWrapped(hasTargetStatus ? targetStatus!.Summary : "Checking target structure");
                    if (!hasTargetStatus || !targetStatus!.IsSupported || hasPartialVariantSupport)
                    {
                        ImGui.PopStyleColor();
                        if (hasTargetStatus && targetStatus is not null && ImGui.IsItemHovered())
                            ImGui.SetTooltip(targetStatus.Detail);
                    }
                }

                ImGui.EndTable();
            }

            if (plugin.TargetInspections.PendingTargetCount > 0)
                ImGui.TextDisabled($"Checking target structure in the background.");

            ImGui.Spacing();

            if (selectedReplacementEmote != null)
            {
                if (string.IsNullOrWhiteSpace(selectedModFolder))
                {
                    ImGui.TextDisabled("Waiting for the selected source mod to finish loading...");
                }
                else if (!TryGetTargetPapStatus(selectedReplacementEmote, out var targetSnapshot)
                    || targetSnapshot!.State != TargetInspectionState.Ready
                    || targetSnapshot.Status is null)
                {
                    plugin.TargetInspections.RequestTarget(CreateTargetInspectionRequest(selectedReplacementEmote));
                    ImGui.TextDisabled("Checking selected target structure...");
                }
                else
                {
                    var targetStatus = targetSnapshot.Status;
                    var targetPaths = targetSnapshot.Paths;
                    previewPlan ??= CreatePreviewPlan(opt, selectedReplacementEmote, selectedSourceEntries);
                    var compatibilityKey = CreateCompatibilityKey(previewPlan, selectedReplacementEmote);
                    plugin.TargetInspections.RequestCompatibility(compatibilityKey, GetSelectedModFolder(), previewPlan, targetSnapshot);
                    _ = plugin.TargetInspections.TryGetCompatibility(compatibilityKey, out var compatibilitySnapshot);
                    var isCompatibilityReady = compatibilitySnapshot?.State == TargetInspectionState.Ready
                        && compatibilitySnapshot.Compatibility is not null;

                    var behaviorNotice = TargetSemantics.BehaviorNotice(selectedReplacementEmote.Behavior);
                    if (behaviorNotice is not null)
                    {
                        DrawBehaviorNotice(behaviorNotice);
                        ImGui.Spacing();
                    }

                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Selected target");
                    if (BeginKeyValueTable("DancyTargetSummary"))
                    {
                        DrawSummaryRow("Target", selectedReplacementEmote.Name);
                        DrawSummaryRow("Type", TargetSemantics.DisplayName(selectedReplacementEmote.Behavior));
                        DrawSummaryRow("Context", TargetSemantics.DisplayName(selectedReplacementEmote.Context));
                        DrawSummaryRow("Variants", DescribeVariantCount(targetStatus, targetPaths.Count));
                        DrawSummaryRow("Compatibility", isCompatibilityReady ? DescribeCompatibility(compatibilitySnapshot!.Compatibility!) : "Checking...");
                        if (targetStatus.UsesStandingIdleWriter)
                            DrawSummaryRow("Strategy", "Preserve target idle structure");
                        ImGui.EndTable();
                    }

                    if (!isCompatibilityReady)
                    {
                        ImGui.TextDisabled("Checking source and target compatibility in the background...");
                    }
                    else
                    {
                        var readyCompatibility = compatibilitySnapshot!.Compatibility!;
                        previewCompatibility = readyCompatibility;
                        previewSourceSelections.Clear();
                        if (compatibilitySnapshot.SourceSelections is not null)
                        {
                            foreach (var selection in compatibilitySnapshot.SourceSelections)
                                previewSourceSelections[selection.Key] = selection.Value;
                        }

                        if (readyCompatibility.Status is PapCompatibilityStatus.Unsupported or PapCompatibilityStatus.Unknown)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, ErrorTextColor);
                            ImGui.TextWrapped(DescribeBlockedCombination(readyCompatibility));
                            ImGui.PopStyleColor();
                        }
                        else if (readyCompatibility.Status == PapCompatibilityStatus.CompatibleWithWarning)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, CautionTextColor);
                            ImGui.TextWrapped("Dancy can create this override, but the selected source and target use different variants.");
                            ImGui.PopStyleColor();
                        }
                        else if (HasPartialVariantSupport(targetStatus))
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, CautionTextColor);
                            ImGui.TextWrapped("Only some target variants are supported. Dancy will leave the remaining variants untouched.");
                            ImGui.PopStyleColor();
                        }

                        if (ImGui.TreeNode("Details"))
                        {
                            var selectedTrigger = string.IsNullOrWhiteSpace(selectedReplacementEmote.Trigger)
                                ? selectedReplacementEmote.Command
                                : selectedReplacementEmote.Trigger;
                            ImGui.TextWrapped($"Trigger: {(string.IsNullOrWhiteSpace(selectedTrigger) ? "State-managed" : selectedTrigger)}");
                            ImGui.TextWrapped($"Timeline: {selectedReplacementEmote.PrimaryTimelineKey}");
                            ImGui.TextWrapped($"Compatibility details: {previewCompatibility.Reason}");
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

                        DrawMappingPreview(previewPlan, opt, selectedSourceEntries, selectedReplacementEmote, readyCompatibility);

                        ImGui.Spacing();
                        if (selectedSourceEntries.Count == 0)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, ErrorTextColor);
                            ImGui.Text("Select at least one Loop source in Step 2.");
                            ImGui.PopStyleColor();
                        }
                        else if (!previewPlan.IsValid || !readyCompatibility.CanCreate)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, ErrorTextColor);
                            ImGui.TextWrapped(!previewPlan.IsValid ? string.Join("\n", previewPlan.Errors) : readyCompatibility.Reason);
                            ImGui.PopStyleColor();
                        }
                        else if (ImGui.Button(isCreatingOverride ? "Creating override..." : "Create Dancy override") && !isCreatingOverride)
                        {
                            _ = CreateOverrideAsync(opt, selectedReplacementEmote, selectedSourceEntries, previewPlan);
                        }
                    }

                    if (selectedSourceEntries.Count > 0)
                    {
                        ImGui.SameLine();
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
                        ImGui.TextDisabled("Creates a new Dancy group and PAP copy. Original files remain untouched.");
                        ImGui.PopStyleColor();
                    }
                }
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
                ImGui.Text("Select a target emote above to continue.");
                ImGui.PopStyleColor();
            }

            EndCard();
#if DEBUG
            if (step3TransitionStartedAt != 0)
            {
                DancyStep3PerformanceTelemetry.RecordElapsed("Step2ToFirstStep3Ready", step3TransitionStartedAt);
                step3TransitionStartedAt = 0;
            }
#endif
        }

        // ======================================
        // Override creation backend
        // ======================================
        private void ResetSelectedSourceGamePaths(RemappableOption source)
        {
            selectedSourceGamePaths.Clear();
            additionalCompatibleMappings.Clear();
            selectedCompatiblePhysicalSourceKey = null;
            additionalMappingError = null;
            foreach (var entry in source.LoopEntries)
                selectedSourceGamePaths.Add(entry.GamePath);
        }

        private void InvalidatePreviewCompatibility()
        {
            previewPlan = null;
            previewCompatibility = null;
            previewSourceSelections.Clear();
            plugin.TargetInspections.InvalidateSourceCompatibility();
        }

        private void EnsureSelectedSourceGamePaths(RemappableOption source)
        {
            var validPaths = SourceSelectionPolicy.DefaultLoopGamePaths(source.Entries);

            selectedSourceGamePaths.RemoveWhere(path => !validPaths.Contains(path));

            if (selectedSourceGamePaths.Count == 0 && source.LoopEntries.Count == 1)
                selectedSourceGamePaths.Add(source.LoopEntries[0].GamePath);

        }

        private List<ParsedEmoteOverride> GetSelectedModProvidedSourceEntries(RemappableOption source)
            => SourceSelectionPolicy.SelectedLoopEntries(source.Entries, selectedSourceGamePaths).ToList();

        private List<ParsedEmoteOverride> GetSelectedSourceEntries(RemappableOption source)
            => GetSelectedModProvidedSourceEntries(source)
                .Concat(additionalCompatibleMappings.Values)
                .OrderBy(entry => entry.AppliesTo.Character.Code, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.GamePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

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
                    .Select(entry => new OverridePlanSource(
                        entry.GamePath,
                        entry.ModdedPapPath,
                        entry.MappingOrigin,
                        entry.PhysicalSourceGamePath))
                    .ToList(),
                CompanionTimelines = source.CompanionTimelines,
                TargetGamePaths = targetPaths,
            });
        }

        private IReadOnlyList<string> GetCompatibleTargetPaths(LuminaEmote target)
        {
            if (!TryGetTargetPapStatus(target, out var snapshot)
                || snapshot!.State != TargetInspectionState.Ready
                || snapshot.Status is null)
                return Array.Empty<string>();

            return snapshot.Status.UsesStandingIdleWriter && snapshot.Status.SupportedGamePaths is { Count: > 0 }
                ? snapshot.Status.SupportedGamePaths
                : snapshot.Paths;
        }

        private bool TryGetTargetPapStatus(LuminaEmote target, out TargetStructureSnapshot? snapshot)
        {
            return plugin.TargetInspections.TryGetTarget(GetTargetId(target), out snapshot);
        }

        private IReadOnlyList<TargetCatalogPresentation<LuminaEmote>> GetTargetCatalogResults(
            TargetSelectionCategory category,
            string query)
        {
            EnsureTargetCatalog();
#if DEBUG
            DancyStep3PerformanceTelemetry.RecordPresentationQuery();
            using var timing = DancyStep3PerformanceTelemetry.Measure("TargetCatalogQuery");
#endif
            return targetCatalog!.GetResults(category, query, maximumResults: 50);
        }

        private void EnsureTargetCatalog()
        {
            if (targetCatalog is not null)
                return;

#if DEBUG
            using var timing = DancyStep3PerformanceTelemetry.Measure("BuildTargetCatalogPresentation");
#endif
            targetCatalog = new TargetCatalogPresentationCache<LuminaEmote>(
                EmoteLibrary.AllEmotes.Select(emote => new TargetCatalogPresentation<LuminaEmote>(
                    emote,
                    GetTargetId(emote),
                    emote.Name,
                    emote.Command,
                    emote.Trigger,
                    emote.Behavior,
                    emote.Context,
                    TargetSemantics.DisplayName(emote.Behavior),
                    TargetSemantics.DisplayName(emote.Context))));
        }

        private void RequestVisibleTargetInspections(IReadOnlyList<TargetCatalogPresentation<LuminaEmote>> results)
        {
            foreach (var result in results)
                plugin.TargetInspections.RequestTarget(CreateTargetInspectionRequest(result.Target));
        }

        private static TargetInspectionRequest CreateTargetInspectionRequest(LuminaEmote target)
            => new(GetTargetId(target), target.PrimaryTimelineKey, target.Name, IsCanonicalStandingIdleTarget(target));

        private static string CreateCompatibilityKey(OverridePlan plan, LuminaEmote target)
            => $"{plan.OverrideId}|{GetTargetId(target)}";

        private string GetSelectedModFolder()
            => selectedModFolder ?? throw new InvalidOperationException("Dancy could not resolve the selected mod folder for PAP inspection.");

        private void InvalidateTargetCatalog()
        {
            targetCatalog = null;
            plugin.TargetInspections.InvalidateTargetCatalog();
            PapResolver.ClearCache();
        }

        private static string GetTargetId(LuminaEmote target)
            => string.IsNullOrWhiteSpace(target.TargetId) ? target.PrimaryTimelineKey : target.TargetId;

#if DEBUG
        internal DancySelfTestResult DebugProfileStep3TargetCatalog()
        {
            var completion = new TaskCompletionSource<DancySelfTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Svc.Framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    completion.TrySetResult(ProfileStep3TargetCatalogOnFrameworkThread());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            return completion.Task.GetAwaiter().GetResult();
        }

        private DancySelfTestResult ProfileStep3TargetCatalogOnFrameworkThread()
        {
            var started = DateTimeOffset.UtcNow;
            var cases = new List<DancySelfTestCase>();
            ProfileDeferredPresentation(cases, "Step 2 to Step 3 presentation", () =>
            {
                var results = GetTargetCatalogResults(TargetSelectionCategory.LoopingEmotes, string.Empty);
                RequestVisibleTargetInspections(results);
            });
            ProfileDeferredPresentation(cases, "Search and tab changes", () =>
            {
                foreach (var (category, query) in new[]
                {
                    (TargetSelectionCategory.LoopingEmotes, "a"),
                    (TargetSelectionCategory.LoopingEmotes, "at"),
                    (TargetSelectionCategory.PosesAndIdles, string.Empty),
                })
                {
                    var results = GetTargetCatalogResults(category, query);
                    RequestVisibleTargetInspections(results);
                }
            });
            ProfileIdleDrawPresentation(cases);
            InvalidateTargetCatalog();
            return new DancySelfTestResult
            {
                Schema = "dancy.step3-performance.v1",
                StartedAtUtc = started,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Cases = cases,
            };
        }

        private void ProfileDeferredPresentation(ICollection<DancySelfTestCase> cases, string name, Action action)
        {
            InvalidateTargetCatalog();
            DancyStep3PerformanceTelemetry.Reset();
            var allocatedBefore = GC.GetTotalAllocatedBytes(false);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                action();
                var snapshot = DancyStep3PerformanceTelemetry.Snapshot();
                var noBlockingWork = snapshot.TargetPapReads == 0
                    && snapshot.TargetPapInspections == 0
                    && snapshot.TargetStructuralPreflights == 0
                    && snapshot.PreviewCompatibilityPreflights == 0
                    && snapshot.GameFileExistsCalls == 0;
                cases.Add(new DancySelfTestCase
                {
                    TestName = name,
                    Stage = "deferred presentation",
                    Expected = "Presentation, search, and tab work perform zero PAP parsing, target structural preflight, game FileExists probes, filesystem work, or Penumbra IPC.",
                    Actual = $"{stopwatch.ElapsedMilliseconds} ms; allocated {GC.GetTotalAllocatedBytes(false) - allocatedBefore:N0} bytes; {snapshot.Describe()}",
                    Status = noBlockingWork ? DancySelfTestStatus.Passed : DancySelfTestStatus.Failed,
                    DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                    FailureReason = noBlockingWork ? null : "Deferred presentation performed target inspection work.",
                });
            }
            catch (Exception exception)
            {
                cases.Add(new DancySelfTestCase
                {
                    TestName = name,
                    Stage = "baseline",
                    Expected = "Read-only reproduction of the legacy synchronous target-row inspection path.",
                    Actual = exception.Message,
                    FailureReason = exception.ToString(),
                    Status = DancySelfTestStatus.Failed,
                    DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                });
            }
        }

        private void ProfileIdleDrawPresentation(ICollection<DancySelfTestCase> cases)
        {
            InvalidateTargetCatalog();
            DancyStep3PerformanceTelemetry.Reset();
            var results = GetTargetCatalogResults(TargetSelectionCategory.LoopingEmotes, string.Empty);
            RequestVisibleTargetInspections(results);
            var allocatedBefore = GC.GetTotalAllocatedBytes(false);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                for (var frame = 0; frame < 120; ++frame)
                {
                    var visible = GetTargetCatalogResults(TargetSelectionCategory.LoopingEmotes, string.Empty);
                    RequestVisibleTargetInspections(visible);
                }
                var snapshot = DancyStep3PerformanceTelemetry.Snapshot();
                var presentationOnly = snapshot.TargetPapReads == 0
                    && snapshot.TargetPapInspections == 0
                    && snapshot.TargetStructuralPreflights == 0
                    && snapshot.PreviewCompatibilityPreflights == 0
                    && snapshot.GameFileExistsCalls == 0
                    && snapshot.PenumbraIpcCalls == 0;
                cases.Add(new DancySelfTestCase
                {
                    TestName = "120 idle Step 3 Draw frames",
                    Stage = "presentation only",
                    Expected = "Draw queues missing structure but performs zero PAP parsing, structural preflight, game FileExists probes, filesystem work, or Penumbra IPC.",
                    Actual = $"{stopwatch.ElapsedMilliseconds} ms; allocated {GC.GetTotalAllocatedBytes(false) - allocatedBefore:N0} bytes; {snapshot.Describe()}",
                    Status = presentationOnly ? DancySelfTestStatus.Passed : DancySelfTestStatus.Failed,
                    DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                    FailureReason = presentationOnly ? null : "Idle Step 3 presentation performed target inspection work.",
                });
            }
            catch (Exception exception)
            {
                cases.Add(new DancySelfTestCase
                {
                    TestName = "120 idle Step 3 Draw frames",
                    Stage = "presentation only",
                    Expected = "Draw queues missing structure but performs zero heavyweight work.",
                    Actual = exception.Message,
                    FailureReason = exception.ToString(),
                    Status = DancySelfTestStatus.Failed,
                    DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                });
            }
        }
#endif

        private void DrawMappingPreview(
            OverridePlan plan,
            RemappableOption source,
            IReadOnlyList<ParsedEmoteOverride> selectedSources,
            LuminaEmote target,
            PapCompatibilityResult compatibility)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.85f, 0.9f, 1f, 1f), "Mapping preview");
            if (!plan.IsValid)
            {
                ImGui.TextDisabled("No metadata will be changed until this plan is valid.");
                return;
            }

            if (BeginKeyValueTable("DancyMappingPreview"))
            {
                var sourceSelection = PreviewSelectionFor(plan);
                DrawSummaryRow("Source", DescribeSourceAnimation(source));
                DrawSummaryRow("Option", source.OptionName);
                DrawSummaryRow("Source structure", DescribeSourceStructure(sourceSelection));
                if (sourceSelection?.Selection.Method == SourceAnimationSelectionMethod.CompanionTimelineEvent)
                    DrawSummaryRow("Selected animation", source.OptionName);
                DrawSummaryRow("Target", target.Name);
                DrawSummaryRow("Affected", $"{plan.PlannedMappings.Count} target path{(plan.PlannedMappings.Count == 1 ? string.Empty : "s")}");
                DrawSummaryRow("PAP origin", DescribePapOrigins(selectedSources));
                DrawSummaryRow("Source mappings", DescribeMappingOrigins(plan.SourceMappings));
                DrawSummaryRow("Strategy", DescribeMappingStrategy(compatibility.WriteStrategy));
                DrawSummaryRow("Compatibility", DescribeCompatibility(compatibility));
                ImGui.EndTable();
            }

            if (ImGui.TreeNode("Technical mapping details"))
            {
                foreach (var mapping in plan.SourceMappings)
                {
                    ImGui.TextWrapped($"Logical source: {mapping.GamePath}");
                    ImGui.TextWrapped($"Physical source: {mapping.SourcePapPath}");
                    ImGui.TextWrapped($"Mapping origin: {mapping.MappingOrigin.DisplayName()}");
                }
                foreach (var copy in plan.PapCopies)
                {
                    var strategies = string.Join(", ", copy.MatchResults
                        .Select(result => result.Strategy.ToString())
                        .Distinct(StringComparer.Ordinal));
                    ImGui.TextWrapped($"Source PAP: {copy.SourcePapPath}");
                    ImGui.TextWrapped($"Match strategy: {strategies}");
                    if (previewSourceSelections.TryGetValue(copy.OutputRelativePath, out var selection))
                    {
                        ImGui.TextWrapped($"Selector: {selection.Selection.AnimationEvent}");
                        ImGui.TextWrapped($"Selection: Header {selection.Selection.AnimationHeaderIndex} · Motion {selection.Selection.HavokMotionIndex} · Timeline {selection.Selection.EmbeddedTmbIndex}");
                        ImGui.TextWrapped($"Selection evidence: {DescribeSelectionMethod(selection.Selection.Method)}");
                    }
                    foreach (var targetPath in copy.TargetGamePaths)
                        ImGui.BulletText(targetPath);
                }
                if (PreviewSelectionFor(plan)?.Selection.Method == SourceAnimationSelectionMethod.CompanionTimelineEvent
                    && source.CompanionTimelines.Any(timeline => timeline.IsActionTimeline))
                {
                    ImGui.TextDisabled("Source option includes companion timeline effects that are not part of the PAP override.");
                }
                ImGui.TreePop();
            }

            foreach (var warning in plan.Warnings)
                ImGui.TextWrapped($"Warning: {warning}");
        }

        private static bool IsCanonicalStandingIdleTarget(LuminaEmote target)
            => string.Equals(target.PrimaryTimelineKey, "normal/idle", StringComparison.OrdinalIgnoreCase);

        private TargetSourceSelectionPreview? PreviewSelectionFor(OverridePlan plan)
            => plan.PapCopies
                .Select(copy => previewSourceSelections.TryGetValue(copy.OutputRelativePath, out var selection) ? selection : null)
                .FirstOrDefault(selection => selection is not null);

        private static string DescribeSourceStructure(TargetSourceSelectionPreview? preview)
        {
            if (preview is null)
                return "Inspecting source";
            return preview.AnimationCount == 1
                ? "Single motion"
                : $"Animation bank · {preview.AnimationCount} motions";
        }

        private static string DescribeSelectionMethod(SourceAnimationSelectionMethod method)
            => method switch
            {
                SourceAnimationSelectionMethod.SingleMotion => "Single source motion",
                SourceAnimationSelectionMethod.CompanionTimelineEvent => "Companion timeline event",
                _ => method.ToString(),
            };

        private static string DescribeSourceAnimation(RemappableOption source)
        {
            var animations = source.LogicalAnimations
                .Select(animation => string.IsNullOrWhiteSpace(animation.Command)
                    ? animation.Name
                    : $"{animation.Name} ({animation.Command})")
                .ToList();
            return animations.Count == 0 ? source.OptionName : string.Join(", ", animations);
        }

        private static string DescribeVariantCount(TargetStructureStatus status, int targetPathCount)
        {
            if (!status.UsesStandingIdleWriter)
                return targetPathCount.ToString();

            if (status.TotalVariantCount == 0)
                return "0";

            return status.SupportedVariantCount == status.TotalVariantCount
                ? status.TotalVariantCount.ToString()
                : $"{status.SupportedVariantCount} / {status.TotalVariantCount}";
        }

        private static string DescribeCompatibility(PapCompatibilityResult compatibility)
            => compatibility.Status switch
            {
                PapCompatibilityStatus.Compatible => "Compatible",
                PapCompatibilityStatus.CompatibleWithWarning => "Compatible with note",
                PapCompatibilityStatus.Unsupported => "Unsupported",
                _ => "Needs review",
            };

        private static string DescribeBlockedCombination(PapCompatibilityResult compatibility)
            => compatibility.Blocker switch
            {
                PapCompatibilityBlocker.Source => compatibility.Reason,
                PapCompatibilityBlocker.Target => "Dancy cannot safely use the selected target's current PAP structure. Choose another target.",
                _ => "Dancy cannot safely create this combination. Review the compatibility details before choosing another source or target.",
            };

        private static string DescribeMappingStrategy(PapOverrideWriteStrategy? strategy)
            => strategy switch
            {
                PapOverrideWriteStrategy.StandingIdleMotion0 => "Preserve target idle structure",
                PapOverrideWriteStrategy.SelectorBankEventPatch => "Preserve selected source animation bank",
                PapOverrideWriteStrategy.SingleSectionEventPatch => "Standard target redirect",
                _ => "Validated target mapping",
            };

        private static string DescribePapOrigins(IReadOnlyList<ParsedEmoteOverride> sources)
        {
            var origins = sources
                .Select(entry => entry.PapOrigin.IsKnown ? entry.PapOrigin.DisplayName : "Mod-relative file")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return origins.Count switch
            {
                0 => "Not available",
                1 => origins[0],
                _ => $"{origins.Count} source variants",
            };
        }

        private static string DescribeMappingOrigins(IReadOnlyList<OverridePlanSource> sources)
        {
            var origins = sources
                .Select(source => source.MappingOrigin.DisplayName())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return origins.Count switch
            {
                0 => "Not available",
                1 => origins[0],
                _ => "Mixed source mappings",
            };
        }

        private static void DrawSummaryRow(string label, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled(label);
            ImGui.TableNextColumn();
            if (ImGui.CalcTextSize(value).X <= ImGui.GetContentRegionAvail().X)
                ImGui.TextUnformatted(value);
            else
                ImGui.TextWrapped(value);
        }

        private static bool BeginKeyValueTable(string id)
        {
            var flags = ImGuiTableFlags.BordersInnerV
                        | ImGuiTableFlags.SizingStretchProp
                        | ImGuiTableFlags.NoSavedSettings;
            if (!ImGui.BeginTable(id, 2, flags))
                return false;

            var labelWidth = Math.Max(112f, ImGui.CalcTextSize("Compatibility").X + 20f);
            ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, labelWidth);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1f);
            return true;
        }

        private static bool HasPartialVariantSupport(TargetStructureStatus status)
            => status.IsSupported
                && status.UsesStandingIdleWriter
                && status.TotalVariantCount > status.SupportedVariantCount;

        private static void DrawBehaviorNotice(TargetBehaviorNotice notice)
        {
            if (notice.Level == TargetNoticeLevel.Informational)
            {
                ImGui.TextDisabled(notice.Description);
                return;
            }

            ImGui.PushStyleColor(ImGuiCol.Text, CautionTextColor);
            if (!string.IsNullOrWhiteSpace(notice.Heading))
                ImGui.TextUnformatted(notice.Heading);
            ImGui.TextWrapped(notice.Description);
            ImGui.PopStyleColor();
        }

        private static ExistingOverrideSummary DescribeExistingOverride(DancyOverrideInfo existing)
        {
            var descriptionSource = DescriptionValue(existing.Description, "Animation:");
            var descriptionTarget = DescriptionValue(existing.Description, "Target:");
            var descriptionOption = DescriptionValue(existing.Description, "Option:");
            var legacy = string.IsNullOrWhiteSpace(descriptionSource) || string.IsNullOrWhiteSpace(descriptionTarget);

            var source = descriptionSource;
            var target = descriptionTarget;
            if (legacy)
            {
                var separator = existing.Name.IndexOf(" -> ", StringComparison.Ordinal);
                if (separator >= 0)
                {
                    source = existing.Name[..separator].Trim();
                    var targetWithCount = existing.Name[(separator + 4)..];
                    var countSeparator = targetWithCount.LastIndexOf(" · ", StringComparison.Ordinal);
                    target = (countSeparator < 0 ? targetWithCount : targetWithCount[..countSeparator]).Trim();
                }
            }

            source = string.IsNullOrWhiteSpace(source) ? "Dancy source" : source;
            target = string.IsNullOrWhiteSpace(target) ? "Dancy target" : target;
            var targetModel = EmoteLibrary.AllEmotes.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, target, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(candidate.Name + " (", StringComparison.OrdinalIgnoreCase));

            return new ExistingOverrideSummary(
                source,
                descriptionOption,
                target,
                targetModel is null ? "Dancy override" : TargetSemantics.DisplayName(targetModel.Behavior),
                targetModel is null ? "Details unavailable" : TargetSemantics.DisplayName(targetModel.Context),
                DescribeExistingMappingOrigin(existing.Description),
                DescribeExistingMappingDetails(existing.Description),
                legacy);
        }

        private static string DescribeExistingMappingOrigin(string description)
        {
            var details = DescribeExistingMappingDetails(description);
            var hasUserAdded = details.Contains("User-added compatible mapping", StringComparison.OrdinalIgnoreCase);
            var hasSourceProvided = details.Contains("Source-provided mapping", StringComparison.OrdinalIgnoreCase);
            if (hasUserAdded && hasSourceProvided)
                return "Mixed source mappings";
            if (hasUserAdded)
                return "User-added compatible mapping";
            if (hasSourceProvided)
                return "Source-provided mapping";
            return "Mapping details unavailable";
        }

        private static string DescribeExistingMappingDetails(string description)
        {
            const string marker = "Source mappings:\n";
            var start = description.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;

            start += marker.Length;
            var end = description.IndexOf("\n\nTarget:", start, StringComparison.OrdinalIgnoreCase);
            return (end < 0 ? description[start..] : description[start..end]).Trim();
        }

        private static string DescriptionValue(string description, string label)
        {
            var line = description.Split('\n')
                .Select(value => value.Trim())
                .FirstOrDefault(value => value.StartsWith(label, StringComparison.OrdinalIgnoreCase));
            return line is null ? string.Empty : line[label.Length..].Trim();
        }

        private static void CopyExistingOverrideDetails(DancyOverrideInfo existing, ExistingOverrideSummary summary)
        {
            var details = new StringBuilder();
            details.AppendLine("Dancy override");
            details.AppendLine($"Source: {summary.Source}");
            if (!string.IsNullOrWhiteSpace(summary.Option))
                details.AppendLine($"Option: {summary.Option}");
            details.AppendLine($"Target: {summary.Target}");
            details.AppendLine($"Type: {summary.Type}");
            details.AppendLine($"Context: {summary.Context}");
            details.AppendLine($"Source mapping: {summary.SourceMapping}");
            if (!string.IsNullOrWhiteSpace(summary.SourceMappingDetails))
                details.AppendLine($"Source mapping details: {summary.SourceMappingDetails}");
            details.AppendLine($"Affected: {existing.Mappings.Count} target path{(existing.Mappings.Count == 1 ? string.Empty : "s")}");
            details.AppendLine();
            details.AppendLine("Mappings:");
            foreach (var mapping in existing.Mappings.OrderBy(mapping => mapping.Key, StringComparer.OrdinalIgnoreCase))
                details.AppendLine($"{mapping.Key} -> {mapping.Value}");
            ImGui.SetClipboardText(details.ToString());
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
            foreach (var mapping in plan.SourceMappings)
            {
                diagnostics.AppendLine($"Source mapping: {mapping.MappingOrigin.DisplayName()}; logical {mapping.GamePath}; physical {mapping.SourcePapPath}");
            }
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
                if (patch.SourceSelection is not null)
                {
                    diagnostics.AppendLine($"  source selection: {patch.SourceSelection.Method}; header {patch.SourceSelection.AnimationHeaderIndex}; motion {patch.SourceSelection.HavokMotionIndex}; timeline {patch.SourceSelection.EmbeddedTmbIndex}; event {patch.SourceSelection.AnimationEvent}");
                    diagnostics.AppendLine($"  selection evidence: {patch.SourceSelection.Evidence}");
                }
                if (!string.IsNullOrWhiteSpace(patch.SourceMotionFingerprint))
                    diagnostics.AppendLine($"  selected source fingerprint: {patch.SourceMotionFingerprint}");
                if (patch.WriteStrategy == PapOverrideWriteStrategy.StandingIdleMotion0)
                {
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
