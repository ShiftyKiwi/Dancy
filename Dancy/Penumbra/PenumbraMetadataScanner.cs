using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Dancy.Penumbra;

public sealed record PenumbraMetadataFileMapping(string GroupName, string OptionName, string GamePath, string ModPath);

public static class PenumbraMetadataScanner
{
    public static IReadOnlyList<PenumbraMetadataFileMapping> ScanV4(JObject meta)
    {
        var mappings = new List<PenumbraMetadataFileMapping>();
        AddFiles(mappings, meta["Name"]?.ToString() ?? "Mod", "(default)", meta["DefaultData"]?["Files"] as JObject);

        if (meta["Groups"] is not JArray groups)
            return mappings;

        foreach (var group in groups.OfType<JObject>())
        {
            var groupName = group["Name"]?.ToString() ?? "Group";
            if (group["Options"] is JArray options)
            {
                foreach (var option in options.OfType<JObject>())
                    AddFiles(mappings, groupName, option["Name"]?.ToString() ?? "Option", option["Files"] as JObject);
            }

            if (group["Containers"] is not JArray containers)
                continue;

            foreach (var (container, index) in containers.OfType<JObject>().Select((value, index) => (value, index)))
            {
                var optionName = container["Name"]?.ToString();
                AddFiles(mappings, groupName, string.IsNullOrWhiteSpace(optionName) ? $"Container {index + 1}" : optionName, container["Files"] as JObject);
            }
        }

        return mappings;
    }

    private static void AddFiles(List<PenumbraMetadataFileMapping> mappings, string groupName, string optionName, JObject? files)
    {
        if (files == null)
            return;

        foreach (var (gamePath, modPath) in files)
            mappings.Add(new PenumbraMetadataFileMapping(groupName, optionName, gamePath, modPath?.ToString() ?? string.Empty));
    }
}
