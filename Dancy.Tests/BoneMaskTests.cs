using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dancy.Animation;
using Xunit;

namespace Dancy.Tests;

public class BoneMaskTests
{
    [Fact]
    public void NormalizesDuplicateNamesWithoutChangingTheUserVisibleAssociation()
    {
        var names = BoneMaskNames.Normalize([" iv_ochinko_a ", "IV_OCHINKO_A", "iv_kougan_l", " "]);
        var association = new BoneMaskAnimationAssociation
        {
            SourceOverrideGamePath = "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap",
            ReplacementGamePath = "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap",
            MotionIdentity = "idle",
        };

        Assert.Equal(["iv_kougan_l", "iv_ochinko_a"], names);
        Assert.Equal("idle", association.MotionIdentity);
    }

    [Fact]
    public void ResolvesOnlyTheExplicitlySelectedAnimatedBone()
    {
        var plan = BoneMaskResolver.Resolve(new BoneMaskResolutionRequest
        {
            TargetSkeleton = Skeleton(("n_root", null), ("n_child", "n_root")),
            BindingTracks = [Track("n_root", 0, true), Track("n_child", 1, true)],
            RequestedBoneNames = ["n_root"],
        });

        Assert.False(plan.HasBlockingResolution);
        Assert.Equal(BoneMaskResolutionState.FoundAndAnimated, Assert.Single(plan.Resolutions).State);
        Assert.Equal(["n_root"], plan.ExcludedTracks.Select(track => track.BoneName));
        Assert.DoesNotContain(plan.ExcludedTracks, track => track.BoneName == "n_child");
    }

    [Fact]
    public void ClassifiesMissingTargetMissingBindingAndUnanimatedSelections()
    {
        var plan = BoneMaskResolver.Resolve(new BoneMaskResolutionRequest
        {
            TargetSkeleton = Skeleton(("animated", null), ("constant", null), ("unbound", null)),
            BindingTracks = [Track("animated", 0, true), Track("constant", 1, false)],
            RequestedBoneNames = ["animated", "constant", "unbound", "not_on_target"],
        });

        Assert.Equal(BoneMaskResolutionState.FoundAndAnimated, State(plan, "animated"));
        Assert.Equal(BoneMaskResolutionState.FoundNotAnimated, State(plan, "constant"));
        Assert.Equal(BoneMaskResolutionState.MissingFromBinding, State(plan, "unbound"));
        Assert.Equal(BoneMaskResolutionState.MissingFromTargetSkeleton, State(plan, "not_on_target"));
        Assert.True(plan.HasBlockingResolution);
        Assert.Equal(["animated"], plan.ExcludedTracks.Select(track => track.BoneName));
    }

    [Fact]
    public void RejectsAmbiguousSkeletonAndBindingNames()
    {
        var duplicateSkeleton = BoneMaskResolver.Resolve(new BoneMaskResolutionRequest
        {
            TargetSkeleton = Skeleton(("n_dup", null), ("n_dup", null)),
            BindingTracks = [Track("n_dup", 0, true)],
            RequestedBoneNames = ["n_dup"],
        });
        var duplicateBinding = BoneMaskResolver.Resolve(new BoneMaskResolutionRequest
        {
            TargetSkeleton = Skeleton(("n_dup", null)),
            BindingTracks = [Track("n_dup", 0, true), Track("n_dup", 1, true)],
            RequestedBoneNames = ["n_dup"],
        });

        Assert.Equal(BoneMaskResolutionState.Ambiguous, Assert.Single(duplicateSkeleton.Resolutions).State);
        Assert.Equal(BoneMaskResolutionState.Ambiguous, Assert.Single(duplicateBinding.Resolutions).State);
    }

    [Fact]
    public void GeneratedAssetIdentityUsesContentAndNormalizedNamedSelections()
    {
        var original = Identity(["iv_ochinko_b", "iv_ochinko_a", "IV_OCHINKO_A"]);
        var reordered = Identity(["iv_ochinko_a", "iv_ochinko_b"]);
        var otherSkeleton = GeneratedBoneMaskAssets.CreateIdentity(new GeneratedBoneMaskAssetIdentityInput
        {
            SourcePapSha256 = new string('A', 64),
            ReplacementGamePath = "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap",
            MotionIdentity = "idle",
            TargetSkeletonIdentity = "skl_c0701b0001:changed",
            WriterSchemaVersion = "writer-v1",
            ExcludedBoneNames = ["iv_ochinko_a", "iv_ochinko_b"],
        });

        Assert.Equal(original.Key, reordered.Key);
        Assert.NotEqual(original.Key, otherSkeleton.Key);
        Assert.Equal(["iv_ochinko_a", "iv_ochinko_b"], original.ExcludedBoneNames);
    }

