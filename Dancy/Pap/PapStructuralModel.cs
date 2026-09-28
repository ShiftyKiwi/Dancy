using System;
using System.Collections.Generic;
using System.Linq;

namespace Dancy.Pap;

/// <summary>
/// Describes PAP container structure without interpreting or changing motion data.
/// A multi-section file remains Unknown until separate evidence proves that a
/// specific section can be replaced without affecting its companions.
/// </summary>
public enum PapTopology
{
    Unknown,
    SingleSection,
    MultiSectionIndependent,
    MultiSectionCoupled,
}

public enum PapBindingEvidence
{
    Unknown,
    ExplicitHeaderField,
    SequentialPapFormat,
}

/// <summary>
/// Dancy currently has no evidence to assign semantic Standing Idle roles.
/// Keeping this enum deliberately narrow prevents a filename from becoming a
/// production behavior claim.
/// </summary>
public enum PapSectionRole
{
    Unknown,
}

public sealed record PapTimelineSection(
    int Index,
    int Offset,
    int Size,
    string ContentSha256,
    IReadOnlyList<string> EventIdentifiers,
    string? EventInspectionIssue = null);

public sealed record PapAnimationSection(
    int AnimationIndex,
    string AnimationName,
    int AnimationType,
    bool IsFaceAnimation,
    int HavokMotionIndex,
    int? TimelineSectionIndex,
    PapSectionRole Role,
    PapBindingEvidence HavokBindingEvidence,
    PapBindingEvidence TimelineBindingEvidence);

public sealed record PapStructure(
    string SourceIdentity,
    IReadOnlyList<PapAnimationSection> Sections,
    IReadOnlyList<PapTimelineSection> TimelineSections,
    PapTopology Topology,
    IReadOnlyList<string> Evidence)
{
    public static PapStructure FromInspection(
        string sourceIdentity,
        PapFileInspector.PapFileInspection inspection,
        IReadOnlyList<PapTimelineSection>? timelineSections = null,
        PapTopology? topology = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        ArgumentNullException.ThrowIfNull(inspection);

        var sections = new List<PapAnimationSection>(inspection.AnimationCount);
        for (var index = 0; index < inspection.AnimationCount; index++)
        {
            var hasTimeline = index < inspection.TimelineSectionSizes.Count;
            sections.Add(new PapAnimationSection(
                index,
                ValueAt(inspection.AnimationNames, index, string.Empty),
                ValueAt(inspection.AnimationTypes, index, 0),
                ValueAt(inspection.FaceAnimationFlags, index, false),
                ValueAt(inspection.HavokIndices, index, -1),
                hasTimeline ? index : null,
                PapSectionRole.Unknown,
                PapBindingEvidence.ExplicitHeaderField,
                hasTimeline ? PapBindingEvidence.SequentialPapFormat : PapBindingEvidence.Unknown));
        }

        var details = timelineSections ?? inspection.TimelineSections
            .Select(location => new PapTimelineSection(location.Index, location.Offset, location.Size, string.Empty, Array.Empty<string>()))
            .ToList();
        var discoveredTopology = topology ?? (inspection.AnimationCount == 1 && inspection.TimelineSectionSizes.Count == 1
            ? PapTopology.SingleSection
            : PapTopology.Unknown);
        var evidence = new List<string>
        {
            "Animation header Havok indices are explicit PAP header fields.",
            "TMB sections are read sequentially in animation order by the PAP format parser; no per-header TMB pointer is present in the inspected header.",
        };
        if (discoveredTopology == PapTopology.Unknown && inspection.AnimationCount > 1)
            evidence.Add("Multi-section semantic roles and independence have not been established.");

        return new PapStructure(sourceIdentity, sections, details, discoveredTopology, evidence);
    }

    private static T ValueAt<T>(IReadOnlyList<T> values, int index, T fallback)
        => index >= 0 && index < values.Count ? values[index] : fallback;
}

public enum PapStructuralPlanStatus
{
    Compatible,
    Unsupported,
    Unknown,
}

/// <summary>
/// A data-only result. It has no file path for output and never invokes a PAP writer.
/// </summary>
public sealed record PapStructuralOverridePlan(
    PapStructuralPlanStatus Status,
    string Reason,
    PapAnimationSection? SourceMotion,
    PapAnimationSection? TargetReplacementSection,
    IReadOnlyList<PapAnimationSection> PreservedTargetSections,
    PapTopology ResultingTopology,
    IReadOnlyList<string> Evidence)
{
    public bool IsDryRun => true;
    public bool IsCompatible => Status == PapStructuralPlanStatus.Compatible;
}

/// <summary>
/// Fails closed while the relationship between target sections is not proven.
/// This planner intentionally has no dependency on PapEditor or file I/O.
/// </summary>
public static class PapStructuralPlanner
{
    public static PapStructuralOverridePlan Plan(
        PapStructure source,
        PapStructure target,
        int? targetReplacementSectionIndex = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (source.Topology != PapTopology.SingleSection || source.Sections.Count != 1)
            return Unsupported("The source is not a verified single-section complete motion.", target.Topology);

        if (target.Topology == PapTopology.MultiSectionCoupled)
            return Unsupported("Target sections are interdependent; no isolated replacement is safe.", target.Topology);
        if (target.Topology == PapTopology.Unknown)
            return Unsupported("Target multi-section topology is not understood; no replacement section can be selected safely.", target.Topology);

        var replacementIndex = target.Topology == PapTopology.SingleSection
            ? 0
            : targetReplacementSectionIndex;
        if (replacementIndex is null)
            return Unsupported("A MultiSectionIndependent target still requires an evidenced replacement section.", target.Topology);

        var replacement = target.Sections.SingleOrDefault(section => section.AnimationIndex == replacementIndex.Value);
        if (replacement is null)
            return Unsupported("The requested target replacement section does not exist.", target.Topology);

        var preserved = target.Sections
            .Where(section => section.AnimationIndex != replacement.AnimationIndex)
            .ToList();
        return new PapStructuralOverridePlan(
            PapStructuralPlanStatus.Compatible,
            "Dry-run only: the source complete motion could be mapped to the evidenced target section without selecting an untouched section.",
            source.Sections[0],
            replacement,
            preserved,
            target.Topology,
            new[]
            {
                "The plan performs no PAP I/O and invokes no writer.",
                "A future writer must preserve every listed target section and target-native timeline semantics.",
            });
    }

    private static PapStructuralOverridePlan Unsupported(string reason, PapTopology topology)
        => new(
            PapStructuralPlanStatus.Unsupported,
            reason,
            null,
            null,
            Array.Empty<PapAnimationSection>(),
            topology,
            new[] { "The planner performs no PAP I/O and invokes no writer." });
}
