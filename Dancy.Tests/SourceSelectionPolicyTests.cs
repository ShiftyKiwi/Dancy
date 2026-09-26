using Dancy.Core;
using Dancy.Core.Models;
using Dancy.Domain;
using Xunit;

namespace Dancy.Tests;

public class SourceSelectionPolicyTests
{
    [Theory]
    [InlineData("chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_start.pap", AnimationPhase.Start)]
    [InlineData("chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_loop.pap", AnimationPhase.Loop)]
    [InlineData("chara/human/c0101/animation/a0001/bt_common/emote/dance_end.pap", AnimationPhase.End)]
    [InlineData("chara/human/c0101/animation/a0001/bt_common/emote/dance_st.pap", AnimationPhase.Start)]
    [InlineData("chara/human/c0101/animation/a0001/bt_common/emote/dance_ed.pap", AnimationPhase.End)]
    public void ClassifiesTerminalFfxivPhaseBeforeFamilyPrefix(string path, AnimationPhase expected)
        => Assert.Equal(expected, GamePathIdentity.Parse(path).Phase);

    [Fact]
    public void DefaultsToLoopPathsAndCannotReturnStartPathsForAStandardOverride()
    {
        var start = Entry("loop_emot08_start.pap", "start.pap");
        var loop = Entry("loop_emot08_loop.pap", "loop.pap");
        var selected = SourceSelectionPolicy.DefaultLoopGamePaths(new[] { start, loop });

        Assert.DoesNotContain(start.GamePath, selected, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(loop.GamePath, selected, StringComparer.OrdinalIgnoreCase);

        var externallySeeded = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        externallySeeded.Add(start.GamePath); // Simulates a stale or externally seeded UI selection.
        var normalSelection = SourceSelectionPolicy.SelectedLoopEntries(new[] { start, loop }, externallySeeded);
        Assert.Equal(new[] { loop }, normalSelection);
    }

    [Fact]
    public void RepresentsStartAndLoopAsOneLogicalAnimation()
    {
        var option = new RemappableOption
        {
            GroupName = "Bench Press - /pushups",
            OptionName = "Enable",
            Entries = new List<ParsedEmoteOverride>
            {
                Entry("loop_emot08_start.pap", "files/start.pap"),
                Entry("loop_emot08_loop.pap", "files/loop.pap"),
            },
        };

        var logical = Assert.Single(option.LogicalAnimations);
        Assert.Equal("Push-ups", logical.Name);
        Assert.Equal(new[] { AnimationPhase.Start, AnimationPhase.Loop }, logical.Phases.OrderBy(phase => phase));
        Assert.Single(option.LoopEntries);
        Assert.Equal(2, logical.PhysicalPapCount);
    }

    private static ParsedEmoteOverride Entry(string fileName, string papPath)
        => new()
        {
            GamePath = $"chara/human/c0101/animation/a0001/bt_common/emote/{fileName}",
            ModdedPapPath = papPath,
            EmoteName = "Push-ups",
            EmoteCommand = "/pushups",
            EmoteRowId = 8,
        };
}
