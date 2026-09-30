using Dancy.Domain;

namespace Dancy.Core.Models;

public class ParsedEmoteOverride
{
    public string GroupName { get; set; } = string.Empty;
    public string OptionName { get; set; } = string.Empty;

    public string GamePath { get; set; } = string.Empty;
    public string ModdedPapPath { get; set; } = string.Empty;
    public string PhysicalSourceGamePath { get; set; } = string.Empty;
    public SourceMappingOrigin MappingOrigin { get; set; } = SourceMappingOrigin.ModProvided;
    public string EmoteName { get; set; } = string.Empty;
    public string EmoteCommand { get; set; } = string.Empty;
    public uint EmoteRowId { get; set; }

    public GamePathIdentity AppliesTo => GamePathIdentity.Parse(GamePath);

    public CharacterPathIdentity PapOrigin => CharacterPathIdentity.FromGamePath(ModdedPapPath);

    public CharacterPathIdentity PhysicalSourceOrigin
        => PapOrigin.IsKnown
            ? PapOrigin
            : CharacterPathIdentity.FromGamePath(PhysicalSourceGamePath);
}
