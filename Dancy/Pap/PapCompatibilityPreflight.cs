using System;
using System.Collections.Generic;
using System.Linq;

namespace Dancy.Pap;

public enum PapCompatibilityStatus
{
    Compatible,
    CompatibleWithWarning,
    Unsupported,
    Unknown,
}

public enum PapOverrideWriteStrategy
{
    SingleSectionEventPatch,
    SelectorBankEventPatch,
    StandingIdleMotion0,
}

public enum PapCompatibilityBlocker
{
    None,
    Source,
    Target,
}

public sealed record PapTargetInspection(string GamePath, PapFileInspector.PapFileInspection Inspection);

public sealed record PapCompatibilityResult(
    PapCompatibilityStatus Status,
    string Reason,
    PapOverrideWriteStrategy? WriteStrategy = null,
    PapCompatibilityBlocker Blocker = PapCompatibilityBlocker.None)
{
    public bool CanCreate => Status is PapCompatibilityStatus.Compatible or PapCompatibilityStatus.CompatibleWithWarning;
}

/// <summary>
/// Validates the structural assumptions made by Dancy's intentionally narrow PAP
/// repath writer. It does not make claims about skeleton compatibility.
/// </summary>
public static class PapCompatibilityPreflight
{
    public static PapCompatibilityResult Evaluate(
        PapFileInspector.PapFileInspection source,
        IEnumerable<PapFileInspector.PapFileInspection> targets)
        => Evaluate(source, targets.Select(target => new PapTargetInspection(string.Empty, target)));

