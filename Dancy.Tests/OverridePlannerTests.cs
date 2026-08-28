using Dancy.Domain;
using Xunit;

namespace Dancy.Tests;

public class OverridePlannerTests
{
    [Fact]
    public void ProducesStableOutputNamesForTheSameSelection()
    {
        var request = new OverridePlanRequest
        {
            ModIdentity = "my-mod",
            SourceGroupName = "Pair Selection",
            SourceOptionName = "Tall x Tall",
            TargetTimelineKey = "emote/target_loop",
            TargetName = "Target",
            TargetCommand = "/target",
            Sources = new[]
            {
                new OverridePlanSource(
                    "chara/human/c0101/animation/a0001/bt_common/emote/source_loop.pap",
                    "pair/source_loop.pap"),
            },
            TargetGamePaths = new[]
            {
                "chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap",
            },
        };

        var first = OverridePlanner.Create(request);
        var second = OverridePlanner.Create(request);

        Assert.True(first.IsValid);
        Assert.Equal(first.OverrideId, second.OverrideId);
        Assert.Equal(first.PapCopies[0].OutputRelativePath, second.PapCopies[0].OutputRelativePath);
    }

    [Fact]
    public void RefusesTwoDifferentSourcesThatWouldOverwriteTheSameTarget()
    {
        var plan = OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = "my-mod",
            SourceGroupName = "Pair Selection",
            SourceOptionName = "Shared",
            TargetTimelineKey = "emote/target_loop",
            TargetName = "Target",
            Sources = new[]
            {
                new OverridePlanSource("chara/human/c1501/animation/a0001/bt_common/emote/first_loop.pap", "pair/first.pap"),
                new OverridePlanSource("chara/human/c1601/animation/a0001/bt_common/emote/second_loop.pap", "pair/second.pap"),
            },
            TargetGamePaths = new[] { "chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap" },
        });

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, error => error.Contains("would both replace", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoricalC1501FallbackProducesEveryTargetMapping()
    {
        var targetPaths = new[]
        {
            "chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap",
            "chara/human/c0201/animation/a0001/bt_common/emote/target_loop.pap",
            "chara/human/c0901/animation/a0001/bt_common/emote/target_loop.pap",
            "chara/human/c1701/animation/a0001/bt_common/emote/target_loop.pap",
        };
        var plan = OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = "fixture",
            SourceGroupName = "Fixture source",
            SourceOptionName = "c1501 source",
            TargetTimelineKey = "emote/target_loop",
            TargetName = "Target",
            TargetCommand = "/target",
            Sources = new[]
            {
                new OverridePlanSource("chara/human/c1501/animation/a0001/bt_common/emote/source_loop.pap", "fixture/source.pap"),
            },
            TargetGamePaths = targetPaths,
        });

        var copy = Assert.Single(plan.PapCopies);
        Assert.True(plan.IsValid);
        Assert.Equal(TargetMatchStrategy.FallbackAllTargetVariants, Assert.Single(copy.MatchResults).Strategy);
        Assert.Equal(targetPaths, copy.TargetGamePaths);
        Assert.Equal(targetPaths.Length, plan.PlannedMappings.Count);
    }

    [Fact]
    public void GroupsSharedPhysicalPapsIntoOneCopy()
    {
        var plan = OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = "fixture",
            SourceGroupName = "Pair selection",
            SourceOptionName = "Shared physical PAP",
            TargetTimelineKey = "emote/target_loop",
            TargetName = "Target",
            Sources = new[]
            {
                new OverridePlanSource("chara/human/c0101/animation/a0001/bt_common/emote/source_loop.pap", "pair/shared.pap"),
                new OverridePlanSource("chara/human/c0201/animation/a0001/bt_common/emote/source_loop.pap", "pair/shared.pap"),
            },
            TargetGamePaths = new[]
            {
                "chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap",
                "chara/human/c0201/animation/a0001/bt_common/emote/target_loop.pap",
            },
        });

        var copy = Assert.Single(plan.PapCopies);
        Assert.True(plan.IsValid);
        Assert.Equal(2, copy.SourceGamePaths.Count);
        Assert.Equal(2, plan.PlannedMappings.Count);
    }

    [Fact]
    public void RejectsAPlanWithoutTargetPaps()
    {
        var plan = OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = "fixture",
            SourceGroupName = "Group",
            SourceOptionName = "Option",
            TargetTimelineKey = "emote/target_loop",
            Sources = new[]
            {
                new OverridePlanSource("chara/human/c0101/animation/a0001/bt_common/emote/source_loop.pap", "source.pap"),
            },
            TargetGamePaths = Array.Empty<string>(),
        });

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, error => error.Contains("no resolvable PAP", StringComparison.OrdinalIgnoreCase));
    }
}
