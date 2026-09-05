using System;
using System.Collections.Generic;
using System.Linq;

namespace Dancy.Animation;

public enum BoneMaskResolutionState
{
    FoundAndAnimated,
    FoundNotAnimated,
    MissingFromTargetSkeleton,
    MissingFromBinding,
    Ambiguous,
}

/// <summary>
/// A name-only skeleton view supplied by the currently selected target.
/// Parent names are retained for a future descendant option; MVP planning does
/// not inspect or include them.
/// </summary>
public sealed class BoneMaskTargetSkeleton
{
    public string Identity { get; init; } = string.Empty;
    public IReadOnlyList<BoneMaskSkeletonBone> Bones { get; init; } = Array.Empty<BoneMaskSkeletonBone>();
}

public sealed class BoneMaskSkeletonBone
{
    public int Index { get; init; } = -1;
    public string Name { get; init; } = string.Empty;
    public string? ParentName { get; init; }
}

/// <summary>
/// A transform mapping from the selected source PAP/HKX binding after its bone
/// name has been resolved through the selected target skeleton.
/// </summary>
public sealed class BoneMaskBindingTrack
{
    public int BindingIndex { get; init; }
    public int TrackIndex { get; init; }
    public int BoneIndex { get; init; } = -1;
    public string BoneName { get; init; } = string.Empty;
    public bool IsAnimated { get; init; }
}

public sealed class BoneMaskResolutionRequest
{
    public BoneMaskTargetSkeleton TargetSkeleton { get; init; } = new();
    public IReadOnlyList<BoneMaskBindingTrack> BindingTracks { get; init; } = Array.Empty<BoneMaskBindingTrack>();
    public IReadOnlyList<string> RequestedBoneNames { get; init; } = Array.Empty<string>();
}

public sealed class BoneMaskBoneResolution
{
    public string RequestedBoneName { get; init; } = string.Empty;
    public BoneMaskResolutionState State { get; init; }
    public IReadOnlyList<BoneMaskBindingTrack> MatchingTracks { get; init; } = Array.Empty<BoneMaskBindingTrack>();
    public string Message { get; init; } = string.Empty;
}

public sealed class BoneMaskExclusionPlan
{
    public IReadOnlyList<string> RequestedBoneNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<BoneMaskBoneResolution> Resolutions { get; init; } = Array.Empty<BoneMaskBoneResolution>();
    public IReadOnlyList<BoneMaskBindingTrack> ExcludedTracks { get; init; } = Array.Empty<BoneMaskBindingTrack>();

    public bool HasBlockingResolution => Resolutions.Any(result => result.State is BoneMaskResolutionState.MissingFromTargetSkeleton
        or BoneMaskResolutionState.MissingFromBinding
        or BoneMaskResolutionState.Ambiguous);
}

public static class BoneMaskResolver
{
    public static BoneMaskExclusionPlan Resolve(BoneMaskResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.TargetSkeleton);
        ArgumentNullException.ThrowIfNull(request.TargetSkeleton.Bones);
        ArgumentNullException.ThrowIfNull(request.BindingTracks);

        var requested = BoneMaskNames.Normalize(request.RequestedBoneNames);
        var skeletonNames = request.TargetSkeleton.Bones
            .Where(bone => !string.IsNullOrWhiteSpace(bone.Name))
            .GroupBy(bone => bone.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var tracksByName = request.BindingTracks
            .Where(track => !string.IsNullOrWhiteSpace(track.BoneName))
            .GroupBy(track => track.BoneName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var results = new List<BoneMaskBoneResolution>(requested.Count);
        var exclusions = new List<BoneMaskBindingTrack>();

        foreach (var name in requested)
        {
            if (!skeletonNames.TryGetValue(name, out var skeletonMatches) || skeletonMatches.Length == 0)
            {
                results.Add(Result(name, BoneMaskResolutionState.MissingFromTargetSkeleton, [], "The selected target skeleton does not contain this named bone."));
                continue;
            }

            if (skeletonMatches.Length != 1)
            {
                results.Add(Result(name, BoneMaskResolutionState.Ambiguous, [], "The selected target skeleton contains this bone name more than once."));
                continue;
            }

            if (!tracksByName.TryGetValue(name, out var trackMatches) || trackMatches.Length == 0)
            {
                results.Add(Result(name, BoneMaskResolutionState.MissingFromBinding, [], "The selected PAP binding has no transform track for this bone."));
                continue;
            }

            if (trackMatches.Length != 1)
            {
                results.Add(Result(name, BoneMaskResolutionState.Ambiguous, trackMatches, "The selected PAP binding maps this bone name more than once."));
                continue;
            }

            var track = trackMatches[0];
            if (!track.IsAnimated)
            {
                results.Add(Result(name, BoneMaskResolutionState.FoundNotAnimated, trackMatches, "The selected PAP binding maps this bone, but the track has no animated channels."));
                continue;
            }

            results.Add(Result(name, BoneMaskResolutionState.FoundAndAnimated, trackMatches, "The named bone resolved to one animated transform track."));
            exclusions.Add(track);
        }

        return new BoneMaskExclusionPlan
        {
            RequestedBoneNames = requested,
            Resolutions = results,
            ExcludedTracks = exclusions
                .OrderBy(track => track.BindingIndex)
                .ThenBy(track => track.TrackIndex)
                .ToArray(),
        };
    }

    private static BoneMaskBoneResolution Result(string name, BoneMaskResolutionState state, IReadOnlyList<BoneMaskBindingTrack> tracks, string message)
        => new()
        {
            RequestedBoneName = name,
            State = state,
            MatchingTracks = tracks,
            Message = message,
        };
}