    public static PapCompatibilityResult Evaluate(
        PapFileInspector.PapFileInspection source,
        IEnumerable<PapTargetInspection> targets)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targets);

        var targetList = targets.ToList();
        if (targetList.Count == 0)
            return new PapCompatibilityResult(PapCompatibilityStatus.Unknown, "Dancy could not inspect any target PAP variants.");

        if (source.AnimationCount != 1 || source.TimelineSectionSizes.Count != 1)
        {
            return new PapCompatibilityResult(
                PapCompatibilityStatus.Unsupported,
                DescribeUnsupportedSource(source),
                Blocker: PapCompatibilityBlocker.Source);
        }

        var results = targetList.Select(target => Evaluate(source, target)).ToList();
        return Combine(results);
    }

    public static PapCompatibilityResult Evaluate(
        PapFileInspector.PapFileInspection source,
        SourceAnimationSelection selection,
        IEnumerable<PapTargetInspection> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var targetList = targets.ToList();
        if (targetList.Count == 0)
            return new PapCompatibilityResult(PapCompatibilityStatus.Unknown, "Dancy could not inspect any target PAP variants.");

        return Combine(targetList.Select(target => Evaluate(source, selection, target)));
    }

    public static PapCompatibilityResult Evaluate(
        PapFileInspector.PapFileInspection source,
        PapTargetInspection target)
        => Evaluate(source, CreateSingleMotionSelection(source), target);

    public static PapCompatibilityResult Evaluate(
        PapFileInspector.PapFileInspection source,
        SourceAnimationSelection selection,
        PapTargetInspection target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(target);

        if (!TryValidateSelection(source, selection, out var selectionError))
        {
            return new PapCompatibilityResult(
                PapCompatibilityStatus.Unsupported,
                selectionError,
                Blocker: PapCompatibilityBlocker.Source);
        }

        if (IsSupportedStandingIdleTarget(target, out var standingIdleReason))
        {
            if (source.AnimationCount != 1 || selection.Method != SourceAnimationSelectionMethod.SingleMotion)
            {
                return new PapCompatibilityResult(
                    PapCompatibilityStatus.Unsupported,
                    "Standing Idle currently requires a one-motion source PAP. Dancy has not enabled selector-backed source banks for this target topology.",
                    Blocker: PapCompatibilityBlocker.Source);
            }

            if (selection.HavokMotionIndex != 0)
            {
                return new PapCompatibilityResult(
                    PapCompatibilityStatus.Unsupported,
                    "The proven Standing Idle route requires a one-section loop source bound to Havok motion 0.",
                    Blocker: PapCompatibilityBlocker.Source);
            }

            return new PapCompatibilityResult(
                PapCompatibilityStatus.Compatible,
                standingIdleReason,
                PapOverrideWriteStrategy.StandingIdleMotion0);
        }

        if (target.Inspection.AnimationCount != 1 || target.Inspection.TimelineSectionSizes.Count != 1)
        {
            return new PapCompatibilityResult(
                PapCompatibilityStatus.Unsupported,
                "The target PAP is not Dancy's exact supported Standing Idle layout and has multiple animation/header or TMB sections. Dancy will not rewrite an arbitrary section.",
                Blocker: PapCompatibilityBlocker.Target);
        }

        var targetEvent = target.Inspection.AnimationNames.SingleOrDefault();
        if (selection.Method == SourceAnimationSelectionMethod.CompanionTimelineEvent
            && !string.IsNullOrWhiteSpace(targetEvent)
            && source.AnimationNames.Where((_, index) => index != selection.AnimationHeaderIndex)
                .Any(name => string.Equals(name, targetEvent, StringComparison.OrdinalIgnoreCase)))
        {
            return new PapCompatibilityResult(
                PapCompatibilityStatus.Unsupported,
                "The target animation event already belongs to an untouched source-bank section. Dancy will not create a duplicate event binding.",
                Blocker: PapCompatibilityBlocker.Target);
        }

        var writeStrategy = selection.Method == SourceAnimationSelectionMethod.CompanionTimelineEvent
            ? PapOverrideWriteStrategy.SelectorBankEventPatch
            : PapOverrideWriteStrategy.SingleSectionEventPatch;
        var sourceHavok = selection.HavokMotionIndex;
        var targetHavok = target.Inspection.HavokIndices.SingleOrDefault();
        if (targetHavok != sourceHavok)
        {
            return new PapCompatibilityResult(
                PapCompatibilityStatus.CompatibleWithWarning,
                selection.Method == SourceAnimationSelectionMethod.CompanionTimelineEvent
                    ? "The selected source motion uses a different Havok index than the target. Dancy preserves the complete verified source bank and updates only the selected target event."
                    : "The source and target use different Havok indices. Dancy preserves the source motion and updates the target event identifier.",
                writeStrategy);
        }

        return new PapCompatibilityResult(
            PapCompatibilityStatus.Compatible,
            selection.Method == SourceAnimationSelectionMethod.CompanionTimelineEvent
                ? "The verified selector-backed source bank will be retained in full while Dancy updates only the selected target event."
                : "Source and target PAP variants use Dancy's supported one-animation, one-TMB-section structure.",
            writeStrategy);
    }

    public static bool IsSupportedStandingIdleTarget(PapTargetInspection target, out string reason)
    {
        ArgumentNullException.ThrowIfNull(target);
        var inspection = target.Inspection;
        if (!IsStandingIdlePath(target.GamePath))
        {
            reason = "The target path is not the canonical normal/idle Standing Idle path.";
            return false;
        }

        if (inspection.AnimationCount != 2 || inspection.TimelineSectionSizes.Count != 2)
        {
            reason = "Standing Idle requires exactly two animation headers and two TMB sections.";
            return false;
        }

        if (!Matches(inspection.AnimationNames, 0, "cbna_add_dmg_f")
            || !Matches(inspection.AnimationNames, 1, "cbnm_id0")
            || !Matches(inspection.AnimationTypes, 0, 15)
            || !Matches(inspection.AnimationTypes, 1, 0)
            || !Matches(inspection.HavokIndices, 0, 0)
            || !Matches(inspection.HavokIndices, 1, 1)
            || !Matches(inspection.FaceAnimationFlags, 0, false)
            || !Matches(inspection.FaceAnimationFlags, 1, false))
        {
            reason = "Standing Idle does not match the proven cbna_add_dmg_f/H0/T0 plus cbnm_id0/H1/T1 topology.";
            return false;
        }

        reason = "The target matches Dancy's proven Standing Idle topology: replace motion 0 only while preserving target motion 1 and both target-native TMB sections.";
        return true;
    }

    private static bool IsStandingIdlePath(string path)
        => path.Replace('\\', '/').EndsWith("/bt_common/resident/idle.pap", StringComparison.OrdinalIgnoreCase);

    private static bool Matches<T>(IReadOnlyList<T> values, int index, T expected)
        => index >= 0 && index < values.Count && EqualityComparer<T>.Default.Equals(values[index], expected);

    private static string DescribeUnsupportedSource(PapFileInspector.PapFileInspection source)
        => $"The selected source PAP contains {source.AnimationCount} animation header{Plural(source.AnimationCount)} and {source.TimelineSectionSizes.Count} TMB section{Plural(source.TimelineSectionSizes.Count)}. Dancy only supports source PAPs with one animation header and one TMB section.";

    private static SourceAnimationSelection CreateSingleMotionSelection(PapFileInspector.PapFileInspection source)
    {
        if (source.AnimationCount != 1 || source.TimelineSectionSizes.Count != 1 || source.AnimationNames.Count != 1 || source.HavokIndices.Count != 1)
            return new SourceAnimationSelection(string.Empty, string.Empty, -1, string.Empty, -1, -1, SourceAnimationSelectionMethod.SingleMotion, string.Empty);

        return new SourceAnimationSelection(
            string.Empty,
            string.Empty,
            0,
            source.AnimationNames[0],
            source.HavokIndices[0],
            0,
            SourceAnimationSelectionMethod.SingleMotion,
            "Implicit selection from a one-motion source PAP.");
    }

    private static bool TryValidateSelection(PapFileInspector.PapFileInspection source, SourceAnimationSelection selection, out string error)
    {
        if (selection.AnimationHeaderIndex < 0 || selection.AnimationHeaderIndex >= source.AnimationCount
            || selection.EmbeddedTmbIndex < 0 || selection.EmbeddedTmbIndex >= source.TimelineSectionSizes.Count
            || selection.EmbeddedTmbIndex != selection.AnimationHeaderIndex
            || selection.AnimationHeaderIndex >= source.AnimationNames.Count
            || selection.AnimationHeaderIndex >= source.HavokIndices.Count)
        {
            error = source.AnimationCount > 1
                ? "This source PAP contains multiple animations, and Dancy could not determine one unambiguous source animation unit."
                : DescribeUnsupportedSource(source);
            return false;
        }

        if (!string.Equals(source.AnimationNames[selection.AnimationHeaderIndex], selection.AnimationEvent, StringComparison.OrdinalIgnoreCase)
            || source.HavokIndices[selection.AnimationHeaderIndex] != selection.HavokMotionIndex
            || selection.HavokMotionIndex < 0)
        {
            error = "The selected source animation no longer matches its verified PAP header or Havok motion binding.";
            return false;
        }

        if (source.AnimationCount > 1 && selection.Method != SourceAnimationSelectionMethod.CompanionTimelineEvent)
        {
            error = "This source PAP contains multiple animations, and Dancy could not determine which animation this mod option uses.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string Plural(int count)
        => count == 1 ? string.Empty : "s";

    public static PapCompatibilityResult Combine(IEnumerable<PapCompatibilityResult> results)
    {
        var all = results.ToList();
        if (all.Count == 0)
            return new PapCompatibilityResult(PapCompatibilityStatus.Unknown, "No selected source PAPs were available for compatibility inspection.");

        var unsupported = all.FirstOrDefault(result => result.Status == PapCompatibilityStatus.Unsupported);
        if (unsupported is not null)
            return unsupported;
        var unknown = all.FirstOrDefault(result => result.Status == PapCompatibilityStatus.Unknown);
        if (unknown is not null)
            return unknown;
        var warning = all.FirstOrDefault(result => result.Status == PapCompatibilityStatus.CompatibleWithWarning);
        if (warning is not null)
            return warning;

        var strategies = all.Select(result => result.WriteStrategy).Distinct().ToList();
        if (strategies.Count == 1)
            return all[0];

        return new PapCompatibilityResult(
            PapCompatibilityStatus.CompatibleWithWarning,
            "The selected targets use more than one supported Dancy write strategy; each target will be generated and validated independently.");
    }
}
