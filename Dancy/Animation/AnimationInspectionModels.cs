using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Dancy.Animation;

/// <summary>
/// A source-agnostic skeleton snapshot. Names, not ordinal indices, are the
/// semantic identity used when comparing independently-authored skeletons.
/// </summary>
public sealed class SkeletonDefinition
{
    public string Identity { get; init; } = string.Empty;
    public IReadOnlyList<BoneDefinition> Bones { get; init; } = Array.Empty<BoneDefinition>();

    public BoneDefinition? FindBone(string name)
        => Bones.FirstOrDefault(bone => string.Equals(bone.Name, name, StringComparison.OrdinalIgnoreCase));

    public BoneDefinition? FindBone(int index)
        => Bones.FirstOrDefault(bone => bone.Index == index);
}

public sealed class BoneDefinition
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public int ParentIndex { get; init; } = -1;
    public IReadOnlyList<int> Children { get; init; } = Array.Empty<int>();
    public Vector3 RestTranslation { get; init; }
    public Quaternion RestRotation { get; init; } = Quaternion.Identity;
    public Vector3 RestScale { get; init; } = Vector3.One;
}

public sealed class AnimationBindingInformation
{
    public int BindingIndex { get; init; }
    public string OriginalSkeletonName { get; init; } = string.Empty;
    public float DurationSeconds { get; init; }
    public int DeclaredTransformTrackCount { get; init; }
    public int BoundTransformTrackCount { get; init; }
}

public enum AnimationTransformActivity
{
    Unknown,
    Constant,
    Changing,
}

/// <summary>
/// A local TRS transform represented with scalar fields so diagnostics remain
/// deterministic and easy to serialize across the debug bridge.
/// </summary>
public sealed class AnimationTransform
{
    public float TranslationX { get; init; }
    public float TranslationY { get; init; }
    public float TranslationZ { get; init; }
    public float RotationX { get; init; }
    public float RotationY { get; init; }
    public float RotationZ { get; init; }
    public float RotationW { get; init; } = 1f;
    public float ScaleX { get; init; } = 1f;
    public float ScaleY { get; init; } = 1f;
    public float ScaleZ { get; init; } = 1f;
}

/// <summary>
/// The animation-local pose relative to the selected skeleton reference pose.
/// Rotation is reported as the shortest quaternion angle to avoid q/-q noise.
/// </summary>
public sealed class AnimationTransformDelta
{
    public float TranslationDeltaX { get; init; }
    public float TranslationDeltaY { get; init; }
    public float TranslationDeltaZ { get; init; }
    public float TranslationMagnitude { get; init; }
    public float RotationDeltaDegrees { get; init; }
    public float ScaleDeltaX { get; init; }
    public float ScaleDeltaY { get; init; }
    public float ScaleDeltaZ { get; init; }
    public float ScaleMagnitude { get; init; }
    public bool EqualsReferencePoseWithinTolerance { get; init; }
}

public sealed class AnimationTrackDefinition
{
    public int BindingIndex { get; init; }
    public int TrackIndex { get; init; }
    public int BoneIndex { get; init; } = -1;
    public string? BoneName { get; init; }
    public string? ParentBoneName { get; init; }
    public IReadOnlyList<string> ChildBoneNames { get; init; } = Array.Empty<string>();
    public AnimationTransformActivity Translation { get; init; }
    public AnimationTransformActivity Rotation { get; init; }
    public AnimationTransformActivity Scale { get; init; }
    /// <summary>Sampled local transform at time zero, if native sampling succeeded.</summary>
    public AnimationTransform? SampledLocalTransform { get; init; }
    public string SkeletonSource { get; init; } = string.Empty;
}

public sealed class AnimationInspectionResult
{
    public string PapPath { get; init; } = string.Empty;
    public int AnimationCount { get; init; }
    public IReadOnlyList<AnimationBindingInformation> Bindings { get; init; } = Array.Empty<AnimationBindingInformation>();
    public IReadOnlyList<AnimationTrackDefinition> Tracks { get; init; } = Array.Empty<AnimationTrackDefinition>();
    public IReadOnlyList<AnimationTrackDefinition> UnresolvedTracks { get; init; } = Array.Empty<AnimationTrackDefinition>();
    public SkeletonDefinition? Skeleton { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public enum AnimationCompatibilityCategory
{
    ExactBoneMatch,
    SourceBoneMissingOnTarget,
    TargetBoneMissingFromSource,
    HierarchyMismatch,
    RestTransformDifference,
    SuspiciousIndexCorrespondence,
    CustomBone,
    Unresolved,
    PotentiallyCompatible,
    PotentiallyIncompatible,
}

public sealed class AnimationTrackCompatibility
{
    public AnimationTrackDefinition SourceTrack { get; init; } = new();
    public BoneDefinition? SourceBone { get; init; }
    public BoneDefinition? TargetBone { get; init; }
    public IReadOnlyList<AnimationCompatibilityCategory> Categories { get; init; } = Array.Empty<AnimationCompatibilityCategory>();
    public float RestTranslationDifference { get; init; }
    public float RestRotationDifferenceDegrees { get; init; }
    public float RestScaleDifference { get; init; }
}

public sealed class AnimationCompatibilityResult
{
    public SkeletonDefinition SourceSkeleton { get; init; } = new();
    public SkeletonDefinition TargetSkeleton { get; init; } = new();
    public IReadOnlyList<AnimationTrackCompatibility> Tracks { get; init; } = Array.Empty<AnimationTrackCompatibility>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
