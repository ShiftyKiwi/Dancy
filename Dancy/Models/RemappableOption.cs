using System.Collections.Generic;

namespace Dancy.Core.Models;

public class RemappableOption
{
    public string GroupName { get; set; } = string.Empty;
    public string OptionName { get; set; } = string.Empty;

    public bool IsSafeToRemap { get; set; }

    public List<ParsedEmoteOverride> Entries { get; set; } = new();
}
