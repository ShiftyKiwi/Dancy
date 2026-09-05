using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dancy.Animation;
using Xunit;

namespace Dancy.Tests;

public class AnimationSemanticComparisonTests
{
    [Fact]
    public void ClassifiesExactRequestedTrackRemovalAsSemanticallyEquivalent()
    {
        var source = SampleSet(
            Sample("n_hara", 0f),
            Sample("n_hara", 1f),
            Sample("iv_ochinko_a", 0f),
            Sample("iv_ochinko_a", 1f));
        var target = SampleSet(Sample("n_hara", 0f), Sample("n_hara", 1f));

        var result = AnimationSemanticComparer.Compare(source, target, ["iv_ochinko_a"]);

        Assert.Equal(AnimationSemanticClassification.SemanticallyEquivalent, result.Classification);
        Assert.Equal(["binding 0: iv_ochinko_a"], result.ExcludedTracksFoundInSource);
        Assert.Empty(result.ExcludedTracksPresentInTarget);
        Assert.Empty(result.MissingRetainedTracks);
        Assert.Empty(result.UnexpectedTargetTracks);
    }

    [Fact]
    public void ReportsMaterialFacialRotationDriftWithTheResponsibleTrack()
    {
        var source = SampleSet(Sample("j_kao", 0f));
        var target = SampleSet(Sample("j_kao", 0f, rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f)));

        var result = AnimationSemanticComparer.Compare(source, target, Array.Empty<string>());

        Assert.Equal(AnimationSemanticClassification.MaterialReconstructionDrift, result.Classification);
        Assert.True(result.FacialOrHeadTracksChangedMaterially);
        Assert.Equal("j_kao", result.MaximumRotation!.BoneName);
        Assert.InRange(result.MaximumRotationDeviationDegrees, 89.9f, 90.1f);
    }

    [Fact]
    public void RejectsUnexpectedOrMissingUnrelatedTracks()
    {
        var source = SampleSet(Sample("n_hara", 0f), Sample("j_kosi", 0f));
        var target = SampleSet(Sample("n_hara", 0f), Sample("j_ago", 0f));

        var result = AnimationSemanticComparer.Compare(source, target, Array.Empty<string>());

        Assert.Equal(AnimationSemanticClassification.UnsafeForExperiment, result.Classification);
        Assert.Equal(["binding 0: j_kosi"], result.MissingRetainedTracks);
        Assert.Equal(["binding 0: j_ago"], result.UnexpectedTargetTracks);
    }

    [Fact]
    public void ProducesEveryThirtyFpsFrameForSmokingLengthClips()
    {
        var times = AnimationSemanticComparer.CreateThirtyFpsSampleTimes(31.666666f);

        Assert.Equal(951, times.Count);
        Assert.Equal(0f, times[0]);
        Assert.Equal(31.666666f, times[^1], 4);
    }

    private static AnimationTransformSampleSet SampleSet(params AnimationTransformSample[] samples)
        => new()
        {
            Bindings = [new AnimationBindingInformation { BindingIndex = 0, DurationSeconds = 1f, DeclaredTransformTrackCount = samples.Length, BoundTransformTrackCount = samples.Length }],
            Samples = samples,
        };

    private static AnimationTransformSample Sample(string bone, float time, Vector3? translation = null, Quaternion? rotation = null, Vector3? scale = null)
        => new()
        {
            BindingIndex = 0,
            TrackIndex = 0,
            BoneIndex = 0,
            BoneName = bone,
            TimeSeconds = time,
            Transform = AnimationTransformMath.FromNumerics(translation ?? Vector3.Zero, rotation ?? Quaternion.Identity, scale ?? Vector3.One),
        };
}
