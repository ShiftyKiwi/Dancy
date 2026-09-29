using Dancy.Core.Models;
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
        Assert.Equal(1, copy.SourceOptionPhysicalPapCount);
        Assert.Equal(2, plan.PlannedMappings.Count);
    }

    [Fact]
    public void GivesSharedPhysicalSourcePapsDifferentStableIdsForDifferentOptionSelectors()
    {
        OverridePlanRequest Request(string option, string companion) => new()
        {
            ModIdentity = "warrior-of-lift",
            SourceGroupName = "Treadmill - /breathcontrol",
            SourceOptionName = option,
            TargetTimelineKey = "emote/target_loop",
            TargetName = "Target",
            Sources = new[]
            {
                new OverridePlanSource("chara/human/c0101/animation/a0001/bt_common/emote/loop_emot11_loop.pap", "files/shared.pap"),
            },
            CompanionTimelines = new[]
            {
                new CompanionTimelineOverride($"chara/action/emote/{companion}", $"files/chara/action/emote/{companion}"),
            },
            TargetGamePaths = new[]
            {
                "chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap",
            },
        };

        var run = OverridePlanner.Create(Request("Run", "loop_emot11_loop_run.tmb"));
        var sprint = OverridePlanner.Create(Request("Sprint", "loop_emot11_loop_sprint.tmb"));

        Assert.True(run.IsValid);
        Assert.True(sprint.IsValid);
        Assert.NotEqual(run.OverrideId, sprint.OverrideId);
        Assert.NotEqual(run.PapCopies[0].OutputRelativePath, sprint.PapCopies[0].OutputRelativePath);
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

    [Fact]
    public void RejectsStartPapEvenWhenItsFamilyNameContainsLoop()
    {
        var plan = OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = "fixture",
            SourceGroupName = "Bench Press - /pushups",
            SourceOptionName = "Enable",
            TargetTimelineKey = "emote_sp/sp60_loop",
            TargetName = "Water",
            TargetCommand = "/water",
            Sources = new[]
            {
                new OverridePlanSource("chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_start.pap", "files/start.pap"),
            },
            TargetGamePaths = new[]
            {
                "chara/human/c0101/animation/a0001/bt_common/emote_sp/sp60_loop.pap",
            },
        });

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, error => error.Contains("only Loop-phase", StringComparison.Ordinal));
    }

    [Fact]
    public void PlansWarriorOfLiftPushupsToWaterUsingOnlyLoopSources()
    {
        var sourceCodes = new[] { "c0101", "c0201", "c0501", "c0601", "c0801", "c0901", "c1401", "c1101" };
        var targetCodes = new[] { "c0101", "c0201", "c0501", "c0601", "c0801", "c0901", "c1101" };
        var plan = OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = "warrior-of-lift",
            SourceGroupName = "Bench Press - /pushups",
            SourceOptionName = "Enable",
            SourceAnimationName = "Push-ups",
            SourceAnimationCommand = "/pushups",
            TargetTimelineKey = "emote_sp/sp60_loop",
            TargetName = "Water",
            TargetCommand = "/water",
            Sources = sourceCodes.Select(code => new OverridePlanSource(
                $"chara/human/{code}/animation/a0001/bt_common/emote/loop_emot08_loop.pap",
                code == "c1101"
                    ? "files/chara/human/c1101/animation/a0001/bt_common/emote/loop_emot08_loop.pap"
                    : "files/chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_loop.pap")).ToList(),
            TargetGamePaths = targetCodes.Select(code => $"chara/human/{code}/animation/a0001/bt_common/emote_sp/sp60_loop.pap").ToList(),
        });

        Assert.True(plan.IsValid);
        Assert.Equal(2, plan.PapCopies.Count);
        Assert.Equal(7, plan.PlannedMappings.Count);
        Assert.Equal("Push-ups -> Water · 7 paths", plan.DisplayName);
        Assert.Equal(
            "Dancy animation override\n\nSource:\nBench Press - /pushups\nOption: Enable\nAnimation: Push-ups (/pushups)\n\nTarget:\nWater (/water)\n\nApplies to:\nMidlander Male (c0101)\nMidlander Female (c0201)\nElezen Male (c0501)\nElezen Female (c0601)\nMiqo'te Female (c0801)\nRoegadyn Male (c0901)\nLalafell Male (c1101)\n\nTarget mappings:\n7",
            plan.Description);
    }

    [Fact]
    public void DescriptionNamesTheOneAffectedTargetVariant()
    {
        var plan = OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = "fixture",
            SourceGroupName = "Group",
            SourceOptionName = "Option",
            SourceAnimationName = "Push-ups",
            TargetTimelineKey = "emote/target_loop",
            TargetName = "Water",
            TargetCommand = "/water",
            Sources = new[]
            {
                new OverridePlanSource("chara/human/c0701/animation/a0001/bt_common/emote/source_loop.pap", "source.pap"),
            },
            TargetGamePaths = new[]
            {
                "chara/human/c0701/animation/a0001/bt_common/emote/target_loop.pap",
            },
        });

        Assert.True(plan.IsValid);
        Assert.Contains("Miqo'te Male (c0701)", plan.Description, StringComparison.Ordinal);
        Assert.Contains("Target mappings:\n1", plan.Description, StringComparison.Ordinal);
    }
}
