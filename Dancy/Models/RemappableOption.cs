using System.Collections.Generic;

namespace Dancy.Core.Models;

public class RemappableOption
{
    public string GroupName { get; set; } = string.Empty;
    public string OptionName { get; set; } = string.Empty;
    public List<ParsedEmoteOverride> Entries { get; set; } = new();
    public List<PapSourceGroup> PapSources { get; set; } = new();
}
