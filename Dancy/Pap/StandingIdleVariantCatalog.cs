using System;
using System.Collections.Generic;
using System.Linq;
using Dancy.Domain;

namespace Dancy.Pap;

/// <summary>
/// Per-variant Standing Idle preflight. A compatible count never grants support
/// to a variant whose actual canonical PAP topology differs from the proven one.
/// </summary>
public sealed record StandingIdleVariantStatus(
    string GamePath,
    CharacterPathIdentity Character,
    bool IsSupported,
    string Reason);

public sealed class StandingIdleVariantSummary
{
    public IReadOnlyList<StandingIdleVariantStatus> Variants { get; init; } = Array.Empty<StandingIdleVariantStatus>();
    public int TotalVariantCount => Variants.Count;
    public int SupportedVariantCount => Variants.Count(variant => variant.IsSupported);
    public bool HasSupportedVariants => SupportedVariantCount > 0;
    public bool AllVariantsSupported => TotalVariantCount > 0 && SupportedVariantCount == TotalVariantCount;
    public IReadOnlyList<string> SupportedGamePaths => Variants
        .Where(variant => variant.IsSupported)
        .Select(variant => variant.GamePath)
        .ToList();
}

public static class StandingIdleVariantCatalog
{
    public static StandingIdleVariantSummary Analyze(IEnumerable<PapTargetInspection> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var variants = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.GamePath))
            .GroupBy(target => GamePathIdentity.Normalize(target.GamePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(target => GamePathIdentity.Parse(target.GamePath).Character.Code, StringComparer.OrdinalIgnoreCase)
            .Select(target =>
            {
                var supported = PapCompatibilityPreflight.IsSupportedStandingIdleTarget(target, out var reason);
                return new StandingIdleVariantStatus(target.GamePath, GamePathIdentity.Parse(target.GamePath).Character, supported, reason);
            })
            .ToList();

        return new StandingIdleVariantSummary { Variants = variants };
    }
}
