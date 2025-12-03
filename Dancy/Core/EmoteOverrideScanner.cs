using Dancy.Core.Models;
using ECommons.DalamudServices;
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

        foreach (var file in Directory.GetFiles(modDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            JObject obj;
            try
            {
                obj = JObject.Parse(File.ReadAllText(file));
            }
            catch
            {
                continue;
            }

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
            {
                foreach (var opt in baseOptions)
                {
                    options.Add(opt);
                }
            }

            if (rootOption != null)
            {
                options.Insert(0, rootOption);
            }

            if (options.Count == 0)
                continue;

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

                    if (!papPath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var emote = EmotePathResolver.Resolve(gamePath);
                    if (emote == null)
                        continue;

                    entries.Add(new ParsedEmoteOverride
                    {
                        GroupName = groupName,
                        OptionName = optionName,
                        GamePath = gamePath,
                        ModdedPapPath = papPath,
                        EmoteName = emote.Name,
                        EmoteCommand = emote.Command,
                        EmoteRowId = emote.RowId
                    });
                }

                if (entries.Count == 0)
                    continue;

                bool safe = entries
                    .Select(e => e.ModdedPapPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == 1;

                results.Add(new RemappableOption
                {
                    GroupName = groupName,
                    OptionName = optionName,
                    IsSafeToRemap = safe,
                    Entries = entries
                });
            }
        }

        return results;
    }
}
