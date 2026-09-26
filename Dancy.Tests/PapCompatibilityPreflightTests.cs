using Dancy.Pap;
using Xunit;

namespace Dancy.Tests;

public class PapCompatibilityPreflightTests
{
    [Fact]
    public void AcceptsWaterLikeSingleSectionLoopVariants()
    {
        var source = Inspection("cbem_loop_emot08_2lp", 0);
        var water = Inspection("sp60_loop", 0);

        var result = PapCompatibilityPreflight.Evaluate(source, new[] { water });

        Assert.Equal(PapCompatibilityStatus.Compatible, result.Status);
    }

    [Fact]
    public void WarnsWhenTheTargetUsesADifferentHavokIndex()
    {
        var result = PapCompatibilityPreflight.Evaluate(Inspection("source", 0), new[] { Inspection("target", 1) });

        Assert.Equal(PapCompatibilityStatus.CompatibleWithWarning, result.Status);
    }

    [Fact]
    public void BlocksMultiSectionPapsInsteadOfWritingOnlyTheFirstSection()
    {
        var complex = new PapFileInspector.PapFileInspection
        {
            AnimationCount = 2,
            AnimationNames = new[] { "first", "second" },
            HavokIndices = new[] { 0, 1 },
            TimelineSectionSizes = new[] { 8, 8 },
        };

        var result = PapCompatibilityPreflight.Evaluate(complex, new[] { Inspection("target", 0) });

        Assert.Equal(PapCompatibilityStatus.Unsupported, result.Status);
        Assert.False(result.CanCreate);
    }

    private static PapFileInspector.PapFileInspection Inspection(string animationName, int havok)
        => new()
        {
            AnimationCount = 1,
            AnimationNames = new[] { animationName },
            HavokIndices = new[] { havok },
            TimelineSectionSizes = new[] { 8 },
        };
}
