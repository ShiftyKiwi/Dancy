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
        if (string.IsNullOrWhiteSpace(request.TargetTimelineKey))
            errors.Add("The selected target has no usable animation timeline.");
        if (request.TargetGamePaths.Count == 0)
            errors.Add("The selected target has no resolvable PAP files.");

        var overrideId = CreateStableId(request, sources);
        if (errors.Count > 0)
            return CreateResult(request, overrideId, copies, mappings, warnings, errors);

        foreach (var sourceGroup in sources.GroupBy(source => GamePathIdentity.Normalize(source.SourcePapPath), StringComparer.OrdinalIgnoreCase))
        {
            var group = sourceGroup.ToList();
            var matchResults = group
                .Select(source => TargetPathMatcher.Match(source.GamePath, request.TargetGamePaths))
                .ToList();
            var targetPaths = matchResults
                .SelectMany(result => result.GamePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (targetPaths.Count == 0)
            {
                errors.Add($"No target PAP path matched source {group[0].GamePath}.");
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
                SourcePapPath = group[0].SourcePapPath,
                OutputRelativePath = outputRelativePath,
                SourceGamePaths = group.Select(source => source.GamePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
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
        => new()
        {
            OverrideId = overrideId,
            DisplayName = $"({request.SourceGroupName}) {request.SourceOptionName} -> {request.TargetName}",
            Description = $"Dancy override {overrideId} using {request.TargetName} ({request.TargetCommand}).",
            PapCopies = copies,
            PlannedMappings = mappings,
            Warnings = warnings,
            Errors = errors,
        };

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
}
