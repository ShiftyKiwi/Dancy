using System;
using System.Collections.Generic;
using System.Linq;

namespace Dancy.Domain;

/// <summary>
/// A presentation-only index over an already discovered target catalog. It is
/// intentionally unaware of PAPs, files, game data, IPC, and compatibility.
/// </summary>
public sealed record TargetCatalogPresentation<T>(
    T Target,
    string Id,
    string Name,
    string Command,
    string Trigger,
    TargetBehavior Behavior,
    TargetContext Context,
    string BehaviorDisplayName,
    string ContextDisplayName)
{
    public string SearchText { get; } = string.Join("\n", Name, Command, Trigger, BehaviorDisplayName, ContextDisplayName);
}

/// <summary>
/// Caches category/search result lists without performing target structural
/// validation. Validation is deliberately scheduled by the UI separately.
/// </summary>
public sealed class TargetCatalogPresentationCache<T>
{
    private readonly IReadOnlyList<TargetCatalogPresentation<T>> entries;
    private readonly Dictionary<QueryKey, IReadOnlyList<TargetCatalogPresentation<T>>> results = new();

    public TargetCatalogPresentationCache(IEnumerable<TargetCatalogPresentation<T>> entries)
        => this.entries = entries.ToList();

    public IReadOnlyList<TargetCatalogPresentation<T>> Entries => entries;

    public IReadOnlyList<TargetCatalogPresentation<T>> GetResults(
        TargetSelectionCategory category,
        string? query,
        int maximumResults)
    {
        if (maximumResults < 1)
            return Array.Empty<TargetCatalogPresentation<T>>();

        var normalizedQuery = query?.Trim() ?? string.Empty;
        var key = new QueryKey(category, normalizedQuery, maximumResults);
        if (results.TryGetValue(key, out var cached))
            return cached;

        var isSearching = normalizedQuery.Length > 0;
        cached = entries
            .Where(entry => isSearching
                ? TargetEmotePolicy.IsSearchResult(entry.Behavior)
                : TargetEmotePolicy.IsVisible(category, entry.Behavior))
            .Where(entry => !isSearching || entry.SearchText.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Take(maximumResults)
            .ToList();
        results.Add(key, cached);
        return cached;
    }

    private readonly record struct QueryKey(TargetSelectionCategory Category, string Query, int MaximumResults);
}
