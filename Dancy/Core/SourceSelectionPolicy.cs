using System;
using System.Collections.Generic;
using System.Linq;
using Dancy.Core.Models;
using Dancy.Domain;

namespace Dancy.Core;

/// <summary>
/// Keeps normal Dancy overrides constrained to loop PAPs. Start/end assets remain
/// visible in the UI as context, but cannot accidentally enter a loop override.
/// </summary>
public static class SourceSelectionPolicy
{
    public static IReadOnlySet<string> DefaultLoopGamePaths(IEnumerable<ParsedEmoteOverride> entries)
        => entries
            .Where(entry => entry.AppliesTo.Phase == AnimationPhase.Loop)
            .Select(entry => entry.GamePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ParsedEmoteOverride> SelectedLoopEntries(
        IEnumerable<ParsedEmoteOverride> entries,
        ISet<string> selectedGamePaths)
        => entries
            .Where(entry => entry.AppliesTo.Phase == AnimationPhase.Loop
                         && selectedGamePaths.Contains(entry.GamePath))
            .ToList();

    public static IReadOnlyList<ParsedEmoteOverride> OrderForDisplay(IEnumerable<ParsedEmoteOverride> entries)
        => entries
            .OrderBy(entry => PhaseOrder(entry.AppliesTo.Phase))
            .ThenBy(entry => entry.AppliesTo.Character.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static int PhaseOrder(AnimationPhase phase)
        => phase switch
        {
            AnimationPhase.Loop => 0,
            AnimationPhase.Start => 1,
            AnimationPhase.End => 2,
            _ => 3,
        };
}
