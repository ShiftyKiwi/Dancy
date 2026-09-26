using Dancy.Core.Models;
using Dancy.Domain;
using Xunit;

namespace Dancy.Tests;

public class DomainSafetyTests
{
    [Theory]
    [InlineData("c0101", "Midlander", "Male")]
    [InlineData("c0201", "Midlander", "Female")]
    [InlineData("c0701", "Miqo'te", "Male")]
    [InlineData("c0901", "Roegadyn", "Male")]
    [InlineData("c1101", "Lalafell", "Male")]
    [InlineData("c1501", "Hrothgar", "Male")]
    [InlineData("c1701", "Viera", "Male")]
    public void ParsesRepresentativeCharacterPaths(string code, string race, string sex)
    {
        var identity = CharacterPathIdentity.FromGamePath($"chara/human/{code}/animation/a0001/bt_common/emote/loop.pap");

        Assert.True(identity.IsKnown);
        Assert.Equal(code, identity.Code);
        Assert.Equal(race, identity.Race);
        Assert.Equal(sex, identity.Sex);
    }

    [Fact]
    public void KeepsAppliesToSeparateFromPhysicalPapOrigin()
    {
        var entry = new ParsedEmoteOverride
        {
            GamePath = "chara/human/c1501/animation/a0001/bt_common/emote/source_loop.pap",
            ModdedPapPath = "pair/chara/human/c0901/animation/a0001/bt_common/emote/source_loop.pap",
        };

        Assert.Equal("c1501", entry.AppliesTo.Character.Code);
        Assert.Equal("c0901", entry.PapOrigin.Code);
        Assert.NotEqual(entry.AppliesTo.Character.Code, entry.PapOrigin.Code);
    }

    [Fact]
    public void RejectsUnknownCharacterPathAndKeepsTransitionsOutOfOrdinaryTargetCategories()
    {
        Assert.False(CharacterPathIdentity.FromGamePath("chara/human/c9999/animation/a0001/test.pap").IsKnown);
        Assert.True(TargetEmotePolicy.IsVisible(TargetSelectionCategory.LoopingEmotes, TargetBehavior.LoopingEmote));
        Assert.True(TargetEmotePolicy.IsVisible(TargetSelectionCategory.PosesAndIdles, TargetBehavior.PersistentPose));
        Assert.True(TargetEmotePolicy.IsVisible(TargetSelectionCategory.Advanced, TargetBehavior.OneShot));
        Assert.False(TargetEmotePolicy.IsVisible(TargetSelectionCategory.LoopingEmotes, TargetBehavior.Transition));
        Assert.False(TargetEmotePolicy.IsVisible(TargetSelectionCategory.PosesAndIdles, TargetBehavior.Transition));
        Assert.False(TargetEmotePolicy.IsVisible(TargetSelectionCategory.Advanced, TargetBehavior.Transition));
    }
}
