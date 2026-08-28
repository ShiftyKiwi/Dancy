using Dancy.Core.Models;
using Dancy.Penumbra;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Dancy.Core;

public static class EmoteOverrideScanner
{
    public static List<RemappableOption> ScanMod(string modDir)
    {
        var results = new List<RemappableOption>();

        var metaPath = Path.Combine(modDir, "meta.json");
        if (File.Exists(metaPath) && TryParseJson(metaPath, out var meta))
        {
            var fileVersion = meta["FileVersion"]?.Value<int?>() ?? 0;
            if (fileVersion >= 4 || meta["Groups"] is JArray || meta["DefaultData"] is JObject)
            {
                ScanMetaJson(meta, results);
                return results;
            }
        }

        foreach (var file in Directory.GetFiles(modDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!TryParseJson(file, out var obj))
                continue;

            string groupName = obj["Name"]?.ToString() ?? Path.GetFileNameWithoutExtension(file);

            var options = new JArray();
            JObject? rootOption = null;

            var baseOptions = obj["Options"] as JArray;
            var rootFiles = obj["Files"] as JObject;

            if (rootFiles != null)
            {
                rootOption = new JObject
                {
                    ["Name"] = "(default)",
                    ["Files"] = rootFiles
                };
            }

            if (baseOptions != null)
                foreach (var opt in baseOptions)
                    options.Add(opt);

            if (rootOption != null)
                options.Insert(0, rootOption);

            foreach (var opt in options)
            {
                string optionName = opt["Name"]?.ToString() ?? "Option";
                var filesObj = opt["Files"] as JObject;
                if (filesObj == null)
                    continue;

                var entries = new List<ParsedEmoteOverride>();

                foreach (var kv in filesObj)
                {
                    string gamePath = kv.Key;
                    string papPath = kv.Value?.ToString() ?? "";

                    if (!TryCreateParsedEntry(groupName, optionName, gamePath, papPath, out var entry))
                        continue;

                    entries.Add(entry);
                }

                if (entries.Count == 0)
                    continue;

                // ✅ NEU: PAP-Gruppierung NACH QUELLDATEN
                var papGroups = entries
                    .GroupBy(e => e.ModdedPapPath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new PapSourceGroup
                    {
                        SourcePap = g.Key,
                        GamePaths = g.Select(e => e.GamePath).ToList()
                    })
                    .ToList();

                results.Add(new RemappableOption
                {
                    GroupName = groupName,
                    OptionName = optionName,
                    Entries = entries,
                    PapSources = papGroups,
                });
            }
        }

        return results;
    }

    private static bool TryParseJson(string file, out JObject obj)
    {
        try
        {
            obj = JObject.Parse(File.ReadAllText(file));
            return true;
        }
        catch
        {
            obj = new JObject();
            return false;
        }
    }

    private static void ScanMetaJson(JObject meta, List<RemappableOption> results)
    {
        foreach (var optionGroup in PenumbraMetadataScanner.ScanV4(meta)
                     .GroupBy(mapping => (mapping.GroupName, mapping.OptionName)))
        {
            var entries = new List<ParsedEmoteOverride>();
            foreach (var mapping in optionGroup)
            {
                if (TryCreateParsedEntry(optionGroup.Key.GroupName, optionGroup.Key.OptionName, mapping.GamePath, mapping.ModPath, out var entry))
                    entries.Add(entry);
            }

            AddRemappableOption(results, optionGroup.Key.GroupName, optionGroup.Key.OptionName, entries);
        }
    }

    private static void AddOptionFromFiles(
        List<RemappableOption> results,
        string groupName,
        string optionName,
        JObject? filesObj)
    {
        if (filesObj == null)
            return;

        var entries = new List<ParsedEmoteOverride>();

        foreach (var kv in filesObj)
        {
            string gamePath = kv.Key;
            string papPath = kv.Value?.ToString() ?? "";

            if (!TryCreateParsedEntry(groupName, optionName, gamePath, papPath, out var entry))
                continue;

            entries.Add(entry);
        }

        AddRemappableOption(results, groupName, optionName, entries);
    }

    private static void AddRemappableOption(
        List<RemappableOption> results,
        string groupName,
        string optionName,
        List<ParsedEmoteOverride> entries)
    {
        if (entries.Count == 0)
            return;

        var papGroups = entries
            .GroupBy(e => e.ModdedPapPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PapSourceGroup
            {
                SourcePap = g.Key,
                GamePaths = g.Select(e => e.GamePath).ToList()
            })
            .ToList();

        results.Add(new RemappableOption
        {
            GroupName = groupName,
            OptionName = optionName,
            Entries = entries,
            PapSources = papGroups,
        });
    }

    private static bool TryCreateParsedEntry(
        string groupName,
        string optionName,
        string gamePath,
        string papPath,
        out ParsedEmoteOverride entry)
    {
        entry = new ParsedEmoteOverride();

        if (!papPath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsEmotePapPath(gamePath))
            return false;

        var emote = EmotePathResolver.Resolve(gamePath);
        var fallbackName = Path.GetFileNameWithoutExtension(gamePath);

        entry = new ParsedEmoteOverride
        {
            GroupName = groupName,
            OptionName = optionName,
            GamePath = gamePath,
            ModdedPapPath = papPath,
            EmoteName = emote?.Name ?? fallbackName,
            EmoteCommand = emote?.Command ?? string.Empty,
            EmoteRowId = emote?.RowId ?? 0
        };

        return true;
    }

    private static bool IsEmotePapPath(string gamePath)
    {
        var normalized = gamePath.Replace('\\', '/');
        return normalized.Contains("/emote/", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(normalized).Contains("emot", StringComparison.OrdinalIgnoreCase);
    }
}
