using System;
using System.Collections.Generic;
using System.Linq;

namespace Dancy.Animation;

/// <summary>
/// A user-authored request to omit named transform tracks from one generated
/// replacement animation. Names are retained because track indices are local
/// to a PAP binding and must be resolved for each generation.
/// </summary>
[Serializable]
public sealed class BoneMask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Bone Mask";
    public bool Enabled { get; set; } = true;
    public BoneMaskAnimationAssociation Association { get; set; } = new();
    public List<string> BoneNames { get; set; } = [];
}

/// <summary>
/// Identifies the Dancy replacement selected when a mask was authored. The
/// paths are game paths, never transient native track indices or file times.
/// </summary>
[Serializable]
public sealed class BoneMaskAnimationAssociation
{
    public string SourceOverrideGamePath { get; set; } = string.Empty;
    public string ReplacementGamePath { get; set; } = string.Empty;
    public string MotionIdentity { get; set; } = string.Empty;
    public string TargetSkeletonIdentity { get; set; } = string.Empty;
}

public static class BoneMaskNames
{
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? boneNames)
    {
        if (boneNames is null)
            return Array.Empty<string>();

        return boneNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
