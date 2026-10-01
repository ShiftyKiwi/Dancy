using Dancy.Domain;
using Xunit;

namespace Dancy.Tests;

public class TargetCatalogPresentationCacheTests
{
    [Fact]
    public void SearchAndCategoryFilteringUseOnlyCachedPresentationFields()
    {
        var catalog = new TargetCatalogPresentationCache<string>(
        [
            Entry("water", "Water", "/water", "Looping Emote", TargetBehavior.LoopingEmote),
            Entry("sit", "Sit", "/lounge", "Persistent Pose", TargetBehavior.PersistentPose),
            Entry("wave", "Wave", "/wave", "One-shot", TargetBehavior.OneShot),
        ]);

        Assert.Equal(["Water"], catalog.GetResults(TargetSelectionCategory.LoopingEmotes, string.Empty, 50).Select(entry => entry.Name));
        Assert.Equal(["Sit"], catalog.GetResults(TargetSelectionCategory.PosesAndIdles, string.Empty, 50).Select(entry => entry.Name));
        Assert.Equal(["Wave"], catalog.GetResults(TargetSelectionCategory.Advanced, string.Empty, 50).Select(entry => entry.Name));
        Assert.Equal(["Water"], catalog.GetResults(TargetSelectionCategory.PosesAndIdles, "water", 50).Select(entry => entry.Name));
        Assert.Equal(["Sit"], catalog.GetResults(TargetSelectionCategory.LoopingEmotes, "persistent", 50).Select(entry => entry.Name));
    }

    [Fact]
    public void SearchAndTabChangesDoNotEvaluateTargets()
    {
        var catalog = new TargetCatalogPresentationCache<string>(
        [
            Entry("water", "Water", "/water", "Looping Emote", TargetBehavior.LoopingEmote),
            Entry("sit", "Sit", "/lounge", "Persistent Pose", TargetBehavior.PersistentPose),
        ]);
        var evaluations = 0;

        var loops = catalog.GetResults(TargetSelectionCategory.LoopingEmotes, string.Empty, 50);
        var poses = catalog.GetResults(TargetSelectionCategory.PosesAndIdles, string.Empty, 50);
        var search = catalog.GetResults(TargetSelectionCategory.Advanced, "water", 50);

        Assert.Equal(0, evaluations);
        Assert.Single(loops);
        Assert.Single(poses);
        Assert.Single(search);

        Evaluate(loops[0].Target);
        Assert.Equal(1, evaluations);

        void Evaluate(string _) => evaluations++;
    }

    [Fact]
    public void RepeatedQueriesReuseTheSameResultList()
    {
        var catalog = new TargetCatalogPresentationCache<string>(
        [Entry("water", "Water", "/water", "Looping Emote", TargetBehavior.LoopingEmote)]);

        var first = catalog.GetResults(TargetSelectionCategory.LoopingEmotes, "wat", 50);
        var second = catalog.GetResults(TargetSelectionCategory.LoopingEmotes, "wat", 50);

        Assert.Same(first, second);
    }

    private static TargetCatalogPresentation<string> Entry(
        string id,
        string name,
        string command,
        string behaviorName,
        TargetBehavior behavior)
        => new(id, id, name, command, command, behavior, TargetContext.Emote, behaviorName, "Emote");
}
