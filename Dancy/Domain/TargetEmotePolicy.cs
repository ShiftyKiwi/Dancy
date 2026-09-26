namespace Dancy.Domain;

public static class TargetEmotePolicy
{
    public static bool IsSelectable(TargetBehavior behavior)
        => behavior is TargetBehavior.LoopingEmote or TargetBehavior.PersistentPose or TargetBehavior.OneShot or TargetBehavior.Unknown;

    public static bool IsVisible(TargetSelectionCategory category, TargetBehavior behavior)
        => TargetSemantics.IsVisible(category, behavior);

    /// <summary>
    /// A non-empty target search spans the visible target categories, but never
    /// promotes raw transition assets into normal selection.
    /// </summary>
    public static bool IsSearchResult(TargetBehavior behavior)
        => behavior != TargetBehavior.Transition;
}
