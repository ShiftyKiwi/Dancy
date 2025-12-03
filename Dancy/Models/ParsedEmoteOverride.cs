namespace Dancy.Core.Models;

public class ParsedEmoteOverride
{
    public string GroupName { get; set; } = string.Empty;
    public string OptionName { get; set; } = string.Empty;

    public string GamePath { get; set; } = string.Empty;
    public string ModdedPapPath { get; set; } = string.Empty;
    public string NewPapPath { get; set; } = string.Empty;

    public string EmoteName { get; set; } = string.Empty;
    public string EmoteCommand { get; set; } = string.Empty;
    public uint EmoteRowId { get; set; }
}
