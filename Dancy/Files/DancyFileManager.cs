using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dancy.Penumbra;
using Dancy.Persistence;
using Newtonsoft.Json.Linq;

namespace Dancy.Files;

public sealed record DancyOverrideInfo(string Id, string Name, string Description, IReadOnlyDictionary<string, string> Mappings);

public sealed record DancyGarbageCollectionResult(
    IReadOnlyList<string> ReferencedFiles,
    IReadOnlyList<string> RemovedFiles,
    IReadOnlyList<string> RemainingFiles,
    bool DryRun);

public sealed record DancyRemovalResult(
    bool Changed,
    int RemovedOverrideCount,
    DancyGarbageCollectionResult GarbageCollection,
    bool DiskClean,
    string Verification)
{
    /// <summary>
    /// The requested option was removed and no Dancy-owned orphan remains. This
    /// can be true when other Dancy overrides legitimately remain in the mod.
    /// </summary>
    public bool RequestedOverrideCleanupSucceeded { get; init; }
}

/// <summary>
/// Owns only the Dancy group and yucksdancy/ output. It deliberately never
/// removes source-mod content outside that directory.
/// </summary>
public static class DancyFileManager
{
    public static bool DancyExists(string modPath)
        => GetDancyOverrides(modPath).Count > 0
           || HasDancyMetadataGroup(modPath)
           || GetGeneratedFiles(modPath).Count > 0;

    public static IReadOnlyList<DancyOverrideInfo> GetDancyOverrides(string modPath)
    {
        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
            return Array.Empty<DancyOverrideInfo>();

        var options = new List<DancyOverrideInfo>();
        if (TryReadJson(Path.Combine(modPath, "meta.json"), out var meta))
            options.AddRange(GetOptionsFromMeta(meta));

        foreach (var legacyPath in GetLegacyGroupPaths(modPath))
        {
            if (TryReadJson(legacyPath, out var group) && DancyMetadataMutator.IsDancyGroup(group))
                options.AddRange(GetOptionsFromGroup(group));
        }

        return options
            .GroupBy(option => option.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static DancyRemovalResult RemoveDancyOverride(string modPath, string overrideId)
    {
        if (string.IsNullOrWhiteSpace(overrideId))
            throw new ArgumentException("A Dancy override ID is required.", nameof(overrideId));

        var removed = RemoveMatchingOptions(modPath, option => string.Equals(GetOverrideId(option), overrideId, StringComparison.OrdinalIgnoreCase));
        return CreateRemovalResult(modPath, removed, CollectGeneratedFiles(modPath), overrideId);
    }

    public static DancyRemovalResult RemoveAllDancyOverrides(string modPath)
    {
        var removed = RemoveMatchingOptions(modPath, _ => true);
        return CreateRemovalResult(modPath, removed, CollectGeneratedFiles(modPath));
    }

    // Compatibility wrapper for the original UI call site.
    public static bool RemoveDancy(string modPath)
        => RemoveAllDancyOverrides(modPath).Changed;

    public static DancyGarbageCollectionResult CollectGeneratedFiles(string modPath, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
            return new DancyGarbageCollectionResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), dryRun);

        var referenced = GetDancyOverrides(modPath)
            .SelectMany(option => option.Mappings.Values)
            .Where(PenumbraGroupWriter.IsDancyGeneratedPapPath)
            .Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generated = GetGeneratedFiles(modPath);
        var orphaned = generated.Where(path => !referenced.Contains(path)).ToList();

        if (!dryRun)
        {
            foreach (var relativePath in orphaned)
            {
                if (PathSafety.TryResolveInsideRoot(modPath, relativePath, out var fullPath) && File.Exists(fullPath))
                    File.Delete(fullPath);
            }

            RemoveEmptyDancyDirectories(modPath);
        }

        var remaining = dryRun ? generated : GetGeneratedFiles(modPath);
        return new DancyGarbageCollectionResult(referenced.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(), orphaned, remaining, dryRun);
    }

    private static int RemoveMatchingOptions(string modPath, Func<JObject, bool> shouldRemove)
    {
        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
            return 0;

        var removed = 0;
        var metaPath = Path.Combine(modPath, "meta.json");
        if (TryReadJson(metaPath, out var meta) && meta["Groups"] is JArray groups)
        {
            var dancyGroups = groups.OfType<JObject>().Where(DancyMetadataMutator.IsDancyGroup).ToList();
            var metadataChanged = false;
            foreach (var group in dancyGroups)
            {
                var options = group["Options"] as JArray;
                if (options is null)
                {
                    group.Remove();
                    metadataChanged = true;
                    continue;
                }

                var matching = options.OfType<JObject>().Where(shouldRemove).ToList();
                foreach (var option in matching)
                {
                    option.Remove();
                    ++removed;
                    metadataChanged = true;
                }

                if (!options.OfType<JObject>().Any())
                {
                    group.Remove();
                    metadataChanged = true;
                }
            }

            if (metadataChanged)
                AtomicJsonFile.Write(metaPath, meta);
        }

        foreach (var legacyPath in GetLegacyGroupPaths(modPath))
        {
            if (!TryReadJson(legacyPath, out var group) || !DancyMetadataMutator.IsDancyGroup(group))
                continue;

            var options = group["Options"] as JArray;
            if (options is null)
            {
                File.Delete(legacyPath);
                continue;
            }
            var matching = options.OfType<JObject>().Where(shouldRemove).ToList();
            foreach (var option in matching)
            {
                option.Remove();
                ++removed;
            }

            if (matching.Count == 0)
                continue;
            if (options.OfType<JObject>().Any())
                AtomicJsonFile.Write(legacyPath, group);
            else
                File.Delete(legacyPath);
        }

        return removed;
    }

