using Dancy.Pap;
using Xunit;

namespace Dancy.Tests;

public class PapStructuralPlannerTests
{
    [Fact]
    public void ClassifiesSingleSectionPap()
    {
        var water = Structure("water", new[] { "cbem_sp60_2lp" }, new[] { 0 });

        Assert.Equal(PapTopology.SingleSection, water.Topology);
        Assert.Equal(PapBindingEvidence.ExplicitHeaderField, Assert.Single(water.Sections).HavokBindingEvidence);
        Assert.Equal(PapBindingEvidence.SequentialPapFormat, Assert.Single(water.Sections).TimelineBindingEvidence);
    }

    [Fact]
    public void DiscoversStandingIdleHeaderAndTimelineBindings()
    {
        var idle = Structure("c0101 normal/idle", new[] { "cbna_add_dmg_f", "cbnm_id0" }, new[] { 0, 1 });

        Assert.Equal(2, idle.Sections.Count);
        Assert.Equal(2, idle.TimelineSections.Count);
        Assert.Collection(idle.Sections,
            first =>
            {
                Assert.Equal(0, first.AnimationIndex);
                Assert.Equal(0, first.HavokMotionIndex);
                Assert.Equal(0, first.TimelineSectionIndex);
            },
            second =>
            {
                Assert.Equal(1, second.AnimationIndex);
                Assert.Equal(1, second.HavokMotionIndex);
                Assert.Equal(1, second.TimelineSectionIndex);
            });
        Assert.Equal(PapTopology.Unknown, idle.Topology);
    }

    [Fact]
    public void TreatsEquivalentStandingIdleVariantsAsStructurallyConsistent()
    {
        var midlander = Structure("c0101 normal/idle", new[] { "cbna_add_dmg_f", "cbnm_id0" }, new[] { 0, 1 });
        var roegadyn = Structure("c0901 normal/idle", new[] { "cbna_add_dmg_f", "cbnm_id0" }, new[] { 0, 1 });

        Assert.Equal(midlander.Topology, roegadyn.Topology);
        Assert.Equal(midlander.Sections.Select(section => section.AnimationName), roegadyn.Sections.Select(section => section.AnimationName));
        Assert.Equal(midlander.Sections.Select(section => section.HavokMotionIndex), roegadyn.Sections.Select(section => section.HavokMotionIndex));
        Assert.Equal(midlander.Sections.Select(section => section.TimelineSectionIndex), roegadyn.Sections.Select(section => section.TimelineSectionIndex));
    }

    [Fact]
    public void RefusesAmbiguousMultiSectionTarget()
    {
        var plan = PapStructuralPlanner.Plan(
            Structure("pushups", new[] { "cbem_loop_emot08_2lp" }, new[] { 0 }),
            Structure("standing idle", new[] { "cbna_add_dmg_f", "cbnm_id0" }, new[] { 0, 1 }));

        Assert.Equal(PapStructuralPlanStatus.Unsupported, plan.Status);
        Assert.Contains("not understood", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(plan.TargetReplacementSection);
    }

    [Fact]
    public void RefusesCoupledTarget()
    {
        var plan = PapStructuralPlanner.Plan(
            Structure("pushups", new[] { "cbem_loop_emot08_2lp" }, new[] { 0 }),
            Structure("coupled", new[] { "first", "second" }, new[] { 0, 1 }, PapTopology.MultiSectionCoupled),
            0);

        Assert.Equal(PapStructuralPlanStatus.Unsupported, plan.Status);
        Assert.Contains("interdependent", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreservesEveryUnreplacedTargetSectionInDryRunPlan()
    {
        var plan = PapStructuralPlanner.Plan(
            Structure("pushups", new[] { "cbem_loop_emot08_2lp" }, new[] { 0 }),
            Structure("independent", new[] { "base", "auxiliary" }, new[] { 0, 1 }, PapTopology.MultiSectionIndependent),
            1);

        Assert.True(plan.IsCompatible);
        Assert.Equal(1, plan.TargetReplacementSection?.AnimationIndex);
        Assert.Equal(new[] { 0 }, plan.PreservedTargetSections.Select(section => section.AnimationIndex));
    }

    [Fact]
    public void PlannerIsDryRunOnlyAndDoesNotCreateFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dancy-structural-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var plan = PapStructuralPlanner.Plan(
                Structure("pushups", new[] { "cbem_loop_emot08_2lp" }, new[] { 0 }),
                Structure("water", new[] { "cbem_sp60_2lp" }, new[] { 0 }));

            Assert.True(plan.IsDryRun);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            Assert.DoesNotContain(plan.Evidence, evidence => evidence.Contains("writer invoked", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void KeepsWaterLikeSingleSectionPlanningUnchanged()
    {
        var source = Structure("pushups", new[] { "cbem_loop_emot08_2lp" }, new[] { 0 });
        var water = Structure("water", new[] { "cbem_sp60_2lp" }, new[] { 0 });

        var plan = PapStructuralPlanner.Plan(source, water);

        Assert.Equal(PapStructuralPlanStatus.Compatible, plan.Status);
        Assert.Equal("cbem_loop_emot08_2lp", plan.SourceMotion?.AnimationName);
        Assert.Equal("cbem_sp60_2lp", plan.TargetReplacementSection?.AnimationName);
        Assert.Empty(plan.PreservedTargetSections);
    }

    private static PapStructure Structure(string identity, string[] animationNames, int[] havokIndices, PapTopology? topology = null)
    {
        var timelineLocations = animationNames
            .Select((_, index) => new PapFileInspector.PapTimelineSectionLocation(index, 100 + index * 8, 8))
            .ToList();
        var inspection = new PapFileInspector.PapFileInspection
        {
            AnimationCount = animationNames.Length,
            AnimationNames = animationNames,
            AnimationTypes = Enumerable.Repeat(0, animationNames.Length).ToList(),
            HavokIndices = havokIndices,
            FaceAnimationFlags = Enumerable.Repeat(false, animationNames.Length).ToList(),
            TimelineSectionSizes = Enumerable.Repeat(8, animationNames.Length).ToList(),
            TimelineSections = timelineLocations,
        };
        var timelines = timelineLocations
            .Select(location => new PapTimelineSection(location.Index, location.Offset, location.Size, $"hash-{location.Index}", Array.Empty<string>()))
            .ToList();
        return PapStructure.FromInspection(identity, inspection, timelines, topology);
    }
}
