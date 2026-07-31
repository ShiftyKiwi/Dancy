using Dancy.Core.Models;
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
                    IsSafeToRemap = true // nur noch UI Hint
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
        var modName = meta["Name"]?.ToString() ?? "Mod";

        AddOptionFromFiles(
            results,
            modName,
            "(default)",
            meta["DefaultData"]?["Files"] as JObject);

        if (meta["Groups"] is not JArray groups)
            return;

        foreach (var groupToken in groups.OfType<JObject>())
        {
            var groupName = groupToken["Name"]?.ToString() ?? "Group";

            if (groupToken["Options"] is JArray options)
            {
                foreach (var optionToken in options.OfType<JObject>())
                {
                    var optionName = optionToken["Name"]?.ToString() ?? "Option";
                    AddOptionFromFiles(results, groupName, optionName, optionToken["Files"] as JObject);
                }
            }

            if (groupToken["Containers"] is not JArray containers)
                continue;

            foreach (var containerToken in containers.OfType<JObject>().Select((Token, Index) => (Token, Index)))
            {
                var optionName = containerToken.Token["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(optionName))
                    optionName = $"Container {containerToken.Index + 1}";

                AddOptionFromFiles(results, groupName, optionName, containerToken.Token["Files"] as JObject);
            }
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
            IsSafeToRemap = true
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
