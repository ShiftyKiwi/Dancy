using System;
using System.Collections.Generic;
using System.Numerics;

namespace Dancy.Animation;

/// <summary>
/// Pure math used by the read-only compatibility inspector. The tolerances are
/// deliberately tighter than visible pose differences while allowing ordinary
/// floating point load/sample noise.
/// </summary>
public static class AnimationTransformMath
{
    public const float TranslationTolerance = 0.0001f;
    public const float ScaleTolerance = 0.0001f;
    public const float RotationToleranceDegrees = 0.1f;

    public static AnimationTransform FromBoneReferencePose(BoneDefinition bone)
        => FromNumerics(bone.RestTranslation, bone.RestRotation, bone.RestScale);

    public static AnimationTransform FromNumerics(Vector3 translation, Quaternion rotation, Vector3 scale)
        => new()
        {
            TranslationX = translation.X,
            TranslationY = translation.Y,
            TranslationZ = translation.Z,
            RotationX = rotation.X,
            RotationY = rotation.Y,
            RotationZ = rotation.Z,
            RotationW = rotation.W,
            ScaleX = scale.X,
            ScaleY = scale.Y,
            ScaleZ = scale.Z,
        };

    public static AnimationTransformDelta CalculateDelta(AnimationTransform animation, AnimationTransform reference)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(reference);

        var translationDelta = ToTranslation(animation) - ToTranslation(reference);
        var scaleDelta = ToScale(animation) - ToScale(reference);
        var rotationDeltaDegrees = CalculateRotationDeltaDegrees(ToRotation(animation), ToRotation(reference));
        var translationMagnitude = translationDelta.Length();
        var scaleMagnitude = scaleDelta.Length();
        return new AnimationTransformDelta
        {
            TranslationDeltaX = translationDelta.X,
            TranslationDeltaY = translationDelta.Y,
            TranslationDeltaZ = translationDelta.Z,
            TranslationMagnitude = translationMagnitude,
            RotationDeltaDegrees = rotationDeltaDegrees,
            ScaleDeltaX = scaleDelta.X,
            ScaleDeltaY = scaleDelta.Y,
            ScaleDeltaZ = scaleDelta.Z,
            ScaleMagnitude = scaleMagnitude,
            EqualsReferencePoseWithinTolerance = translationMagnitude <= TranslationTolerance
                                                && scaleMagnitude <= ScaleTolerance
                                                && rotationDeltaDegrees <= RotationToleranceDegrees,
        };
    }

    public static float CalculateRotationDeltaDegrees(Quaternion left, Quaternion right)
    {
        var normalizedLeft = NormalizeQuaternion(left);
        var normalizedRight = NormalizeQuaternion(right);
        var dot = Math.Clamp(MathF.Abs(Quaternion.Dot(normalizedLeft, normalizedRight)), 0f, 1f);
        return 2f * MathF.Acos(dot) * (180f / MathF.PI);
    }

    /// <summary>
    /// Resolves a bone's model transform with local animation values where they
    /// exist and reference-pose values everywhere else. This characterizes
    /// time-zero propagation separately from activity sampling.
    /// </summary>
    public static AnimationTransform CalculateModelTransform(
        SkeletonDefinition skeleton,
        int boneIndex,
        IReadOnlyDictionary<int, AnimationTransform> localTransforms)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(localTransforms);
        return CalculateModelTransform(skeleton, boneIndex, localTransforms, new HashSet<int>());
    }

    private static AnimationTransform CalculateModelTransform(
        SkeletonDefinition skeleton,
        int boneIndex,
        IReadOnlyDictionary<int, AnimationTransform> localTransforms,
        HashSet<int> visiting)
    {
        var bone = skeleton.FindBone(boneIndex)
            ?? throw new ArgumentException($"Skeleton {skeleton.Identity} does not contain bone index {boneIndex}.", nameof(boneIndex));
        if (!visiting.Add(boneIndex))
            throw new ArgumentException($"Skeleton {skeleton.Identity} contains a parent cycle at bone {boneIndex}.", nameof(skeleton));

        try
        {
            var local = localTransforms.TryGetValue(boneIndex, out var sampled)
                ? sampled
                : FromBoneReferencePose(bone);
            if (bone.ParentIndex < 0)
                return local;

            var parent = CalculateModelTransform(skeleton, bone.ParentIndex, localTransforms, visiting);
            var matrix = ToMatrix(local) * ToMatrix(parent);
            if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation))
                throw new ArgumentException($"The accumulated transform for {bone.Name} cannot be represented as TRS.", nameof(skeleton));
            return FromNumerics(translation, NormalizeQuaternion(rotation), scale);
        }
        finally
        {
            visiting.Remove(boneIndex);
        }
    }

    private static Vector3 ToTranslation(AnimationTransform transform)
        => new(transform.TranslationX, transform.TranslationY, transform.TranslationZ);

    private static Vector3 ToScale(AnimationTransform transform)
        => new(transform.ScaleX, transform.ScaleY, transform.ScaleZ);

    private static Quaternion ToRotation(AnimationTransform transform)
        => new(transform.RotationX, transform.RotationY, transform.RotationZ, transform.RotationW);

    private static Matrix4x4 ToMatrix(AnimationTransform transform)
        => Matrix4x4.CreateScale(ToScale(transform))
           * Matrix4x4.CreateFromQuaternion(NormalizeQuaternion(ToRotation(transform)))
           * Matrix4x4.CreateTranslation(ToTranslation(transform));

    private static Quaternion NormalizeQuaternion(Quaternion quaternion)
    {
        if (!float.IsFinite(quaternion.X) || !float.IsFinite(quaternion.Y)
            || !float.IsFinite(quaternion.Z) || !float.IsFinite(quaternion.W)
            || quaternion.LengthSquared() <= float.Epsilon)
        {
            throw new ArgumentException("A finite, non-zero quaternion is required for compatibility analysis.", nameof(quaternion));
        }

        return Quaternion.Normalize(quaternion);
    }
}
