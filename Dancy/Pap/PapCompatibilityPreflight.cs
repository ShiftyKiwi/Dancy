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

public sealed record PapCompatibilityResult(PapCompatibilityStatus Status, string Reason)
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

        var complexTarget = targetList.FirstOrDefault(target => target.AnimationCount != 1 || target.TimelineSectionSizes.Count != 1);
        if (complexTarget is not null)
        {
            return new PapCompatibilityResult(
                PapCompatibilityStatus.Unsupported,
                "The target PAP has multiple animation/header or TMB sections. Dancy will not rewrite only its first section.");
        }

        var sourceHavok = source.HavokIndices.SingleOrDefault();
        var targetHavok = targetList.Select(target => target.HavokIndices.SingleOrDefault()).Distinct().ToList();
        if (targetHavok.Count > 1 || targetHavok.Any(index => index != sourceHavok))
        {
            return new PapCompatibilityResult(
                PapCompatibilityStatus.CompatibleWithWarning,
                "The source and target use different Havok indices. Dancy preserves the source motion and updates the target event identifier.");
        }

        return new PapCompatibilityResult(
            PapCompatibilityStatus.Compatible,
            "Source and target PAP variants use Dancy's supported one-animation, one-TMB-section structure.");
    }

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
        return warning ?? all[0];
    }
}
