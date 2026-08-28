using System;
using System.Collections.Generic;
using System.Linq;

namespace Dancy.Domain;

public enum TargetMatchStrategy
{
    NoMatch,
    ExactDirectory,
    SameRigAndLayer,
    SameRig,
    SingleSharedTarget,
    FallbackAllTargetVariants,
}

public sealed class TargetMatchResult
{
    public TargetMatchResult(TargetMatchStrategy strategy, IReadOnlyList<string> gamePaths, IReadOnlyList<string> warnings)
    {
        Strategy = strategy;
        GamePaths = gamePaths;
        Warnings = warnings;
    }

    public TargetMatchStrategy Strategy { get; }
    public IReadOnlyList<string> GamePaths { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsMatch => GamePaths.Count > 0;
}

public static class TargetPathMatcher
{
    public static TargetMatchResult Match(string sourceGamePath, IEnumerable<string> targetGamePaths)
    {
        var targets = targetGamePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
            return new TargetMatchResult(TargetMatchStrategy.NoMatch, targets, new[] { "The selected target has no resolvable PAP files." });

        var source = GamePathIdentity.Parse(sourceGamePath);
        var exact = targets
            .Where(path => string.Equals(GamePathIdentity.Parse(path).Directory, source.Directory, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0)
            return new TargetMatchResult(TargetMatchStrategy.ExactDirectory, exact, Array.Empty<string>());

        if (source.Character.IsKnown && !string.IsNullOrEmpty(source.Layer))
        {
            var sameRigAndLayer = targets
                .Where(path =>
                {
                    var candidate = GamePathIdentity.Parse(path);
                    return string.Equals(candidate.Character.Code, source.Character.Code, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(candidate.Layer, source.Layer, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            if (sameRigAndLayer.Count > 0)
                return new TargetMatchResult(TargetMatchStrategy.SameRigAndLayer, sameRigAndLayer, Array.Empty<string>());
        }

        if (source.Character.IsKnown)
        {
            var sameRig = targets
                .Where(path => string.Equals(GamePathIdentity.Parse(path).Character.Code, source.Character.Code, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sameRig.Count > 0)
                return new TargetMatchResult(TargetMatchStrategy.SameRig, sameRig, Array.Empty<string>());
        }

        if (targets.Count == 1)
        {
            return new TargetMatchResult(
                TargetMatchStrategy.SingleSharedTarget,
                targets,
                new[] { "The source rig has no matching target variant; Dancy will use the target's single shared PAP." });
        }

        return new TargetMatchResult(
            TargetMatchStrategy.FallbackAllTargetVariants,
            targets,
            new[] { "The source rig has no matching target variant; Dancy will map the selected source PAP to every available target variant." });
    }
}
