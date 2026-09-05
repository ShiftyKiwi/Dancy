using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Dancy.Animation;

public enum AnimationSemanticClassification
{
    SemanticallyEquivalent,
    MinorReconstructionDrift,
    MaterialReconstructionDrift,
    UnsafeForExperiment,
}

public sealed class AnimationSemanticTolerance
{
    // The strict band is tighter than a visible pose change. The minor band
    // allows ordinary float and 30 fps reconstruction noise, not a new pose.
    public float StrictTranslation { get; init; } = 0.0005f;
    public float StrictRotationDegrees { get; init; } = 0.1f;
    public float StrictScale { get; init; } = 0.0005f;
    public float MinorTranslation { get; init; } = 0.005f;
    public float MinorRotationDegrees { get; init; } = 0.5f;
    public float MinorScale { get; init; } = 0.005f;
    public float DurationSeconds { get; init; } = 0.001f;
}

public sealed class AnimationTransformSample
{
    public int BindingIndex { get; init; }
    public int TrackIndex { get; init; }
    public int BoneIndex { get; init; }
    public string BoneName { get; init; } = string.Empty;
    public float TimeSeconds { get; init; }
    public AnimationTransform Transform { get; init; } = new();
}

public sealed class AnimationTransformSampleSet
{
    public string PapPath { get; init; } = string.Empty;
    public IReadOnlyList<AnimationBindingInformation> Bindings { get; init; } = Array.Empty<AnimationBindingInformation>();
    public IReadOnlyList<AnimationTransformSample> Samples { get; init; } = Array.Empty<AnimationTransformSample>();
}

public sealed class AnimationSemanticDeviation
{
    public int BindingIndex { get; init; }
    public string BoneName { get; init; } = string.Empty;
    public float TimeSeconds { get; init; }
    public float Value { get; init; }
}

