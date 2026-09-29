using Dancy.Core.Models;
using Dancy.Pap;
using Xunit;

namespace Dancy.Tests;

public class SourceAnimationSelectorTests
{
    [Fact]
    public void KeepsTraditionalOneMotionSourcesAsSingleMotion()
    {
        var result = SourceAnimationSelector.Select(
            "chara/human/c0101/animation/a0001/bt_common/emote/loop.pap",
            "files/loop.pap",
            Inspection(new[] { "cbem_loop" }, new[] { 0 }),
            Array.Empty<CompanionTimelineOverride>(),
            _ => Array.Empty<string>(),
            new IReadOnlyList<string>[] { new[] { "cbem_loop" } });

        var selection = Assert.IsType<SourceAnimationSelection>(result.Selection);
        Assert.True(result.IsSuccess);
        Assert.Equal(SourceAnimationSelectionMethod.SingleMotion, selection.Method);
        Assert.Equal(0, selection.AnimationHeaderIndex);
        Assert.Equal(0, selection.HavokMotionIndex);
        Assert.Equal(0, selection.EmbeddedTmbIndex);
    }

    [Theory]
    [InlineData("Style", "loop_emot11_loop_back.tmb", "cbem_treadmill_01b_lp0", 0)]
    [InlineData("Walk", "loop_emot11_loop.tmb", "cbem_treadmill_01f_lp0", 1)]
    [InlineData("Run", "loop_emot11_loop_run.tmb", "cbem_treadmill_02f_lp0", 2)]
    [InlineData("Sprint", "loop_emot11_loop_sprint.tmb", "cbem_treadmill_sprint_lp0", 3)]
    public void SelectsExactlyOneCompleteMotionFromCompanionTimelineEvidence(
        string option,
        string companionFile,
        string expectedEvent,
        int expectedIndex)
    {
        var timeline = Timeline(companionFile);
        var result = Select(timeline, new[] { expectedEvent });

        var selection = Assert.IsType<SourceAnimationSelection>(result.Selection);
        Assert.True(result.IsSuccess);
        Assert.Equal(SourceAnimationSelectionMethod.CompanionTimelineEvent, selection.Method);
        Assert.Equal(expectedEvent, selection.AnimationEvent);
        Assert.Equal(expectedIndex, selection.AnimationHeaderIndex);
        Assert.Equal(expectedIndex, selection.HavokMotionIndex);
        Assert.Equal(expectedIndex, selection.EmbeddedTmbIndex);
        Assert.Contains(companionFile, selection.Evidence, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(option));
    }

    [Fact]
    public void RefusesASelectorThatMatchesNoSourceHeader()
    {
        var result = Select(Timeline("selector.tmb"), new[] { "not_a_treadmill_event" });

        Assert.False(result.IsSuccess);
        Assert.Contains("could not determine which animation", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesACompanionTimelineWithMultipleCandidateEvents()
    {
        var result = Select(Timeline("selector.tmb"), new[] { "cbem_treadmill_01f_lp0", "cbem_treadmill_02f_lp0" });

        Assert.False(result.IsSuccess);
        Assert.Contains("multiple animations", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesAnInvalidHavokMotionBinding()
    {
        var inspection = Inspection(Events, new[] { 0, 1, -1, 3 });
        var result = SourceAnimationSelector.Select(
            "chara/human/c0101/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
            "files/shared.pap",
            inspection,
            new[] { Timeline("loop_emot11_loop_run.tmb") },
            _ => new[] { "cbem_treadmill_02f_lp0" },
            Embedded);

        Assert.False(result.IsSuccess);
        Assert.Contains("invalid Havok", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesWhenTheSelectedEmbeddedTimelineDoesNotMatchTheHeaderEvent()
    {
        var embedded = new IReadOnlyList<string>[]
        {
            new[] { Events[0] },
            new[] { Events[1] },
            new[] { Events[1] },
            new[] { Events[3] },
        };
        var result = SourceAnimationSelector.Select(
            "chara/human/c0101/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
            "files/shared.pap",
            Inspection(Events, new[] { 0, 1, 2, 3 }),
            new[] { Timeline("loop_emot11_loop_run.tmb") },
            _ => new[] { "cbem_treadmill_02f_lp0" },
            embedded);

        Assert.False(result.IsSuccess);
        Assert.Contains("embedded PAP timeline", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static SourceAnimationSelectionResult Select(CompanionTimelineOverride timeline, IReadOnlyList<string> events)
        => SourceAnimationSelector.Select(
            "chara/human/c0101/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
            "files/shared.pap",
            Inspection(Events, new[] { 0, 1, 2, 3 }),
            new[] { timeline },
            _ => events,
            Embedded);

    private static CompanionTimelineOverride Timeline(string file)
        => new($"chara/action/emote/{file}", $"files/chara/action/emote/{file}");

    private static PapFileInspector.PapFileInspection Inspection(IReadOnlyList<string> names, IReadOnlyList<int> havok)
        => new()
        {
            AnimationCount = names.Count,
            AnimationNames = names,
            HavokIndices = havok,
            TimelineSectionSizes = Enumerable.Repeat(8, names.Count).ToList(),
        };

    private static readonly string[] Events =
    [
        "cbem_treadmill_01b_lp0",
        "cbem_treadmill_01f_lp0",
        "cbem_treadmill_02f_lp0",
        "cbem_treadmill_sprint_lp0",
    ];

    private static readonly IReadOnlyList<string>[] Embedded =
    [
        new[] { Events[0] },
        new[] { Events[1] },
        new[] { Events[2] },
        new[] { Events[3], "cfxf_clench" },
    ];
}
