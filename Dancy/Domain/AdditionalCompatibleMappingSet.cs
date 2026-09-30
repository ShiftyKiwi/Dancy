using System;
using System.Collections.Generic;
using System.Linq;
using Dancy.Core.Models;

namespace Dancy.Domain;

/// <summary>
/// Holds explicit, option-local logical race mappings selected by the user.
/// It has no persistence or global compatibility behavior.
/// </summary>
public sealed class AdditionalCompatibleMappingSet
{
    private readonly Dictionary<string, ParsedEmoteOverride> mappings = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ParsedEmoteOverride> Values
        => mappings.Values.OrderBy(mapping => mapping.AppliesTo.Character.Code, StringComparer.OrdinalIgnoreCase).ToList();

    public bool TryAdd(
        ParsedEmoteOverride physicalSource,
        CharacterPathIdentity logicalCharacter,
        IEnumerable<ParsedEmoteOverride> modProvidedMappings,
        out ParsedEmoteOverride? mapping,
        out string error)
    {
        mapping = null;
        error = string.Empty;
        if (physicalSource.MappingOrigin != SourceMappingOrigin.ModProvided)
        {
            error = "Choose a source-provided Loop mapping as the physical animation source.";
            return false;
        }

        if (physicalSource.AppliesTo.Phase != AnimationPhase.Loop)
        {
            error = "Only Loop source paths can receive an additional compatible mapping.";
            return false;
        }

        if (!logicalCharacter.IsKnown)
        {
            error = "Dancy could not determine a playable race identity for the additional mapping.";
            return false;
        }

        if (!GamePathIdentity.TryReplaceCharacter(physicalSource.GamePath, logicalCharacter, out var logicalPath, out error))
            return false;

        var represented = modProvidedMappings
            .Concat(mappings.Values)
            .Any(existing => string.Equals(existing.AppliesTo.Character.Code, logicalCharacter.Code, StringComparison.OrdinalIgnoreCase));
        if (represented)
        {
            error = $"{logicalCharacter.DisplayName} is already represented by this source option.";
            return false;
        }

        var physicalSourceGamePath = physicalSource.GamePath;
        if (physicalSource.PapOrigin.IsKnown
            && GamePathIdentity.TryReplaceCharacter(physicalSource.GamePath, physicalSource.PapOrigin, out var physicalLogicalPath, out _))
        {
            physicalSourceGamePath = physicalLogicalPath;
        }

        mapping = new ParsedEmoteOverride
        {
            GroupName = physicalSource.GroupName,
            OptionName = physicalSource.OptionName,
            GamePath = logicalPath,
            ModdedPapPath = physicalSource.ModdedPapPath,
            PhysicalSourceGamePath = physicalSourceGamePath,
            MappingOrigin = SourceMappingOrigin.UserAddedCompatible,
            EmoteName = physicalSource.EmoteName,
            EmoteCommand = physicalSource.EmoteCommand,
            EmoteRowId = physicalSource.EmoteRowId,
        };
        mappings.Add(mapping.GamePath, mapping);
        return true;
    }

    public bool Remove(string logicalGamePath)
        => mappings.Remove(GamePathIdentity.Normalize(logicalGamePath));

    public void Clear()
        => mappings.Clear();
}
