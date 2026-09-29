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
    StandingIdleMotion0,
}

public sealed record PapTargetInspection(string GamePath, PapFileInspector.PapFileInspection Inspection);

public sealed record PapCompatibilityResult(
    PapCompatibilityStatus Status,
    string Reason,
    PapOverrideWriteStrategy? WriteStrategy = null)
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
                "The selected source PAP has multiple animation/header or TMB sections. Dancy's repath writer only supports one-section loop PAPs.");
        }

        var results = targetList.Select(target => Evaluate(source, target)).ToList();
        return Combine(results);
    }

    public static PapCompatibilityResult Evaluate(
        PapFileInspector.PapFileInspection source,
        PapTargetInspection target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (source.AnimationCount != 1 || source.TimelineSectionSizes.Count != 1)
        {
            return new PapCompatibilityResult(
                PapCompatibilityStatus.Unsupported,
                "The selected source PAP has multiple animation/header or TMB sections. Dancy's repath writer only supports one-section loop PAPs.");
        }

        if (IsSupportedStandingIdleTarget(target, out var standingIdleReason))
        {
            if (!Matches(source.HavokIndices, 0, 0))
            {
                return new PapCompatibilityResult(
                    PapCompatibilityStatus.Unsupported,
                    "The proven Standing Idle route requires a one-section loop source bound to Havok motion 0.");
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
                "The target PAP is not Dancy's exact supported Standing Idle layout and has multiple animation/header or TMB sections. Dancy will not rewrite an arbitrary section.");
        }

        var sourceHavok = source.HavokIndices.SingleOrDefault();
        var targetHavok = target.Inspection.HavokIndices.SingleOrDefault();
        if (targetHavok != sourceHavok)
        {
            return new PapCompatibilityResult(
                PapCompatibilityStatus.CompatibleWithWarning,
                "The source and target use different Havok indices. Dancy preserves the source motion and updates the target event identifier.",
                PapOverrideWriteStrategy.SingleSectionEventPatch);
        }

        return new PapCompatibilityResult(
            PapCompatibilityStatus.Compatible,
            "Source and target PAP variants use Dancy's supported one-animation, one-TMB-section structure.",
            PapOverrideWriteStrategy.SingleSectionEventPatch);
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
