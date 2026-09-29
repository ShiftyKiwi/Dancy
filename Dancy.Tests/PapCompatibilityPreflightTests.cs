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
        Assert.Equal(PapCompatibilityBlocker.Source, result.Blocker);
        Assert.Contains("2 animation headers", result.Reason, StringComparison.Ordinal);
        Assert.Contains("2 TMB sections", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsASelectedMultiMotionSourceForANormalOneSectionTarget()
    {
        var source = new PapFileInspector.PapFileInspection
        {
            AnimationCount = 4,
            AnimationNames = new[] { "style", "walk", "run", "sprint" },
            HavokIndices = new[] { 0, 1, 2, 3 },
            TimelineSectionSizes = new[] { 8, 8, 8, 8 },
        };
        var selection = new SourceAnimationSelection(
            "chara/human/c0101/animation/a0001/bt_common/emote/loop.pap",
            "files/shared.pap",
            2,
            "run",
            2,
            2,
            SourceAnimationSelectionMethod.CompanionTimelineEvent,
            "Companion timeline selected event run.");

        var result = PapCompatibilityPreflight.Evaluate(
            source,
            selection,
            new PapTargetInspection("chara/human/c0101/animation/a0001/bt_common/emote/target.pap", Inspection("target", 2)));

        Assert.Equal(PapCompatibilityStatus.Compatible, result.Status);
        Assert.True(result.CanCreate);
    }

    [Fact]
    public void KeepsSelectorBackedMultiMotionSourcesOutOfStandingIdle()
    {
        var source = new PapFileInspector.PapFileInspection
        {
            AnimationCount = 2,
            AnimationNames = new[] { "style", "run" },
            HavokIndices = new[] { 0, 1 },
            TimelineSectionSizes = new[] { 8, 8 },
        };
        var selection = new SourceAnimationSelection(
            "chara/human/c0101/animation/a0001/bt_common/emote/loop.pap",
            "files/shared.pap",
            1,
            "run",
            1,
            1,
            SourceAnimationSelectionMethod.CompanionTimelineEvent,
            "Companion timeline selected event run.");

        var result = PapCompatibilityPreflight.Evaluate(
            source,
            selection,
            new PapTargetInspection("chara/human/c0701/animation/a0001/bt_common/resident/idle.pap", StandingIdleInspection()));

        Assert.Equal(PapCompatibilityStatus.Unsupported, result.Status);
        Assert.Contains("selector-backed", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptsOnlyTheProvenStandingIdleMotionZeroTopology()
    {
        var source = Inspection("loop", 0);
        var target = new PapTargetInspection(
            "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap",
            StandingIdleInspection());

        var result = PapCompatibilityPreflight.Evaluate(source, target);

        Assert.Equal(PapCompatibilityStatus.Compatible, result.Status);
        Assert.Equal(PapOverrideWriteStrategy.StandingIdleMotion0, result.WriteStrategy);
        Assert.True(result.CanCreate);
    }

    [Fact]
    public void RejectsAnArbitraryTwoSectionTargetEvenWhenItsHeadersLookLikeStandingIdle()
    {
        var result = PapCompatibilityPreflight.Evaluate(
            Inspection("loop", 0),
            new PapTargetInspection(
                "chara/human/c0701/animation/a0001/bt_common/emote/other.pap",
                StandingIdleInspection()));

        Assert.Equal(PapCompatibilityStatus.Unsupported, result.Status);
        Assert.False(result.CanCreate);
        Assert.Null(result.WriteStrategy);
        Assert.Equal(PapCompatibilityBlocker.Target, result.Blocker);
    }

    [Fact]
    public void RejectsStandingIdleWhenTheLoopSourceIsNotHavokMotionZero()
    {
        var result = PapCompatibilityPreflight.Evaluate(
            Inspection("loop", 1),
            new PapTargetInspection(
                "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap",
                StandingIdleInspection()));

        Assert.Equal(PapCompatibilityStatus.Unsupported, result.Status);
        Assert.False(result.CanCreate);
        Assert.Contains("motion 0", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsStandingIdleWhenTheSecondTargetSectionDoesNotMatchTheProvenHeader()
    {
        var invalid = StandingIdleInspection("unexpected");

        var result = PapCompatibilityPreflight.Evaluate(
            Inspection("loop", 0),
            new PapTargetInspection(
                "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap",
                invalid));

        Assert.Equal(PapCompatibilityStatus.Unsupported, result.Status);
        Assert.False(result.CanCreate);
    }

    [Fact]
    public void CatalogAcceptsOnlyExplicitCanonicalStandingIdleVariants()
    {
        var variants = new[]
        {
            "c0101", "c0201", "c0401", "c0501", "c0601", "c0701", "c0801", "c0901",
            "c1001", "c1101", "c1301", "c1401", "c1501", "c1601", "c1701", "c1801",
        }.Select(code => new PapTargetInspection(
            $"chara/human/{code}/animation/a0001/bt_common/resident/idle.pap",
            StandingIdleInspection()));

        var summary = StandingIdleVariantCatalog.Analyze(variants);

        Assert.Equal(16, summary.TotalVariantCount);
        Assert.Equal(16, summary.SupportedVariantCount);
        Assert.True(summary.AllVariantsSupported);
    }

    [Fact]
    public void CatalogRejectsOnlyTheDivergentVariantInsteadOfTrustingSectionCounts()
    {
        var variants = new[]
        {
            new PapTargetInspection(
                "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap",
                StandingIdleInspection()),
            new PapTargetInspection(
                "chara/human/c0801/animation/a0001/bt_common/resident/idle.pap",
                StandingIdleInspection("another_two_section_pap")),
        };

        var summary = StandingIdleVariantCatalog.Analyze(variants);

        Assert.Equal(2, summary.TotalVariantCount);
        Assert.Equal(1, summary.SupportedVariantCount);
        Assert.False(summary.AllVariantsSupported);
        Assert.Single(summary.SupportedGamePaths);
        Assert.Equal("c0701", summary.Variants.Single(variant => variant.IsSupported).Character.Code);
    }

    private static PapFileInspector.PapFileInspection Inspection(string animationName, int havok)
        => new()
        {
            AnimationCount = 1,
            AnimationNames = new[] { animationName },
            AnimationTypes = new[] { 0 },
            HavokIndices = new[] { havok },
            FaceAnimationFlags = new[] { false },
            TimelineSectionSizes = new[] { 8 },
        };

    private static PapFileInspector.PapFileInspection StandingIdleInspection(string secondAnimationName = "cbnm_id0")
        => new()
        {
            AnimationCount = 2,
            AnimationNames = new[] { "cbna_add_dmg_f", secondAnimationName },
            AnimationTypes = new[] { 15, 0 },
            HavokIndices = new[] { 0, 1 },
            FaceAnimationFlags = new[] { false, false },
            TimelineSectionSizes = new[] { 8, 8 },
        };
}
