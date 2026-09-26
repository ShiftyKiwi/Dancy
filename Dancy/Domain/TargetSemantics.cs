using System;

namespace Dancy.Domain;

/// <summary>
/// Describes how long the game normally keeps a target animation active. This
/// is intentionally independent from the filename-derived PAP phase.
/// </summary>
public enum TargetBehavior
{
    Unknown,
    LoopingEmote,
    PersistentPose,
    OneShot,
    Transition,
}

/// <summary>
/// Describes the character state that owns a target animation when the game
/// exposes one. It is not a claim about PAP structure or rig compatibility.
/// </summary>
public enum TargetContext
{
    Unknown,
    Emote,
    StandingIdle,
    GroundSit,
    ChairSit,
    SleepOrLie,
    OtherPersistentPose,
}

public enum TargetSelectionCategory
{
    LoopingEmotes,
    PosesAndIdles,
    Advanced,
}

public static class TargetSemantics
{
    public static TargetBehavior Classify(
        bool hasCommand,
        bool hasLoopTimeline,
        TargetContext context,
        AnimationPhase primaryPhase)
    {
        if (context is not TargetContext.Unknown and not TargetContext.Emote)
            return TargetBehavior.PersistentPose;
        if (hasLoopTimeline)
            return TargetBehavior.LoopingEmote;
        if (hasCommand)
            return TargetBehavior.OneShot;
        return primaryPhase is AnimationPhase.Start or AnimationPhase.End
            ? TargetBehavior.Transition
            : TargetBehavior.Unknown;
    }

    public static bool IsVisible(TargetSelectionCategory category, TargetBehavior behavior)
        => category switch
        {
            TargetSelectionCategory.LoopingEmotes => behavior == TargetBehavior.LoopingEmote,
            TargetSelectionCategory.PosesAndIdles => behavior == TargetBehavior.PersistentPose,
            TargetSelectionCategory.Advanced => behavior is TargetBehavior.OneShot or TargetBehavior.Unknown,
            _ => false,
        };

    public static string DisplayName(TargetBehavior behavior)
        => behavior switch
        {
            TargetBehavior.LoopingEmote => "Looping Emote",
            TargetBehavior.PersistentPose => "Persistent Pose",
            TargetBehavior.OneShot => "One-shot",
            TargetBehavior.Transition => "Transition",
            _ => "Unknown",
        };

    public static string DisplayName(TargetContext context)
        => context switch
        {
            TargetContext.Emote => "Emote",
            TargetContext.StandingIdle => "Standing Idle",
            TargetContext.GroundSit => "Ground Sit",
            TargetContext.ChairSit => "Chair Sit",
            TargetContext.SleepOrLie => "Sleeping / Lying",
            TargetContext.OtherPersistentPose => "Other Persistent Pose",
            _ => "Unknown",
        };

    public static string? BehaviorNotice(TargetBehavior behavior)
        => behavior switch
        {
            TargetBehavior.PersistentPose => "This animation remains active while the character remains in the associated state.",
            TargetBehavior.OneShot => "This is a one-shot target. A looping source may be interrupted when the target animation completes.",
            TargetBehavior.Unknown => "Dancy could not determine this target's playback behavior. Structural compatibility does not guarantee how long the game will keep it active.",
            _ => null,
        };

    public static bool MatchesSearch(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return Array.Exists(fields, field => !string.IsNullOrWhiteSpace(field)
            && field.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
