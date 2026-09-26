using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dancy.Domain;
using Dancy.Persistence;
using Newtonsoft.Json.Linq;

namespace Dancy.Penumbra;

public enum PenumbraMetadataFormat
{
    V4Meta,
    LegacyGroupFile,
}

public sealed class PenumbraWriteResult
{
    public PenumbraMetadataFormat Format { get; init; }
    public string MetadataPath { get; init; } = string.Empty;
    public string OverrideId { get; init; } = string.Empty;
}

public static class PenumbraGroupWriter
{
    public static IReadOnlyList<string> GetExistingGeneratedFiles(string modFolder, string overrideId)
    {
        var metaPath = Path.Combine(modFolder, "meta.json");
        if (!File.Exists(metaPath))
            return Array.Empty<string>();

        try
        {
            var meta = JObject.Parse(File.ReadAllText(metaPath));
            var group = (meta["Groups"] as JArray)?.OfType<JObject>().FirstOrDefault(DancyMetadataMutator.IsDancyGroup);
            var option = (group?["Options"] as JArray)?.OfType<JObject>().FirstOrDefault(candidate =>
                string.Equals(candidate["Id"]?.ToString(), overrideId, StringComparison.OrdinalIgnoreCase));
            return (option?["Files"] as JObject)?.Properties()
                .Select(property => property.Value.ToString())
                .Where(IsDancyGeneratedPapPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
        }
        catch
        {
            // The writer will return the original parse error when it attempts the actual update.
            return Array.Empty<string>();
        }
    }

    public static PenumbraWriteResult CreateOrUpdateDancyGroup(
        string modFolder,
        OverridePlan plan,
        IReadOnlyDictionary<string, string> finalMappings,
        AtomicJsonWriteOptions? writeOptions = null)
    {
        if (string.IsNullOrWhiteSpace(modFolder) || !Directory.Exists(modFolder))
            throw new DirectoryNotFoundException("The selected mod directory does not exist.");
        if (!plan.IsValid)
            throw new InvalidOperationException("Dancy cannot write metadata for an invalid override plan.");
        if (finalMappings.Count == 0)
            throw new InvalidOperationException("Dancy cannot write an override without file mappings.");

        ValidateDancyOwnedMappings(modFolder, finalMappings);

        var metaPath = Path.Combine(modFolder, "meta.json");
        if (File.Exists(metaPath))
        {
            var meta = JObject.Parse(File.ReadAllText(metaPath));
            var fileVersion = meta["FileVersion"]?.Value<int?>() ?? 0;
            if (fileVersion >= 4)
            {
                DancyMetadataMutator.UpsertV4(meta, plan, finalMappings);
                AtomicJsonFile.Write(metaPath, meta, writeOptions);
                return new PenumbraWriteResult
                {
                    Format = PenumbraMetadataFormat.V4Meta,
                    MetadataPath = metaPath,
                    OverrideId = plan.OverrideId,
                };
            }
        }

        var legacyPath = FindOrCreateLegacyGroupPath(modFolder);
        var legacyGroup = File.Exists(legacyPath)
            ? JObject.Parse(File.ReadAllText(legacyPath))
            : new JObject();
        DancyMetadataMutator.UpsertLegacy(legacyGroup, plan, finalMappings);
        AtomicJsonFile.Write(legacyPath, legacyGroup, writeOptions);
        return new PenumbraWriteResult
        {
            Format = PenumbraMetadataFormat.LegacyGroupFile,
            MetadataPath = legacyPath,
            OverrideId = plan.OverrideId,
        };
    }

    private static string FindOrCreateLegacyGroupPath(string modFolder)
    {
        var groupFiles = Directory.GetFiles(modFolder, "group_*.json", SearchOption.TopDirectoryOnly);
        return groupFiles.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path)
                   .Contains("yucksdancy", StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(modFolder, $"group_{groupFiles.Length + 1:D3}_yucksdancy.json");
    }

    private static void ValidateDancyOwnedMappings(string modFolder, IReadOnlyDictionary<string, string> mappings)
    {
        foreach (var (gamePath, modPath) in mappings)
        {
            if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(modPath))
                throw new InvalidOperationException("Dancy encountered an empty game or mod path while writing metadata.");
            if (!IsDancyGeneratedPapPath(modPath)
                || !PathSafety.TryResolveInsideRoot(modFolder, modPath, out _))
            {
                throw new InvalidOperationException($"Dancy refused to write an unsafe generated PAP path: {modPath}");
            }
        }
    }

    public static bool IsDancyGeneratedPapPath(string modPath)
        => modPath.StartsWith("yucksdancy/paps/", StringComparison.OrdinalIgnoreCase)
           && modPath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase);
}
