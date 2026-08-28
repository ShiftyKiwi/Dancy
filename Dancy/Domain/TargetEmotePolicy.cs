namespace Dancy.Domain;

/// <summary>
/// Keeps normal override selection conservative. Unknown timeline classifications are not
/// selectable unless the developer/non-loop switch is explicitly enabled.
/// </summary>
public static class TargetEmotePolicy
{
    public static bool IsSelectable(bool includeNonLoopTargets, bool isLoopCapable)
        => includeNonLoopTargets || isLoopCapable;
}