    [Fact]
    public void GeneratedOutputAlwaysStaysUnderTheDancyOwnedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DancyTests", Guid.NewGuid().ToString("N"));
        var path = GeneratedBoneMaskAssets.CreateOutputPath(root, Identity(["n_root"]));

        Assert.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("animation.pap", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnavailableWriterReturnsAnExplicitNonMutationFailure()
    {
        var result = new UnavailableAnimationTrackExclusionWriter().RebuildWithExcludedBones(new AnimationTrackExclusionWriteRequest
        {
            AssetIdentity = Identity(["n_root"]),
        });

        Assert.False(result.Succeeded);
        Assert.Equal(UnavailableAnimationTrackExclusionWriter.UnavailableErrorCode, result.ErrorCode);
    }

    [Fact]
    public void ValidatesWriterOutputAgainstTheExactPlannedNames()
    {
        var plan = BoneMaskResolver.Resolve(new BoneMaskResolutionRequest
        {
            TargetSkeleton = Skeleton(("n_root", null)),
            BindingTracks = [Track("n_root", 0, true)],
            RequestedBoneNames = ["n_root"],
        });
        var identity = Identity(["n_root"]);
        var request = new AnimationTrackExclusionWriteRequest
        {
            AssetIdentity = identity,
            ExclusionPlan = plan,
        };
        var result = new AnimationTrackExclusionWriteResult
        {
            Succeeded = true,
            GeneratedAssetKey = identity.Key,
            OutputPapPath = "C:/Dancy/generated/animation.pap",
            OutputPapSha256 = new string('B', 64),
            SourceTrackCount = 2,
            OutputTrackCount = 1,
            ExcludedBoneNames = ["n_root"],
            Validation = new AnimationTrackExclusionStructuralValidation
            {
                PapParsed = true,
                HkxParsed = true,
                BindingsValid = true,
                ExpectedTracksRemoved = true,
                NoUnexpectedMappings = true,
                ReconstructedRetainedTracks = true,
                RetainedTracksSemanticallyEquivalent = true,
            },
        };

        Assert.Empty(AnimationTrackExclusionResultValidator.Validate(request, result));
    }

    [Fact]
    public void SmokingFixtureModelRemovesExactlyEightNamedTracksFrom147()
    {
        var excluded = new[]
        {
            "iv_kougan_l", "iv_kougan_r", "iv_ochinko_a", "iv_ochinko_b",
            "iv_ochinko_c", "iv_ochinko_d", "iv_ochinko_e", "iv_ochinko_f",
        };
        var retained = Enumerable.Range(0, 139).Select(index => $"fixture_bone_{index:D3}").ToArray();
        var all = retained.Concat(excluded).ToArray();
        var plan = BoneMaskResolver.Resolve(new BoneMaskResolutionRequest
        {
            TargetSkeleton = new BoneMaskTargetSkeleton { Identity = "smoking-c0701-fixture", Bones = all.Select((name, index) => new BoneMaskSkeletonBone { Index = index, Name = name }).ToArray() },
            BindingTracks = all.Select((name, index) => Track(name, index, true)).ToArray(),
            RequestedBoneNames = excluded,
        });

        Assert.False(plan.HasBlockingResolution);
        Assert.Equal(147, all.Length);
        Assert.Equal(8, plan.ExcludedTracks.Count);
        Assert.Equal(139, all.Length - plan.ExcludedTracks.Count);
        Assert.All(excluded, name => Assert.Contains(plan.ExcludedTracks, track => string.Equals(track.BoneName, name, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(plan.ExcludedTracks, track => track.BoneName.StartsWith("fixture_bone_", StringComparison.Ordinal));
    }

    private static BoneMaskTargetSkeleton Skeleton(params (string Name, string? Parent)[] bones)
        => new()
        {
            Identity = "test-skeleton",
            Bones = bones.Select((bone, index) => new BoneMaskSkeletonBone { Index = index, Name = bone.Name, ParentName = bone.Parent }).ToArray(),
        };

    private static BoneMaskBindingTrack Track(string name, int trackIndex, bool animated)
        => new() { BindingIndex = 0, TrackIndex = trackIndex, BoneIndex = trackIndex, BoneName = name, IsAnimated = animated };

    private static BoneMaskResolutionState State(BoneMaskExclusionPlan plan, string name)
        => Assert.Single(plan.Resolutions, result => result.RequestedBoneName == name).State;

    private static GeneratedBoneMaskAssetIdentity Identity(IReadOnlyList<string> names)
        => GeneratedBoneMaskAssets.CreateIdentity(new GeneratedBoneMaskAssetIdentityInput
        {
            SourcePapSha256 = new string('A', 64),
            ReplacementGamePath = "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap",
            MotionIdentity = "idle",
            TargetSkeletonIdentity = "skl_c0701b0001",
            WriterSchemaVersion = "writer-v1",
            ExcludedBoneNames = names,
        });
}
