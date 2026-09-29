using Dancy.Domain;
using Xunit;

namespace Dancy.Tests;

public class TargetSemanticsTests
{
    [Fact]
    public void ClassifiesKnownLoopingEmote()
    {
        var behavior = TargetSemantics.Classify(
            hasCommand: true,
            hasLoopTimeline: true,
            context: TargetContext.Emote,
            primaryPhase: AnimationPhase.Loop);

        Assert.Equal(TargetBehavior.LoopingEmote, behavior);
        Assert.Null(TargetSemantics.BehaviorNotice(behavior));
    }

    [Theory]
    [InlineData(TargetContext.StandingIdle)]
    [InlineData(TargetContext.GroundSit)]
    [InlineData(TargetContext.ChairSit)]
    [InlineData(TargetContext.SleepOrLie)]
    public void ClassifiesKnownPersistentStatesIndependentlyOfPapPhase(TargetContext context)
    {
        var behavior = TargetSemantics.Classify(
            hasCommand: false,
            hasLoopTimeline: false,
            context: context,
            primaryPhase: AnimationPhase.Unknown);

        Assert.Equal(TargetBehavior.PersistentPose, behavior);
        Assert.True(TargetEmotePolicy.IsVisible(TargetSelectionCategory.PosesAndIdles, behavior));
        var notice = Assert.IsType<TargetBehaviorNotice>(TargetSemantics.BehaviorNotice(behavior));
        Assert.Equal(TargetNoticeLevel.Informational, notice.Level);
        Assert.Contains("Remains active", notice.Description);
    }

    [Fact]
    public void ClassifiesCommandBackedNonLoopAsOneShot()
    {
        var behavior = TargetSemantics.Classify(
            hasCommand: true,
            hasLoopTimeline: false,
            context: TargetContext.Emote,
            primaryPhase: AnimationPhase.Start);

        Assert.Equal(TargetBehavior.OneShot, behavior);
        var notice = Assert.IsType<TargetBehaviorNotice>(TargetSemantics.BehaviorNotice(behavior));
        Assert.Equal(TargetNoticeLevel.Caution, notice.Level);
        Assert.Equal("One-shot target", notice.Heading);
        Assert.True(TargetEmotePolicy.IsVisible(TargetSelectionCategory.Advanced, behavior));
    }

    [Fact]
    public void ClassifiesRawStartTargetAsHiddenTransition()
    {
        var behavior = TargetSemantics.Classify(
            hasCommand: false,
            hasLoopTimeline: false,
            context: TargetContext.Unknown,
            primaryPhase: AnimationPhase.Start);

        Assert.Equal(TargetBehavior.Transition, behavior);
        Assert.False(TargetEmotePolicy.IsVisible(TargetSelectionCategory.LoopingEmotes, behavior));
        Assert.False(TargetEmotePolicy.IsVisible(TargetSelectionCategory.PosesAndIdles, behavior));
        Assert.False(TargetEmotePolicy.IsVisible(TargetSelectionCategory.Advanced, behavior));
    }

    [Fact]
    public void ClassifiesUnidentifiedTargetAsUnknownWithExplicitWarning()
    {
        var behavior = TargetSemantics.Classify(
            hasCommand: false,
            hasLoopTimeline: false,
            context: TargetContext.Unknown,
            primaryPhase: AnimationPhase.Unknown);

        Assert.Equal(TargetBehavior.Unknown, behavior);
        var notice = Assert.IsType<TargetBehaviorNotice>(TargetSemantics.BehaviorNotice(behavior));
        Assert.Equal(TargetNoticeLevel.Caution, notice.Level);
        Assert.Equal("Playback behavior unknown", notice.Heading);
        Assert.Contains("could not be determined", notice.Description);
    }

    [Theory]
    [InlineData("Sit on Ground", "/groundsit", "Ground-sit state", TargetBehavior.PersistentPose, TargetContext.GroundSit)]
    [InlineData("Sit-ups", "/situps", "/situps", TargetBehavior.LoopingEmote, TargetContext.Emote)]
    [InlineData("Sit", "/lounge", "Seated on furniture state", TargetBehavior.PersistentPose, TargetContext.ChairSit)]
    public void SitSearchFindsDifferentTargetTypes(string name, string command, string trigger, TargetBehavior behavior, TargetContext context)
    {
        Assert.True(TargetSemantics.MatchesSearch(
            "sit",
            name,
            command,
            trigger,
            TargetSemantics.DisplayName(behavior),
            TargetSemantics.DisplayName(context)));
    }

    [Fact]
    public void ActiveSearchSpansTargetCategoriesButKeepsTransitionsHidden()
    {
        Assert.True(TargetEmotePolicy.IsSearchResult(TargetBehavior.LoopingEmote));
        Assert.True(TargetEmotePolicy.IsSearchResult(TargetBehavior.PersistentPose));
        Assert.True(TargetEmotePolicy.IsSearchResult(TargetBehavior.OneShot));
        Assert.True(TargetEmotePolicy.IsSearchResult(TargetBehavior.Unknown));
        Assert.False(TargetEmotePolicy.IsSearchResult(TargetBehavior.Transition));
    }
}
