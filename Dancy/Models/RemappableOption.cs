using System.Collections.Generic;

namespace Dancy.Core.Models;

public class RemappableOption
{
    public string GroupName { get; set; } = string.Empty;
    public string OptionName { get; set; } = string.Empty;

    // OBSOLET: wird nicht mehr als technische Entscheidung genutzt
    public bool IsSafeToRemap { get; set; }

    public List<ParsedEmoteOverride> Entries { get; set; } = new();

    // ✅ NEU – technische Wahrheit über Quellen
    public List<PapSourceGroup> PapSources { get; set; } = new();
}
