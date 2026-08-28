using Dancy.Domain;
using Xunit;

namespace Dancy.Tests;

public class TargetPathMatcherTests
{
    [Fact]
    public void UsesAllVariantsWhenTargetDoesNotMirrorTheSourceRig()
    {
        const string source = "chara/human/c1501/animation/a0001/bt_common/emote/source_loop.pap";
        var targets = new[]
        {
            "chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap",
            "chara/human/c0201/animation/a0001/bt_common/emote/target_loop.pap",
            "chara/human/c0901/animation/a0001/bt_common/emote/target_loop.pap",
            "chara/human/c1701/animation/a0001/bt_common/emote/target_loop.pap",
        };

        var result = TargetPathMatcher.Match(source, targets);

        Assert.Equal(TargetMatchStrategy.FallbackAllTargetVariants, result.Strategy);
        Assert.Equal(targets, result.GamePaths);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void PrefersTheExactTargetDirectory()
    {
        const string source = "chara/human/c1201/animation/a0001/bt_common/emote/source_loop.pap";
        var exact = "chara/human/c1201/animation/a0001/bt_common/emote/target_loop.pap";
        var result = TargetPathMatcher.Match(source, new[]
        {
            "chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap",
            exact,
        });

        Assert.Equal(TargetMatchStrategy.ExactDirectory, result.Strategy);
        Assert.Equal(new[] { exact }, result.GamePaths);
    }

    [Fact]
    public void SelectsTheSameRigAndLayerBeforeOtherCandidates()
    {
        const string source = "chara/human/c0901/animation/a0002/bt_common/emote/source_loop.pap";
        const string expected = "chara/human/c0901/animation/a0002/resident/target_loop.pap";

        var result = TargetPathMatcher.Match(source, new[]
        {
            "chara/human/c0901/animation/a0001/bt_common/emote/target_loop.pap",
            expected,
            "chara/human/c0101/animation/a0002/bt_common/emote/target_loop.pap",
        });

        Assert.Equal(TargetMatchStrategy.SameRigAndLayer, result.Strategy);
        Assert.Equal(new[] { expected }, result.GamePaths);
    }

    [Fact]
    public void SelectsTheSameRigWhenNoLayerMatches()
    {
        const string source = "chara/human/c0901/animation/a0002/bt_common/emote/source_loop.pap";
        const string expected = "chara/human/c0901/animation/a0001/bt_common/emote/target_loop.pap";

        var result = TargetPathMatcher.Match(source, new[]
        {
            expected,
            "chara/human/c0101/animation/a0002/bt_common/emote/target_loop.pap",
        });

        Assert.Equal(TargetMatchStrategy.SameRig, result.Strategy);
        Assert.Equal(new[] { expected }, result.GamePaths);
    }

    [Fact]
    public void UsesOneSharedTargetWhenItIsTheOnlyVariant()
    {
        const string target = "chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap";

        var result = TargetPathMatcher.Match(
            "chara/human/c1501/animation/a0001/bt_common/emote/source_loop.pap",
            new[] { target });

        Assert.Equal(TargetMatchStrategy.SingleSharedTarget, result.Strategy);
        Assert.Equal(new[] { target }, result.GamePaths);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void ReportsNoTargetWhenTheTargetCatalogIsEmpty()
    {
        var result = TargetPathMatcher.Match(
            "chara/human/c1501/animation/a0001/bt_common/emote/source_loop.pap",
            Array.Empty<string>());

        Assert.Equal(TargetMatchStrategy.NoMatch, result.Strategy);
        Assert.Empty(result.GamePaths);
        Assert.Single(result.Warnings);
    }
}
