using Dancy.Core.Models;
using Dancy.Domain;
using Xunit;

namespace Dancy.Tests;

public class AdditionalCompatibleMappingTests
{
    [Fact]
    public void CreatesAnExplicitUserAddedLogicalRacePathWithoutChangingThePhysicalPap()
    {
        var mappings = new AdditionalCompatibleMappingSet();
        var source = TreadmillWalkC0101();

        var added = mappings.TryAdd(source, Character("c0501"), new[] { source }, out var mapping, out var error);

        Assert.True(added, error);
        Assert.NotNull(mapping);
        Assert.Equal("chara/human/c0501/animation/a0001/bt_common/emote/loop_emot11_loop.pap", mapping.GamePath);
        Assert.Equal(source.ModdedPapPath, mapping.ModdedPapPath);
        Assert.Equal(source.GamePath, mapping.PhysicalSourceGamePath);
        Assert.Equal(SourceMappingOrigin.UserAddedCompatible, mapping.MappingOrigin);
        Assert.Equal("c0501", mapping.AppliesTo.Character.Code);
        Assert.Equal("c0101", mapping.PhysicalSourceOrigin.Code);
    }

    [Fact]
    public void RejectsRaceAlreadyProvidedByTheModOrAlreadyAddedByTheUser()
    {
        var mappings = new AdditionalCompatibleMappingSet();
        var source = TreadmillWalkC0101();

        Assert.False(mappings.TryAdd(source, Character("c0101"), new[] { source }, out _, out var providedError));
        Assert.Contains("already represented", providedError, StringComparison.OrdinalIgnoreCase);

        Assert.True(mappings.TryAdd(source, Character("c0501"), new[] { source }, out var first, out var firstError), firstError);
        Assert.NotNull(first);
        Assert.False(mappings.TryAdd(source, Character("c0501"), new[] { source }, out _, out var duplicateError));
        Assert.Contains("already represented", duplicateError, StringComparison.OrdinalIgnoreCase);
        Assert.Single(mappings.Values);
    }

    [Fact]
    public void RemovesAndRecreatesOnlyTheRequestedAdditionalMapping()
    {
        var mappings = new AdditionalCompatibleMappingSet();
        var source = TreadmillWalkC0101();

        Assert.True(mappings.TryAdd(source, Character("c0501"), new[] { source }, out var first, out var error), error);
        Assert.NotNull(first);
        Assert.True(mappings.Remove(first.GamePath));
        Assert.Empty(mappings.Values);

        Assert.True(mappings.TryAdd(source, Character("c0501"), new[] { source }, out var recreated, out error), error);
        Assert.NotNull(recreated);
        Assert.Single(mappings.Values);
    }

