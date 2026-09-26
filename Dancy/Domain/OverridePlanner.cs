using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Dancy.Domain;

public sealed record OverridePlanSource(string GamePath, string SourcePapPath);

public sealed class OverridePlanRequest
{
    public string ModIdentity { get; init; } = string.Empty;
    public string SourceGroupName { get; init; } = string.Empty;
    public string SourceOptionName { get; init; } = string.Empty;
    public string SourceAnimationName { get; init; } = string.Empty;
    public string SourceAnimationCommand { get; init; } = string.Empty;
    public string TargetTimelineKey { get; init; } = string.Empty;
    public string TargetName { get; init; } = string.Empty;
    public string TargetCommand { get; init; } = string.Empty;
    public IReadOnlyList<OverridePlanSource> Sources { get; init; } = Array.Empty<OverridePlanSource>();
    public IReadOnlyList<string> TargetGamePaths { get; init; } = Array.Empty<string>();
}

public sealed class PlannedPapCopy
{
    public string SourcePapPath { get; init; } = string.Empty;
    public string OutputRelativePath { get; init; } = string.Empty;
    public IReadOnlyList<string> SourceGamePaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TargetGamePaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<TargetMatchResult> MatchResults { get; init; } = Array.Empty<TargetMatchResult>();
}

public sealed class OverridePlan
{
    public string OverrideId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<PlannedPapCopy> PapCopies { get; init; } = Array.Empty<PlannedPapCopy>();
    public IReadOnlyDictionary<string, string> PlannedMappings { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool IsValid => Errors.Count == 0 && PapCopies.Count > 0 && PlannedMappings.Count > 0;
}

public static class OverridePlanner
{
    public static OverridePlan Create(OverridePlanRequest request)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copies = new List<PlannedPapCopy>();
        var sources = request.Sources
            .Where(source => !string.IsNullOrWhiteSpace(source.GamePath) && !string.IsNullOrWhiteSpace(source.SourcePapPath))
            .ToList();

        if (sources.Count == 0)
            errors.Add("Select at least one source PAP path.");
        if (sources.Any(source => GamePathIdentity.Parse(source.GamePath).Phase != AnimationPhase.Loop))
            errors.Add("Dancy loop overrides accept only Loop-phase source PAPs. Start, end, and unknown PAPs are diagnostic context, not normal override sources.");
        if (string.IsNullOrWhiteSpace(request.TargetTimelineKey))
            errors.Add("The selected target has no usable animation timeline.");
        if (request.TargetGamePaths.Count == 0)
            errors.Add("The selected target has no resolvable PAP files.");

        var overrideId = CreateStableId(request, sources);
        if (errors.Count > 0)
            return CreateResult(request, overrideId, copies, mappings, warnings, errors);

