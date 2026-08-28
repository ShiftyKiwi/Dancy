using System;
using System.Collections.Generic;

namespace Dancy.Domain;

public readonly record struct CharacterPathIdentity(string Code, string Race, string Sex)
{
    private static readonly IReadOnlyDictionary<string, (string Race, string Sex)> KnownCharacters =
        new Dictionary<string, (string Race, string Sex)>(StringComparer.OrdinalIgnoreCase)
        {
            ["c0101"] = ("Midlander", "Male"),
            ["c0201"] = ("Midlander", "Female"),
            ["c0301"] = ("Highlander", "Male"),
            ["c0401"] = ("Highlander", "Female"),
            ["c0501"] = ("Elezen", "Male"),
            ["c0601"] = ("Elezen", "Female"),
            ["c0701"] = ("Lalafell", "Male"),
            ["c0801"] = ("Lalafell", "Female"),
            ["c0901"] = ("Roegadyn", "Male"),
            ["c1001"] = ("Roegadyn", "Female"),
            ["c1101"] = ("Miqo'te", "Male"),
            ["c1201"] = ("Miqo'te", "Female"),
            ["c1301"] = ("Au Ra", "Male"),
            ["c1401"] = ("Au Ra", "Female"),
            ["c1501"] = ("Hrothgar", "Male"),
            ["c1601"] = ("Hrothgar", "Female"),
            ["c1701"] = ("Viera", "Male"),
            ["c1801"] = ("Viera", "Female"),
        };

    public bool IsKnown => !string.IsNullOrEmpty(Code) && !string.IsNullOrEmpty(Race);

    public string DisplayName => IsKnown ? $"{Race} {Sex} ({Code})" : "Unknown rig";

    public static CharacterPathIdentity FromGamePath(string? gamePath)
    {
        var normalized = GamePathIdentity.Normalize(gamePath);
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!KnownCharacters.TryGetValue(segment, out var identity))
                continue;

            return new CharacterPathIdentity(segment.ToLowerInvariant(), identity.Race, identity.Sex);
        }

        return new CharacterPathIdentity(string.Empty, string.Empty, string.Empty);
    }
}