    [Fact]
    public void RejectsAnInvalidLogicalSourcePath()
    {
        var mappings = new AdditionalCompatibleMappingSet();
        var invalid = new ParsedEmoteOverride
        {
            GroupName = "Treadmill - /breathcontrol",
            OptionName = "Walk",
            GamePath = "chara/action/emote/loop_emot11_loop.pap",
            ModdedPapPath = "files/chara/human/c0101/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
        };

        Assert.False(mappings.TryAdd(invalid, Character("c0501"), new[] { invalid }, out _, out var error));
        Assert.Contains("race identity", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoesNotExpandAnyOtherRaceWhenOneMappingIsAdded()
    {
        var mappings = new AdditionalCompatibleMappingSet();
        var source = TreadmillWalkC0101();

        Assert.True(mappings.TryAdd(source, Character("c0501"), new[] { source }, out _, out var error), error);

        Assert.Equal(new[] { "c0501" }, mappings.Values.Select(mapping => mapping.AppliesTo.Character.Code));
        Assert.DoesNotContain(mappings.Values, mapping => mapping.AppliesTo.Character.Code == "c0301");
        Assert.DoesNotContain(mappings.Values, mapping => mapping.AppliesTo.Character.Code == "c0701");
    }

    [Fact]
    public void UsesThePhysicalPapRaceWhenTheCreatorLogicalRowUsesAnotherRace()
    {
        var mappings = new AdditionalCompatibleMappingSet();
        var source = TreadmillWalkC0101();
        source.GamePath = "chara/human/c0801/animation/a0001/bt_common/emote/loop_emot11_loop.pap";

        Assert.True(mappings.TryAdd(source, Character("c0501"), new[] { source }, out var mapping, out var error), error);
        Assert.NotNull(mapping);
        Assert.Equal("c0101", mapping.PhysicalSourceOrigin.Code);
        Assert.Equal("chara/human/c0101/animation/a0001/bt_common/emote/loop_emot11_loop.pap", mapping.PhysicalSourceGamePath);
    }

    [Fact]
    public void StableIdsAndDescriptionsDistinguishUserAddedMappingsFromCreatorMappings()
    {
        var source = TreadmillWalkC0101();
        var mappings = new AdditionalCompatibleMappingSet();
        Assert.True(mappings.TryAdd(source, Character("c0501"), new[] { source }, out var added, out var error), error);
        Assert.NotNull(added);

        var providedPlan = CreateWaterPlan(new OverridePlanSource(
            source.GamePath,
            source.ModdedPapPath,
            SourceMappingOrigin.ModProvided,
            source.GamePath));
        var addedPlan = CreateWaterPlan(new OverridePlanSource(
            added.GamePath,
            added.ModdedPapPath,
            added.MappingOrigin,
            added.PhysicalSourceGamePath));

        Assert.True(providedPlan.IsValid);
        Assert.True(addedPlan.IsValid);
        Assert.NotEqual(providedPlan.OverrideId, addedPlan.OverrideId);
        Assert.Contains("User-added compatible mapping", addedPlan.Description, StringComparison.Ordinal);
        Assert.Contains("Elezen Male (c0501)", addedPlan.Description, StringComparison.Ordinal);
        Assert.Contains("Midlander Male (c0101)", addedPlan.Description, StringComparison.Ordinal);
        Assert.Equal(SourceMappingOrigin.ModProvided, Assert.Single(providedPlan.SourceMappings).MappingOrigin);
        Assert.Equal(SourceMappingOrigin.UserAddedCompatible, Assert.Single(addedPlan.SourceMappings).MappingOrigin);
    }

    [Fact]
    public void CreatorProvidedTreadmillAndBenchMappingsRemainSourceProvided()
    {
        var treadmill = TreadmillWalkC0101();
        var bench = BenchC0501();
        var treadmillPath = treadmill.GamePath;
        var treadmillPap = treadmill.ModdedPapPath;
        var benchPath = bench.GamePath;
        var benchPap = bench.ModdedPapPath;

        var mappings = new AdditionalCompatibleMappingSet();
        Assert.True(mappings.TryAdd(treadmill, Character("c0501"), new[] { treadmill }, out var added, out var error), error);
        Assert.NotNull(added);

        var treadmillPlan = CreateWaterPlan(new OverridePlanSource(
            treadmill.GamePath,
            treadmill.ModdedPapPath,
            treadmill.MappingOrigin,
            treadmill.PhysicalSourceGamePath));
        var benchPlan = CreateWaterPlan(new OverridePlanSource(
            bench.GamePath,
            bench.ModdedPapPath,
            bench.MappingOrigin,
            bench.PhysicalSourceGamePath));

        Assert.Equal(treadmillPath, treadmill.GamePath);
        Assert.Equal(treadmillPap, treadmill.ModdedPapPath);
        Assert.Equal(benchPath, bench.GamePath);
        Assert.Equal(benchPap, bench.ModdedPapPath);
        Assert.Equal(SourceMappingOrigin.ModProvided, Assert.Single(treadmillPlan.SourceMappings).MappingOrigin);
        Assert.Equal(SourceMappingOrigin.ModProvided, Assert.Single(benchPlan.SourceMappings).MappingOrigin);
        Assert.Equal(SourceMappingOrigin.UserAddedCompatible, added.MappingOrigin);
    }

    [Fact]
    public void InfersOnePhysicalSourceWithoutSelectingAnyCreatorProvidedOutput()
    {
        var provided = TreadmillWalkSourceRows();
        var physicalCandidates = AdditionalCompatiblePhysicalSources.Discover(provided);

        Assert.Single(physicalCandidates);
        Assert.True(AdditionalCompatiblePhysicalSources.TryResolve(physicalCandidates, selectedKey: null, out var physicalSource, out var error), error);
        Assert.NotNull(physicalSource);
        Assert.Equal("c0101", physicalSource.Source.PhysicalSourceOrigin.Code);

        var additions = new AdditionalCompatibleMappingSet();
        Assert.True(additions.TryAdd(physicalSource.Source, Character("c0501"), provided, out var added, out error), error);
        Assert.NotNull(added);

        var selectedCreatorRows = Array.Empty<ParsedEmoteOverride>();
        var finalSources = selectedCreatorRows.Concat(additions.Values)
            .Select(ToPlanSource)
            .ToArray();
        var plan = CreateWaterPlan(finalSources, "c0101", "c0501");

        Assert.Single(finalSources);
        Assert.True(plan.IsValid);
        Assert.Equal(new[] { "c0501" }, plan.SourceMappings.Select(source => GamePathIdentity.Parse(source.GamePath).Character.Code));
        Assert.Equal(new[] { "c0501" }, plan.PlannedMappings.Keys.Select(path => GamePathIdentity.Parse(path).Character.Code));
    }

    [Fact]
    public void SupportsMultipleUserAddedMappingsWithNoCreatorProvidedOutputRows()
    {
        var provided = TreadmillWalkSourceRows();
        var physicalCandidates = AdditionalCompatiblePhysicalSources.Discover(provided);
        Assert.True(AdditionalCompatiblePhysicalSources.TryResolve(physicalCandidates, selectedKey: null, out var physicalSource, out var error), error);
        Assert.NotNull(physicalSource);

        var additions = new AdditionalCompatibleMappingSet();
        Assert.True(additions.TryAdd(physicalSource.Source, Character("c0501"), provided, out _, out error), error);
        Assert.True(additions.TryAdd(physicalSource.Source, Character("c0701"), provided, out _, out error), error);

        var plan = CreateWaterPlan(additions.Values.Select(ToPlanSource).ToArray(), "c0101", "c0501", "c0701");

        Assert.True(plan.IsValid);
        Assert.Equal(new[] { "c0501", "c0701" }, plan.SourceMappings
            .Select(source => GamePathIdentity.Parse(source.GamePath).Character.Code)
            .OrderBy(code => code));
        Assert.Equal(new[] { "c0501", "c0701" }, plan.PlannedMappings.Keys
            .Select(path => GamePathIdentity.Parse(path).Character.Code)
            .OrderBy(code => code));
    }

    [Fact]
    public void RequiresExplicitPhysicalSourceWhenAnOptionHasDistinctPaps()
    {
        var provided = new[] { TreadmillWalkC0101(), TreadmillWalkC0801WithOwnPap() };
        var physicalCandidates = AdditionalCompatiblePhysicalSources.Discover(provided);

        Assert.Equal(2, physicalCandidates.Count);
        Assert.False(AdditionalCompatiblePhysicalSources.TryResolve(physicalCandidates, selectedKey: null, out _, out var missingSelection));
        Assert.Contains("Choose the physical source", missingSelection, StringComparison.OrdinalIgnoreCase);

        var selected = physicalCandidates.Single(candidate => candidate.Source.PhysicalSourceOrigin.Code == "c0801");
        Assert.True(AdditionalCompatiblePhysicalSources.TryResolve(physicalCandidates, selected.Key, out var physicalSource, out var error), error);
        Assert.NotNull(physicalSource);
        Assert.Equal("c0801", physicalSource.Source.PhysicalSourceOrigin.Code);

        var additions = new AdditionalCompatibleMappingSet();
        Assert.True(additions.TryAdd(physicalSource.Source, Character("c0501"), provided, out var added, out error), error);
        Assert.NotNull(added);
        var plan = CreateWaterPlan(new[] { ToPlanSource(added) }, "c0501", "c0801");

        Assert.True(plan.IsValid);
        Assert.Equal("c0801", added.PhysicalSourceOrigin.Code);
        Assert.Equal(new[] { "c0501" }, plan.PlannedMappings.Keys.Select(path => GamePathIdentity.Parse(path).Character.Code));
    }

    [Fact]
    public void RemovingTheLastUserAddedMappingLeavesNoValidOutputSelection()
    {
        var provided = TreadmillWalkSourceRows();
        var physicalCandidates = AdditionalCompatiblePhysicalSources.Discover(provided);
        Assert.True(AdditionalCompatiblePhysicalSources.TryResolve(physicalCandidates, selectedKey: null, out var physicalSource, out var error), error);
        Assert.NotNull(physicalSource);

        var additions = new AdditionalCompatibleMappingSet();
        Assert.True(additions.TryAdd(physicalSource.Source, Character("c0501"), provided, out var added, out error), error);
        Assert.NotNull(added);
        Assert.True(additions.Remove(added.GamePath));

        var plan = CreateWaterPlan(additions.Values.Select(ToPlanSource).ToArray(), "c0501");
        Assert.Empty(additions.Values);
        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, message => message.Contains("Select at least one source", StringComparison.OrdinalIgnoreCase));
    }

    private static ParsedEmoteOverride TreadmillWalkC0101()
        => new()
        {
            GroupName = "Treadmill - /breathcontrol",
            OptionName = "Walk",
            GamePath = "chara/human/c0101/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
            ModdedPapPath = "files/chara/human/c0101/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
            PhysicalSourceGamePath = "chara/human/c0101/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
            MappingOrigin = SourceMappingOrigin.ModProvided,
            EmoteName = "Breath Control",
            EmoteCommand = "/breathcontrol",
        };

    private static ParsedEmoteOverride BenchC0501()
        => new()
        {
            GroupName = "Bench Press - /pushups",
            OptionName = "Enable",
            GamePath = "chara/human/c0501/animation/a0001/bt_common/emote/loop_emot08_loop.pap",
            ModdedPapPath = "files/chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_loop.pap",
            PhysicalSourceGamePath = "chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_loop.pap",
            MappingOrigin = SourceMappingOrigin.ModProvided,
            EmoteName = "Push-ups",
            EmoteCommand = "/pushups",
        };

    private static ParsedEmoteOverride TreadmillWalkC0801WithOwnPap()
        => new()
        {
            GroupName = "Treadmill - /breathcontrol",
            OptionName = "Walk",
            GamePath = "chara/human/c0801/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
            ModdedPapPath = "files/chara/human/c0801/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
            PhysicalSourceGamePath = "chara/human/c0801/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
            MappingOrigin = SourceMappingOrigin.ModProvided,
            EmoteName = "Breath Control",
            EmoteCommand = "/breathcontrol",
        };

    private static ParsedEmoteOverride[] TreadmillWalkSourceRows()
    {
        var c0101 = TreadmillWalkC0101();
        var c0801 = TreadmillWalkC0101();
        c0801.GamePath = "chara/human/c0801/animation/a0001/bt_common/emote/loop_emot11_loop.pap";
        var c0901 = TreadmillWalkC0101();
        c0901.GamePath = "chara/human/c0901/animation/a0001/bt_common/emote/loop_emot11_loop.pap";
        var c1101 = TreadmillWalkC0101();
        c1101.GamePath = "chara/human/c1101/animation/a0001/bt_common/emote/loop_emot11_loop.pap";
        return new[] { c0101, c0801, c0901, c1101 };
    }

    private static CharacterPathIdentity Character(string code)
    {
        Assert.True(CharacterPathIdentity.TryGet(code, out var character));
        return character;
    }

    private static OverridePlan CreateWaterPlan(OverridePlanSource source)
        => CreateWaterPlan(
            new[] { source },
            GamePathIdentity.Parse(source.GamePath).Character.Code);

    private static OverridePlan CreateWaterPlan(IReadOnlyList<OverridePlanSource> sources, params string[] targetCharacters)
        => OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = "warrior-of-lift-1.0.0",
            SourceGroupName = "Treadmill - /breathcontrol",
            SourceOptionName = "Walk",
            SourceAnimationName = "Breath Control",
            SourceAnimationCommand = "/breathcontrol",
            TargetTimelineKey = "emote_sp/sp60_loop",
            TargetName = "Water",
            TargetCommand = "/water",
            Sources = sources,
            TargetGamePaths = targetCharacters
                .Select(character => $"chara/human/{character}/animation/a0001/bt_common/emote_sp/sp60_loop.pap")
                .ToArray(),
        });

    private static OverridePlanSource ToPlanSource(ParsedEmoteOverride entry)
        => new(
            entry.GamePath,
            entry.ModdedPapPath,
            entry.MappingOrigin,
            entry.PhysicalSourceGamePath);
}
