using System.Collections.Generic;

namespace Dancy.Core.Models;

public sealed class PapSourceGroup
{
    public string SourcePap { get; set; } = string.Empty;

    // Alle GamePaths, die auf diese PAP zeigen
    public List<string> GamePaths { get; set; } = new();
}
