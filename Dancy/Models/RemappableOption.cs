using System;
using System.Collections.Generic;
using System.Linq;
using Dancy.Domain;

namespace Dancy.Core.Models;

public class RemappableOption
{
    public string GroupName { get; set; } = string.Empty;
    public string OptionName { get; set; } = string.Empty;
    public List<ParsedEmoteOverride> Entries { get; set; } = new();
    public List<PapSourceGroup> PapSources { get; set; } = new();
    public List<CompanionTimelineOverride> CompanionTimelines { get; set; } = new();

    public IReadOnlyList<LogicalSourceAnimation> LogicalAnimations
        => Entries
            .GroupBy(entry => (entry.EmoteRowId, entry.EmoteName, entry.EmoteCommand))
            .Select(group => new LogicalSourceAnimation(
                group.Key.EmoteName,
                group.Key.EmoteCommand,
                group.Key.EmoteRowId,
                group.OrderBy(entry => entry.AppliesTo.Phase)
                    .ThenBy(entry => entry.AppliesTo.Character.Code, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.GamePath, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();

    public IReadOnlyList<ParsedEmoteOverride> LoopEntries
        => Entries.Where(entry => entry.AppliesTo.Phase == AnimationPhase.Loop).ToList();
}

public sealed record LogicalSourceAnimation(
    string Name,
    string Command,
    uint RowId,
    IReadOnlyList<ParsedEmoteOverride> Paths)
{
    public IReadOnlyList<AnimationPhase> Phases
        => Paths.Select(path => path.AppliesTo.Phase).Distinct().OrderBy(phase => phase).ToList();

    public int PhysicalPapCount
        => Paths.Select(path => path.ModdedPapPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}
