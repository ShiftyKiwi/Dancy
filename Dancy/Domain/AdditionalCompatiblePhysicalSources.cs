using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dancy.Core.Models;

namespace Dancy.Domain;

/// <summary>
/// Resolves physical PAP choices independently from logical output mappings.
/// </summary>
public sealed record AdditionalCompatiblePhysicalSource(string Key, ParsedEmoteOverride Source)
{
    public string DisplayName
    {
        get
        {
            var origin = Source.PhysicalSourceOrigin;
            var race = origin.IsKnown ? origin.DisplayName : "Mod-relative physical source";
            return $"{race} / {Path.GetFileName(Source.ModdedPapPath)}";
        }
    }
}

public static class AdditionalCompatiblePhysicalSources
{
    public static IReadOnlyList<AdditionalCompatiblePhysicalSource> Discover(IEnumerable<ParsedEmoteOverride> sourceEntries)
        => sourceEntries
            .Where(entry => entry.MappingOrigin == SourceMappingOrigin.ModProvided
                            && entry.AppliesTo.Phase == AnimationPhase.Loop
                            && !string.IsNullOrWhiteSpace(entry.ModdedPapPath))
            .GroupBy(entry => GamePathIdentity.Normalize(entry.ModdedPapPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(entry => entry.PhysicalSourceOrigin.Code, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.GamePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(entry => entry.PhysicalSourceOrigin.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ModdedPapPath, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new AdditionalCompatiblePhysicalSource(GamePathIdentity.Normalize(entry.ModdedPapPath), entry))
            .ToList();

    public static bool TryResolve(
        IReadOnlyList<AdditionalCompatiblePhysicalSource> candidates,
        string? selectedKey,
        out AdditionalCompatiblePhysicalSource? physicalSource,
        out string error)
    {
        physicalSource = null;
        error = string.Empty;
        if (candidates.Count == 0)
        {
            error = "This option has no source-provided Loop PAP available as a physical source.";
            return false;
        }

        if (candidates.Count == 1)
        {
            physicalSource = candidates[0];
            return true;
        }

        if (string.IsNullOrWhiteSpace(selectedKey))
        {
            error = "Choose the physical source PAP for this additional logical mapping.";
            return false;
        }

        physicalSource = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, selectedKey, StringComparison.OrdinalIgnoreCase));
        if (physicalSource is not null)
            return true;

        error = "The selected physical source PAP is no longer available for this option.";
        return false;
    }
}