    private static DancyRemovalResult CreateRemovalResult(string modPath, int removed, DancyGarbageCollectionResult gc, string? overrideId = null)
    {
        var diskClean = !HasDancyMetadataGroup(modPath) && GetGeneratedFiles(modPath).Count == 0;
        var noOrphanedFilesRemain = gc.RemainingFiles.All(path => gc.ReferencedFiles.Contains(path, StringComparer.OrdinalIgnoreCase));
        var requestedOptionAbsent = string.IsNullOrWhiteSpace(overrideId)
            ? diskClean
            : GetDancyOverrides(modPath).All(option => !string.Equals(option.Id, overrideId, StringComparison.OrdinalIgnoreCase));
        var requestedCleanupSucceeded = removed > 0 && requestedOptionAbsent && noOrphanedFilesRemain;
        var verification = diskClean
            ? "DISK CLEAN: no Dancy metadata group or yucksdancy files remain."
            : requestedCleanupSucceeded
                ? "DISK CLEAN FOR SELECTED OVERRIDE: its metadata was removed and no orphaned Dancy files remain; other referenced Dancy overrides remain."
                : "DISK NOT CLEAN: Dancy metadata or generated files remain.";
        return new DancyRemovalResult(
            removed > 0 || gc.RemovedFiles.Count > 0,
            removed,
            gc,
            diskClean,
            verification)
        {
            RequestedOverrideCleanupSucceeded = requestedCleanupSucceeded,
        };
    }

    private static bool HasDancyMetadataGroup(string modPath)
    {
        if (TryReadJson(Path.Combine(modPath, "meta.json"), out var meta)
            && (meta["Groups"] as JArray)?.OfType<JObject>().Any(DancyMetadataMutator.IsDancyGroup) == true)
        {
            return true;
        }

        return GetLegacyGroupPaths(modPath)
            .Any(path => TryReadJson(path, out var group) && DancyMetadataMutator.IsDancyGroup(group));
    }

    private static IReadOnlyList<DancyOverrideInfo> GetOptionsFromMeta(JObject meta)
    {
        if (meta["Groups"] is not JArray groups)
            return Array.Empty<DancyOverrideInfo>();

        return groups.OfType<JObject>()
            .Where(DancyMetadataMutator.IsDancyGroup)
            .SelectMany(GetOptionsFromGroup)
            .ToList();
    }

    private static IReadOnlyList<DancyOverrideInfo> GetOptionsFromGroup(JObject group)
    {
        if (group["Options"] is not JArray options)
            return Array.Empty<DancyOverrideInfo>();

        return options.OfType<JObject>()
            .Select(option => new DancyOverrideInfo(
                GetOverrideId(option),
                option["Name"]?.ToString() ?? "Dancy override",
                option["Description"]?.ToString() ?? string.Empty,
                ReadMappings(option["Files"] as JObject)))
            .Where(option => !string.IsNullOrWhiteSpace(option.Id))
            .ToList();
    }

    private static string GetOverrideId(JObject option)
        => option["Id"]?.ToString() ?? option["DancyOverrideId"]?.ToString() ?? string.Empty;

    private static IReadOnlyDictionary<string, string> ReadMappings(JObject? files)
        => files?.Properties().ToDictionary(
                property => property.Name,
                property => property.Value.ToString(),
                StringComparer.OrdinalIgnoreCase)
           ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> GetGeneratedFiles(string modPath)
    {
        if (!PathSafety.TryResolveInsideRoot(modPath, "yucksdancy", out var dancyDirectory) || !Directory.Exists(dancyDirectory))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(dancyDirectory, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(modPath, path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> GetLegacyGroupPaths(string modPath)
        => Directory.Exists(modPath)
            ? Directory.EnumerateFiles(modPath, "group_*.json", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

    private static void RemoveEmptyDancyDirectories(string modPath)
    {
        if (!PathSafety.TryResolveInsideRoot(modPath, "yucksdancy", out var dancyDirectory) || !Directory.Exists(dancyDirectory))
            return;

        foreach (var directory in Directory.EnumerateDirectories(dancyDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }

        if (!Directory.EnumerateFileSystemEntries(dancyDirectory).Any())
            Directory.Delete(dancyDirectory);
    }

    private static bool TryReadJson(string path, out JObject json)
    {
        try
        {
            json = JObject.Parse(File.ReadAllText(path));
            return true;
        }
        catch
        {
            json = new JObject();
            return false;
        }
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
