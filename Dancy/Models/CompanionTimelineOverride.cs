using System;

namespace Dancy.Core.Models;

/// <summary>
/// A non-PAP file redirect from the same Penumbra option as an animation PAP.
/// Companion action timelines can provide explicit source-motion selection evidence.
/// </summary>
public sealed record CompanionTimelineOverride(string GamePath, string ModdedTimelinePath)
{
    public bool IsActionTimeline
        => GamePath.Replace('\\', '/').Contains("chara/action/", StringComparison.OrdinalIgnoreCase);
}