public sealed class AnimationSemanticComparisonResult
{
    public AnimationSemanticClassification Classification { get; init; }
    public int SampleTimeCount { get; init; }
    public int SourceTrackCount { get; init; }
    public int TargetTrackCount { get; init; }
    public float MaximumTranslationDeviation { get; init; }
    public float MaximumRotationDeviationDegrees { get; init; }
    public float MaximumScaleDeviation { get; init; }
    public AnimationSemanticDeviation? MaximumTranslation { get; init; }
    public AnimationSemanticDeviation? MaximumRotation { get; init; }
    public AnimationSemanticDeviation? MaximumScale { get; init; }
    public bool FacialOrHeadTracksChangedMaterially { get; init; }
    public IReadOnlyList<string> ExpectedExcludedTracks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedTracksFoundInSource { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedTracksPresentInTarget { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingRetainedTracks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UnexpectedTargetTracks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Compares sampled local transforms by binding and bone name. Track ordinals
/// are deliberately not identity: VFXEditor's importer is allowed to rebuild
/// the binding order when the requested excluded tracks are omitted.
/// </summary>
public static class AnimationSemanticComparer
{
    public static IReadOnlyList<float> CreateThirtyFpsSampleTimes(float durationSeconds, int maximumSamples = 1201)
    {
        if (!float.IsFinite(durationSeconds) || durationSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "A finite, non-negative duration is required.");
        if (maximumSamples < 2)
            throw new ArgumentOutOfRangeException(nameof(maximumSamples));
        if (durationSeconds <= 0f)
            return [0f];

        var frames = Math.Max(1, (int)MathF.Round(durationSeconds * 30f));
        var samples = Math.Min(frames + 1, maximumSamples);
        var result = new float[samples];
        for (var index = 0; index < samples; index++)
            result[index] = durationSeconds * index / (samples - 1);
        return result;
    }

    public static AnimationSemanticComparisonResult Compare(
        AnimationTransformSampleSet source,
        AnimationTransformSampleSet target,
        IEnumerable<string> excludedBoneNames,
        AnimationSemanticTolerance? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(excludedBoneNames);
        tolerance ??= new AnimationSemanticTolerance();
        ValidateTolerance(tolerance);

        var excluded = excludedBoneNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourceTracks = GroupTracks(source.Samples, "source");
        var targetTracks = GroupTracks(target.Samples, "target");
        var sourceKeys = sourceTracks.Keys.ToHashSet(TrackKeyComparer.Instance);
        var targetKeys = targetTracks.Keys.ToHashSet(TrackKeyComparer.Instance);
        var excludedTracksFound = sourceKeys
            .Where(key => excluded.Contains(key.BoneName, StringComparer.OrdinalIgnoreCase))
            .Select(Describe)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedTarget = targetKeys
            .Where(key => excluded.Contains(key.BoneName, StringComparer.OrdinalIgnoreCase))
            .Select(Describe)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedTarget = sourceKeys
            .Where(key => !excluded.Contains(key.BoneName, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(TrackKeyComparer.Instance);
        var missing = expectedTarget.Except(targetKeys, TrackKeyComparer.Instance).Select(Describe).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var unexpected = targetKeys.Except(expectedTarget, TrackKeyComparer.Instance).Select(Describe).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var warnings = new List<string>();

        if (!BindingDurationsMatch(source.Bindings, target.Bindings, tolerance.DurationSeconds))
            warnings.Add($"Binding durations differ by more than {tolerance.DurationSeconds:0.####} seconds.");
        if (missing.Length > 0)
            warnings.Add($"{missing.Length} retained source track(s) are missing from the rebuilt binding.");
        if (unexpected.Length > 0)
            warnings.Add($"{unexpected.Length} unexpected track(s) are present in the rebuilt binding.");
        if (excludedTarget.Length > 0)
            warnings.Add($"{excludedTarget.Length} requested excluded track(s) remain in the rebuilt binding.");
        if (excludedTracksFound.Length != excluded.Length)
            warnings.Add("At least one requested excluded bone was not mapped by the source binding.");

        AnimationSemanticDeviation? maximumTranslation = null;
        AnimationSemanticDeviation? maximumRotation = null;
        AnimationSemanticDeviation? maximumScale = null;
        var facialMaterialChange = false;
        var samplesPerTrack = 0;
        var structurallyUnsafe = warnings.Count > 0;

        foreach (var key in expectedTarget.OrderBy(key => key.BindingIndex).ThenBy(key => key.BoneName, StringComparer.OrdinalIgnoreCase))
        {
            if (!sourceTracks.TryGetValue(key, out var sourceSamples) || !targetTracks.TryGetValue(key, out var targetSamples))
                continue;
            if (sourceSamples.Count != targetSamples.Count)
            {
                warnings.Add($"{Describe(key)} has {sourceSamples.Count} source sample(s) and {targetSamples.Count} target sample(s).");
                structurallyUnsafe = true;
                continue;
            }

            samplesPerTrack = Math.Max(samplesPerTrack, sourceSamples.Count);
            for (var index = 0; index < sourceSamples.Count; index++)
            {
                var left = sourceSamples[index];
                var right = targetSamples[index];
                if (MathF.Abs(left.TimeSeconds - right.TimeSeconds) > 0.00001f)
                {
                    warnings.Add($"{Describe(key)} was sampled at different times in source and target.");
                    structurallyUnsafe = true;
                    continue;
                }

                var delta = AnimationTransformMath.CalculateDelta(right.Transform, left.Transform);
                maximumTranslation = Max(maximumTranslation, new AnimationSemanticDeviation
                {
                    BindingIndex = key.BindingIndex,
                    BoneName = key.BoneName,
                    TimeSeconds = left.TimeSeconds,
                    Value = delta.TranslationMagnitude,
                });
                maximumRotation = Max(maximumRotation, new AnimationSemanticDeviation
                {
                    BindingIndex = key.BindingIndex,
                    BoneName = key.BoneName,
                    TimeSeconds = left.TimeSeconds,
                    Value = delta.RotationDeltaDegrees,
                });
                maximumScale = Max(maximumScale, new AnimationSemanticDeviation
                {
                    BindingIndex = key.BindingIndex,
                    BoneName = key.BoneName,
                    TimeSeconds = left.TimeSeconds,
                    Value = delta.ScaleMagnitude,
                });

                if (IsFacialOrHeadBone(key.BoneName) && IsMaterial(delta, tolerance))
                    facialMaterialChange = true;
            }
        }

        var classification = structurallyUnsafe
            ? AnimationSemanticClassification.UnsafeForExperiment
            : IsStrict(maximumTranslation?.Value ?? 0f, maximumRotation?.Value ?? 0f, maximumScale?.Value ?? 0f, tolerance)
                ? AnimationSemanticClassification.SemanticallyEquivalent
                : IsMinor(maximumTranslation?.Value ?? 0f, maximumRotation?.Value ?? 0f, maximumScale?.Value ?? 0f, tolerance)
                    ? AnimationSemanticClassification.MinorReconstructionDrift
                    : AnimationSemanticClassification.MaterialReconstructionDrift;

        return new AnimationSemanticComparisonResult
        {
            Classification = classification,
            SampleTimeCount = samplesPerTrack,
            SourceTrackCount = sourceKeys.Count,
            TargetTrackCount = targetKeys.Count,
            MaximumTranslationDeviation = maximumTranslation?.Value ?? 0f,
            MaximumRotationDeviationDegrees = maximumRotation?.Value ?? 0f,
            MaximumScaleDeviation = maximumScale?.Value ?? 0f,
            MaximumTranslation = maximumTranslation,
            MaximumRotation = maximumRotation,
            MaximumScale = maximumScale,
            FacialOrHeadTracksChangedMaterially = facialMaterialChange,
            ExpectedExcludedTracks = excluded,
            ExcludedTracksFoundInSource = excludedTracksFound,
            ExcludedTracksPresentInTarget = excludedTarget,
            MissingRetainedTracks = missing,
            UnexpectedTargetTracks = unexpected,
            Warnings = warnings,
        };
    }

    private static Dictionary<TrackKey, List<AnimationTransformSample>> GroupTracks(IEnumerable<AnimationTransformSample> samples, string label)
    {
        var result = new Dictionary<TrackKey, List<AnimationTransformSample>>(TrackKeyComparer.Instance);
        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.BoneName))
                throw new InvalidDataException($"The {label} sample set contains an unnamed transform track.");
            var key = new TrackKey(sample.BindingIndex, sample.BoneName);
            if (!result.TryGetValue(key, out var values))
                result[key] = values = [];
            values.Add(sample);
        }
        foreach (var values in result.Values)
            values.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        return result;
    }

    private static AnimationSemanticDeviation? Max(AnimationSemanticDeviation? current, AnimationSemanticDeviation candidate)
        => current is null || candidate.Value > current.Value ? candidate : current;

    private static bool BindingDurationsMatch(IReadOnlyList<AnimationBindingInformation> source, IReadOnlyList<AnimationBindingInformation> target, float tolerance)
    {
        if (source.Count != target.Count)
            return false;
        var sourceDurations = source.OrderBy(binding => binding.BindingIndex).ToArray();
        var targetDurations = target.OrderBy(binding => binding.BindingIndex).ToArray();
        return sourceDurations.Zip(targetDurations).All(pair => pair.First.BindingIndex == pair.Second.BindingIndex
                                                         && MathF.Abs(pair.First.DurationSeconds - pair.Second.DurationSeconds) <= tolerance);
    }

    private static bool IsStrict(float translation, float rotation, float scale, AnimationSemanticTolerance tolerance)
        => translation <= tolerance.StrictTranslation
           && rotation <= tolerance.StrictRotationDegrees
           && scale <= tolerance.StrictScale;

    private static bool IsMinor(float translation, float rotation, float scale, AnimationSemanticTolerance tolerance)
        => translation <= tolerance.MinorTranslation
           && rotation <= tolerance.MinorRotationDegrees
           && scale <= tolerance.MinorScale;

    private static bool IsMaterial(AnimationTransformDelta delta, AnimationSemanticTolerance tolerance)
        => delta.TranslationMagnitude > tolerance.MinorTranslation
           || delta.RotationDeltaDegrees > tolerance.MinorRotationDegrees
           || delta.ScaleMagnitude > tolerance.MinorScale;

    private static bool IsFacialOrHeadBone(string name)
        => name.Contains("kao", StringComparison.OrdinalIgnoreCase)
           || name.Contains("ago", StringComparison.OrdinalIgnoreCase)
           || name.Contains("kubi", StringComparison.OrdinalIgnoreCase)
           || name.Contains("kami", StringComparison.OrdinalIgnoreCase)
           || name.Contains("mimi", StringComparison.OrdinalIgnoreCase)
           || name.Contains("ear", StringComparison.OrdinalIgnoreCase)
           || name.Contains("face", StringComparison.OrdinalIgnoreCase)
           || name.Contains("head", StringComparison.OrdinalIgnoreCase)
           || name.Contains("jaw", StringComparison.OrdinalIgnoreCase);

    private static void ValidateTolerance(AnimationSemanticTolerance tolerance)
    {
        if (tolerance.StrictTranslation < 0 || tolerance.StrictRotationDegrees < 0 || tolerance.StrictScale < 0
            || tolerance.MinorTranslation < tolerance.StrictTranslation
            || tolerance.MinorRotationDegrees < tolerance.StrictRotationDegrees
            || tolerance.MinorScale < tolerance.StrictScale
            || tolerance.DurationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }
    }

    private static string Describe(TrackKey key) => $"binding {key.BindingIndex}: {key.BoneName}";

    private readonly record struct TrackKey(int BindingIndex, string BoneName);

    private sealed class TrackKeyComparer : IEqualityComparer<TrackKey>
    {
        public static TrackKeyComparer Instance { get; } = new();

        public bool Equals(TrackKey left, TrackKey right)
            => left.BindingIndex == right.BindingIndex
               && string.Equals(left.BoneName, right.BoneName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(TrackKey value)
            => HashCode.Combine(value.BindingIndex, StringComparer.OrdinalIgnoreCase.GetHashCode(value.BoneName));
    }
}