        var sourceMatches = sources
            .Select(source => new SourceMatch(source, TargetPathMatcher.Match(source.GamePath, request.TargetGamePaths)))
            .ToList();
        var explicitlyClaimedTargets = sourceMatches
            .Where(match => match.Result.Strategy is TargetMatchStrategy.ExactDirectory
                or TargetMatchStrategy.SameRigAndLayer
                or TargetMatchStrategy.SameRig)
            .SelectMany(match => match.Result.GamePaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceGroup in sourceMatches.GroupBy(match => GamePathIdentity.Normalize(match.Source.SourcePapPath), StringComparer.OrdinalIgnoreCase))
        {
            var group = sourceGroup.ToList();
            var matchResults = group
                .Select(match => RestrictFallbackToUnclaimedTargets(match.Result, explicitlyClaimedTargets))
                .ToList();
            var targetPaths = matchResults
                .SelectMany(result => result.GamePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (targetPaths.Count == 0)
            {
                errors.Add($"No target PAP path matched source {group[0].Source.GamePath}.");
                continue;
            }

            warnings.AddRange(matchResults.SelectMany(result => result.Warnings));
            var outputRelativePath = $"yucksdancy/paps/{overrideId}-{ShortHash(sourceGroup.Key)}.pap";
            foreach (var targetPath in targetPaths)
            {
                if (mappings.TryGetValue(targetPath, out var existing) && !string.Equals(existing, outputRelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Selected source PAPs would both replace {targetPath}. Select one source path or choose a target with matching variants.");
                    continue;
                }

                mappings[targetPath] = outputRelativePath;
            }

            copies.Add(new PlannedPapCopy
            {
                SourcePapPath = group[0].Source.SourcePapPath,
                OutputRelativePath = outputRelativePath,
                SourceGamePaths = group.Select(match => match.Source.GamePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                TargetGamePaths = targetPaths,
                MatchResults = matchResults,
            });
        }

        return CreateResult(request, overrideId, copies, mappings, warnings.Distinct(StringComparer.Ordinal).ToList(), errors.Distinct(StringComparer.Ordinal).ToList());
    }

    private static OverridePlan CreateResult(
        OverridePlanRequest request,
        string overrideId,
        IReadOnlyList<PlannedPapCopy> copies,
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
    {
        var sourceName = string.IsNullOrWhiteSpace(request.SourceAnimationName)
            ? request.SourceGroupName
            : request.SourceAnimationName;
        var sourceAnimation = string.IsNullOrWhiteSpace(request.SourceAnimationCommand)
            ? sourceName
            : $"{sourceName} ({request.SourceAnimationCommand})";
        var target = string.IsNullOrWhiteSpace(request.TargetCommand)
            ? request.TargetName
            : $"{request.TargetName} ({request.TargetCommand})";
        var affectedRigs = mappings.Keys
            .Select(path => GamePathIdentity.Parse(path).Character)
            .Where(character => character.IsKnown)
            .DistinctBy(character => character.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(character => character.Code, StringComparer.OrdinalIgnoreCase)
            .Select(character => character.DisplayName)
            .ToList();
        var appliesTo = affectedRigs.Count == 0
            ? "No known race-specific target variants"
            : string.Join("\n", affectedRigs);

        return new OverridePlan
        {
            OverrideId = overrideId,
            DisplayName = $"{sourceName} -> {request.TargetName} · {mappings.Count} path{(mappings.Count == 1 ? string.Empty : "s")}",
            Description = $"Dancy animation override\n\nSource:\n{request.SourceGroupName}\nOption: {request.SourceOptionName}\nAnimation: {sourceAnimation}\n\nTarget:\n{target}\n\nApplies to:\n{appliesTo}\n\nTarget mappings:\n{mappings.Count}",
            PapCopies = copies,
            PlannedMappings = mappings,
            Warnings = warnings,
            Errors = errors,
        };
    }

    private static string CreateStableId(OverridePlanRequest request, IReadOnlyList<OverridePlanSource> sources)
    {
        var canonical = string.Join("\n", new[]
        {
            request.ModIdentity,
            request.SourceGroupName,
            request.SourceOptionName,
        }.Select(GamePathIdentity.Normalize).Concat(sources
            .OrderBy(source => source.GamePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.SourcePapPath, StringComparer.OrdinalIgnoreCase)
            .Select(source => $"{GamePathIdentity.Normalize(source.GamePath)}|{GamePathIdentity.Normalize(source.SourcePapPath)}")));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash[..16]).ToString("D");
    }

    private static string ShortHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static TargetMatchResult RestrictFallbackToUnclaimedTargets(TargetMatchResult result, ISet<string> explicitlyClaimedTargets)
    {
        if (result.Strategy != TargetMatchStrategy.FallbackAllTargetVariants)
            return result;

        var remaining = result.GamePaths
            .Where(path => !explicitlyClaimedTargets.Contains(path))
            .ToList();
        return new TargetMatchResult(result.Strategy, remaining, result.Warnings);
    }

    private sealed record SourceMatch(OverridePlanSource Source, TargetMatchResult Result);
}
