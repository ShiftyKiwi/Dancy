using Dancy.Pap;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace Dancy.Diagnostics;

/// <summary>
/// Reads a Penumbra library without changing its files. The output contains
/// only metadata, PAP structural summaries, and hashes; it never retains a
/// creator asset or expands an archive to disk.
/// </summary>
public static class PenumbraAnimationCorpusIndexer
{
    public static PenumbraAnimationCorpusIndex Create(
        string libraryRoot,
        IReadOnlyDictionary<string, PapCorpusSummary>? vanillaByGamePath = null,
        Func<string, bool>? includePayloadHashesByGamePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        if (!Directory.Exists(libraryRoot))
            throw new DirectoryNotFoundException($"Penumbra corpus root was not found: {libraryRoot}");

        var issues = new List<PenumbraCorpusIssue>();
        var documents = EnumerateDirectoryDocuments(libraryRoot, issues)
            .Concat(EnumerateArchiveDocuments(libraryRoot, issues))
            .OrderBy(document => document.Source.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mods = new List<PenumbraCorpusMod>();
        foreach (var document in documents)
        {
            try
            {
                var meta = JObject.Parse(document.MetadataJson);
                var mappings = EnumeratePapMappings(meta, document, libraryRoot, issues, vanillaByGamePath, includePayloadHashesByGamePath)
                    .OrderBy(mapping => mapping.Scope.GroupOrder)
                    .ThenBy(mapping => mapping.Scope.OptionOrder)
                    .ThenBy(mapping => mapping.GamePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(mapping => mapping.ModPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                mods.Add(new PenumbraCorpusMod(
                    document.Source,
                    meta["FileVersion"]?.Value<int?>(),
                    meta["Identifier"]?.ToString() ?? string.Empty,
                    meta["Name"]?.ToString() ?? Path.GetFileNameWithoutExtension(document.Source.RelativePath),
                    meta["Author"]?.ToString() ?? string.Empty,
                    meta["Version"]?.ToString() ?? string.Empty,
                    mappings));
            }
            catch (Exception exception)
            {
                issues.Add(new PenumbraCorpusIssue(document.Source.RelativePath, "metadata", exception.Message));
            }
        }

        return new PenumbraAnimationCorpusIndex(
            SchemaVersion: 1,
            SourceRootName: new DirectoryInfo(libraryRoot).Name,
            ActiveSelectionStatus: "Not observed: corpus indexing records metadata defaults only.",
            Mods: mods,
            Issues: issues.OrderBy(issue => issue.Source, StringComparer.OrdinalIgnoreCase).ThenBy(issue => issue.Kind, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public static PenumbraAnimationCorpusIndex AttachVanillaSummaries(
        PenumbraAnimationCorpusIndex index,
        IReadOnlyDictionary<string, PapCorpusSummary> vanillaByGamePath)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(vanillaByGamePath);

        return index with
        {
            Mods = index.Mods.Select(mod => mod with
            {
                PapMappings = mod.PapMappings.Select(mapping =>
                {
                    vanillaByGamePath.TryGetValue(mapping.GamePath, out var vanilla);
                    return mapping with
                    {
                        Vanilla = vanilla,
                        Comparison = PapCorpusComparison.Create(mapping.Modded, vanilla),
                    };
                }).ToList(),
            }).ToList(),
        };
    }

    public static PenumbraCorpusReadOnlyInventory CaptureReadOnlyInventory(string libraryRoot, int sampleCount = 64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        if (!Directory.Exists(libraryRoot))
            throw new DirectoryNotFoundException($"Penumbra corpus root was not found: {libraryRoot}");
        if (sampleCount < 1)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));

        var files = Directory.EnumerateFiles(libraryRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var samples = new List<PenumbraCorpusInventorySample>();
        foreach (var index in SampleIndices(files.Count, sampleCount))
        {
            var path = files[index];
            var info = new FileInfo(path);
            samples.Add(new PenumbraCorpusInventorySample(
                Path.GetRelativePath(libraryRoot, path),
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                HashFile(path)));
        }
        return new PenumbraCorpusReadOnlyInventory(
            files.Count,
            files.Sum(path => new FileInfo(path).Length),
            samples);
    }

    private static IEnumerable<CorpusDocument> EnumerateDirectoryDocuments(string libraryRoot, List<PenumbraCorpusIssue> issues)
    {
        var metadataPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var documents = new List<CorpusDocument>();
        foreach (var name in new[] { "meta.json", "default_mod.json" })
        {
            try
            {
                foreach (var metadataPath in Directory.EnumerateFiles(libraryRoot, name, SearchOption.AllDirectories))
                {
                    var modDirectory = Path.GetDirectoryName(metadataPath) ?? libraryRoot;
                    if (name.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(Path.Combine(modDirectory, "meta.json")))
                        continue;
                    metadataPaths.TryAdd(modDirectory, metadataPath);
                }
            }
            catch (Exception exception)
            {
                issues.Add(new PenumbraCorpusIssue(".", "enumerate-directory-metadata", exception.Message));
            }
        }

        foreach (var (modDirectory, metadataPath) in metadataPaths.OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(libraryRoot, modDirectory);
            try
            {
                documents.Add(new CorpusDocument(
                    new PenumbraCorpusSource("Directory", relative, Path.GetFileName(metadataPath)),
                    File.ReadAllText(metadataPath),
                    rawModPath => OpenDirectoryAsset(modDirectory, rawModPath)));
            }
            catch (Exception exception)
            {
                issues.Add(new PenumbraCorpusIssue(relative, "read-metadata", exception.Message));
            }
        }

        return documents;
    }

    private static IEnumerable<CorpusDocument> EnumerateArchiveDocuments(string libraryRoot, List<PenumbraCorpusIssue> issues)
    {
        var documents = new List<CorpusDocument>();
        IEnumerable<string> archives;
        try
        {
            archives = Directory.EnumerateFiles(libraryRoot, "*.pmp", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            issues.Add(new PenumbraCorpusIssue(".", "enumerate-archives", exception.Message));
            return documents;
        }

        foreach (var archivePath in archives)
        {
            var relative = Path.GetRelativePath(libraryRoot, archivePath);
            try
            {
                using var archive = ZipFile.OpenRead(archivePath);
                var metadata = archive.Entries.FirstOrDefault(entry => entry.FullName.Equals("meta.json", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase));
                if (metadata is null)
                {
                    issues.Add(new PenumbraCorpusIssue(relative, "archive-metadata", "Archive contains no root meta.json or default_mod.json."));
                    continue;
                }

                using var reader = new StreamReader(metadata.Open());
                var metadataJson = reader.ReadToEnd();
                documents.Add(new CorpusDocument(
                    new PenumbraCorpusSource("Archive", relative, metadata.FullName),
                    metadataJson,
                    rawModPath => OpenArchiveAsset(archivePath, rawModPath)));
            }
            catch (Exception exception)
            {
                issues.Add(new PenumbraCorpusIssue(relative, "read-archive", exception.Message));
            }
        }

        return documents;
    }

    private static IEnumerable<PenumbraCorpusPapMapping> EnumeratePapMappings(
        JObject meta,
        CorpusDocument document,
        string libraryRoot,
        List<PenumbraCorpusIssue> issues,
        IReadOnlyDictionary<string, PapCorpusSummary>? vanillaByGamePath,
        Func<string, bool>? includePayloadHashesByGamePath)
    {
        foreach (var scope in EnumerateScopes(meta))
        {
            if (scope.Files is null)
                continue;

            foreach (var property in scope.Files.Properties())
            {
                var gamePath = NormalizePath(property.Name);
                var modPath = NormalizePath(property.Value.ToString());
                if (!gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                    continue;

                PapCorpusSummary? pap = null;
                string? assetError = null;
                try
                {
                    using var asset = document.OpenAsset(modPath);
                    if (asset is null)
                        assetError = "PAP asset was not found at the metadata path.";
                    else if (!PapCorpusSummary.TryCreate(asset, includePayloadHashesByGamePath is null, out pap, out assetError))
                        assetError ??= "PAP structure could not be inspected.";
                    else if (!pap!.PayloadHashesCaptured
                        && (pap.Sections.Count > 1 || includePayloadHashesByGamePath?.Invoke(gamePath) == true))
                    {
                        asset.Position = 0;
                        if (!PapCorpusSummary.TryCreate(asset, includePayloadHashes: true, out pap, out assetError))
                            assetError ??= "PAP payload hashes could not be captured.";
                    }
                }
                catch (Exception exception)
                {
                    assetError = exception.Message;
                }

                if (!string.IsNullOrWhiteSpace(assetError))
                    issues.Add(new PenumbraCorpusIssue(document.Source.RelativePath, "pap", $"{gamePath}: {assetError}"));

                PapCorpusSummary? vanilla = null;
                if (vanillaByGamePath is not null)
                    vanillaByGamePath.TryGetValue(gamePath, out vanilla);
                yield return new PenumbraCorpusPapMapping(
                    scope.ToPublic(),
                    gamePath,
                    modPath,
                    document.Source.Kind == "Archive" ? $"{document.Source.RelativePath}!/{modPath}" : $"{document.Source.RelativePath}/{modPath}",
                    pap,
                    vanilla,
                    PapCorpusComparison.Create(pap, vanilla),
                    assetError);
            }
        }
    }

    private static IEnumerable<ScopeDocument> EnumerateScopes(JObject meta)
    {
        yield return new ScopeDocument("Default data", "(default)", "DefaultData", 0, 0, null, true, meta["DefaultData"]?["Files"] as JObject);

        if (meta["Groups"] is not JArray groups)
            yield break;

        foreach (var (group, groupIndex) in groups.OfType<JObject>().Select((value, index) => (value, index)))
        {
            var groupName = group["Name"]?.ToString() ?? $"Group {groupIndex + 1}";
            var type = group["Type"]?.ToString() ?? "Unknown";
            var priority = group["Priority"]?.Value<int?>();
            var defaultSettings = group["DefaultSettings"]?.Value<int?>();
            if (group["Options"] is JArray options)
            {
                foreach (var (option, optionIndex) in options.OfType<JObject>().Select((value, index) => (value, index)))
                {
                    yield return new ScopeDocument(
                        groupName,
                        option["Name"]?.ToString() ?? $"Option {optionIndex + 1}",
                        type,
                        groupIndex + 1,
                        optionIndex,
                        priority,
                        ResolveDefaultSelection(type, defaultSettings, optionIndex),
                        option["Files"] as JObject);
                }
            }

            if (group["Containers"] is not JArray containers)
                continue;
            foreach (var (container, containerIndex) in containers.OfType<JObject>().Select((value, index) => (value, index)))
            {
                yield return new ScopeDocument(
                    groupName,
                    container["Name"]?.ToString() ?? $"Container {containerIndex + 1}",
                    $"{type}:Container",
                    groupIndex + 1,
                    containerIndex,
                    priority,
                    null,
                    container["Files"] as JObject);
            }
        }
    }

    private static bool? ResolveDefaultSelection(string type, int? settings, int optionIndex)
    {
        if (settings is null)
            return null;
        if (type.Equals("Single", StringComparison.OrdinalIgnoreCase))
            return settings.Value == optionIndex;
        if (type.Equals("Multi", StringComparison.OrdinalIgnoreCase) && optionIndex is >= 0 and < 31)
            return (settings.Value & (1 << optionIndex)) != 0;
        return null;
    }

    private static Stream? OpenDirectoryAsset(string modDirectory, string rawModPath)
    {
        var normalized = rawModPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(modDirectory, normalized));
        var boundary = Path.GetFullPath(modDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Metadata PAP path escapes its mod directory.");
        if (!File.Exists(candidate))
            return null;
        return new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static Stream? OpenArchiveAsset(string archivePath, string rawModPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var normalized = NormalizePath(rawModPath);
        var entry = archive.Entries.FirstOrDefault(candidate => NormalizePath(candidate.FullName).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return new MemoryStream(output.ToArray(), writable: false);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static IReadOnlyList<int> SampleIndices(int count, int sampleCount)
    {
        if (count == 0)
            return Array.Empty<int>();
        if (count <= sampleCount)
            return Enumerable.Range(0, count).ToList();
        return Enumerable.Range(0, sampleCount)
            .Select(index => (int)((long)index * (count - 1) / (sampleCount - 1)))
            .Distinct()
            .ToList();
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record CorpusDocument(PenumbraCorpusSource Source, string MetadataJson, Func<string, Stream?> OpenAsset);

    private sealed record ScopeDocument(string GroupName, string OptionName, string SelectionType, int GroupOrder, int OptionOrder, int? Priority, bool? DefaultSelected, JObject? Files)
    {
        public PenumbraCorpusOptionScope ToPublic() => new(GroupName, OptionName, SelectionType, GroupOrder, OptionOrder, Priority, DefaultSelected);
    }
}

public sealed record PenumbraAnimationCorpusIndex(
    int SchemaVersion,
    string SourceRootName,
    string ActiveSelectionStatus,
    IReadOnlyList<PenumbraCorpusMod> Mods,
    IReadOnlyList<PenumbraCorpusIssue> Issues);

public sealed record PenumbraCorpusSource(string Kind, string RelativePath, string MetadataEntry);
public sealed record PenumbraCorpusIssue(string Source, string Kind, string Message);
public sealed record PenumbraCorpusReadOnlyInventory(int FileCount, long TotalBytes, IReadOnlyList<PenumbraCorpusInventorySample> Samples);
public sealed record PenumbraCorpusInventorySample(string RelativePath, long Length, long LastWriteUtcTicks, string Sha256);
public sealed record PenumbraCorpusMod(PenumbraCorpusSource Source, int? FileVersion, string Identifier, string Name, string Author, string Version, IReadOnlyList<PenumbraCorpusPapMapping> PapMappings);
public sealed record PenumbraCorpusOptionScope(string GroupName, string OptionName, string SelectionType, int GroupOrder, int OptionOrder, int? Priority, bool? DefaultSelected);
public sealed record PenumbraCorpusPapMapping(PenumbraCorpusOptionScope Scope, string GamePath, string ModPath, string AssetIdentity, PapCorpusSummary? Modded, PapCorpusSummary? Vanilla, PapCorpusComparison? Comparison, string? AssetIssue);

public sealed record PapCorpusSummary(string FileSha256, string HavokPayloadSha256, int HavokPayloadSize, IReadOnlyList<PapCorpusAnimationSection> Sections, IReadOnlyList<PapCorpusTimelineSection> TimelineSections, bool PayloadHashesCaptured)
{
    public static bool TryCreate(byte[] bytes, out PapCorpusSummary? summary, out string? error)
    {
        using var input = new MemoryStream(bytes, writable: false);
        return TryCreate(input, includePayloadHashes: true, out summary, out error);
    }

    public static bool TryCreate(Stream input, bool includePayloadHashes, out PapCorpusSummary? summary, out string? error)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!input.CanSeek)
            {
                using var buffered = new MemoryStream();
                input.CopyTo(buffered);
                return TryCreate(buffered.ToArray(), out summary, out error);
            }

            if (input.Length < 26 || input.Length > int.MaxValue)
                throw new InvalidDataException("PAP header is truncated or exceeds the supported inspection length.");

            var fileHeader = ReadRange(input, 0, 26);
            if (BitConverter.ToInt32(fileHeader, 0) != 0x20706170)
                throw new InvalidDataException("PAP magic is invalid.");

            var animationCount = BitConverter.ToInt16(fileHeader, 8);
            var animationHeaderOffset = BitConverter.ToInt32(fileHeader, 14);
            var havokOffset = BitConverter.ToInt32(fileHeader, 18);
            var timelineOffset = BitConverter.ToInt32(fileHeader, 22);
            var headersEnd = (long)animationHeaderOffset + animationCount * 40;
            if (animationCount <= 0 || animationHeaderOffset < 26 || headersEnd > input.Length || havokOffset < headersEnd || timelineOffset <= havokOffset || timelineOffset > input.Length)
                throw new InvalidDataException("PAP header offsets are invalid.");

            var headers = new List<PapCorpusAnimationSection>(animationCount);
            for (var index = 0; index < animationCount; index++)
            {
                var header = ReadRange(input, animationHeaderOffset + index * 40, 40);
                headers.Add(new PapCorpusAnimationSection(
                    index,
                    ReadNullTerminatedString(header, 0, 32),
                    BitConverter.ToInt16(header, 32),
                    BitConverter.ToInt32(header, 36) == 1,
                    BitConverter.ToInt16(header, 34),
                    Hash(header)));
            }

            var timelines = new List<PapCorpusTimelineSection>(animationCount);
            var position = timelineOffset;
            var customOffset = timelineOffset % 4;
            for (var index = 0; index < animationCount; index++)
            {
                if (position < 0 || position + 8 > input.Length)
                    throw new InvalidDataException("TMB section is outside the PAP file.");
                var prefix = ReadRange(input, position, 8);
                var size = BitConverter.ToInt32(prefix, 4);
                if (size <= 0 || (long)position + size > input.Length)
                    throw new InvalidDataException("TMB section size is invalid.");
                timelines.Add(new PapCorpusTimelineSection(index, size, includePayloadHashes ? HashRange(input, position, size) : string.Empty));
                position += size;
                if (index < animationCount - 1)
                {
                    var remainder = (position - customOffset) % 4;
                    position += remainder == 0 ? 0 : 4 - remainder;
                }
            }

            summary = new PapCorpusSummary(
                includePayloadHashes ? HashRange(input, 0, checked((int)input.Length)) : string.Empty,
                includePayloadHashes ? HashRange(input, havokOffset, timelineOffset - havokOffset) : string.Empty,
                timelineOffset - havokOffset,
                headers,
                timelines,
                includePayloadHashes);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            summary = null;
            error = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static byte[] ReadRange(Stream input, int offset, int length)
    {
        var bytes = new byte[length];
        input.Position = offset;
        var read = 0;
        while (read < bytes.Length)
        {
            var received = input.Read(bytes, read, bytes.Length - read);
            if (received == 0)
                throw new EndOfStreamException("PAP ended before the requested range could be read.");
            read += received;
        }
        return bytes;
    }

    private static string HashRange(Stream input, int offset, int length)
    {
        input.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
                throw new EndOfStreamException("PAP ended before the requested range could be hashed.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReadNullTerminatedString(byte[] bytes, int offset, int length)
    {
        var actualLength = 0;
        while (actualLength < length && bytes[offset + actualLength] != 0)
            actualLength++;
        return System.Text.Encoding.UTF8.GetString(bytes, offset, actualLength);
    }
}

public sealed record PapCorpusAnimationSection(int Index, string AnimationName, int AnimationType, bool IsFaceAnimation, int HavokMotionIndex, string HeaderSha256);
public sealed record PapCorpusTimelineSection(int Index, int Size, string ContentSha256);

/// <summary>
/// PAP stores a single Havok container, not independently addressable Havok blobs
/// per animation header. A differing container hash therefore cannot honestly be
/// attributed to one section without a separate Havok-level reader.
/// </summary>
public sealed record PapCorpusComparison(bool FileMatches, bool HavokPayloadMatches, IReadOnlyList<PapCorpusSectionComparison> Sections)
{
    public static PapCorpusComparison? Create(PapCorpusSummary? modded, PapCorpusSummary? vanilla)
    {
        if (modded is null || vanilla is null || !modded.PayloadHashesCaptured || !vanilla.PayloadHashesCaptured)
            return null;

        var wholeHavokMatches = modded.HavokPayloadSha256.Equals(vanilla.HavokPayloadSha256, StringComparison.OrdinalIgnoreCase);
        var sections = modded.Sections.Select(section =>
        {
            var vanillaHeader = vanilla.Sections.FirstOrDefault(candidate => candidate.Index == section.Index);
            var moddedTimeline = modded.TimelineSections.FirstOrDefault(candidate => candidate.Index == section.Index);
            var vanillaTimeline = vanilla.TimelineSections.FirstOrDefault(candidate => candidate.Index == section.Index);
            return new PapCorpusSectionComparison(
                section.Index,
                vanillaHeader is not null && section.HeaderSha256.Equals(vanillaHeader.HeaderSha256, StringComparison.OrdinalIgnoreCase),
                moddedTimeline is not null && vanillaTimeline is not null && moddedTimeline.ContentSha256.Equals(vanillaTimeline.ContentSha256, StringComparison.OrdinalIgnoreCase),
                wholeHavokMatches ? "same-by-whole-payload-identity" : "unknown-section-boundary-in-different-whole-payload");
        }).ToList();
        return new PapCorpusComparison(
            modded.FileSha256.Equals(vanilla.FileSha256, StringComparison.OrdinalIgnoreCase),
            wholeHavokMatches,
            sections);
    }
}

public sealed record PapCorpusSectionComparison(int Index, bool HeaderMatches, bool TimelineMatches, string HavokSectionStatus);
