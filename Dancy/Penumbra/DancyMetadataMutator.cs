using System;
using System.Collections.Generic;
using System.Linq;
using Dancy.Domain;
using Newtonsoft.Json.Linq;

namespace Dancy.Penumbra;

public static class DancyMetadataMutator
{
    public const string GroupName = "Yuck's Dancy";
    private const string GroupId = "35ca3d5e-e3ec-467f-af95-5e4ee583b6cc";

    public static bool IsDancyGroup(JObject group)
        => string.Equals(group["Name"]?.ToString(), GroupName, StringComparison.OrdinalIgnoreCase);

    public static void UpsertV4(JObject meta, OverridePlan plan, IReadOnlyDictionary<string, string> mappings)
    {
        var groups = meta["Groups"] as JArray;
        if (groups == null)
        {
            groups = new JArray();
            meta["Groups"] = groups;
        }

        var group = groups.OfType<JObject>().FirstOrDefault(IsDancyGroup);
        if (group == null)
        {
            group = new JObject();
            groups.Add(group);
        }

        group["Type"] = "Multi";
        group["Id"] = group["Id"]?.ToString() ?? GroupId;
        group["Name"] = GroupName;
        group["Description"] = "Created and maintained by Dancy";
        group["Priority"] = group["Priority"]?.Value<int?>() ?? 9999;
        group["DefaultSettings"] = group["DefaultSettings"]?.Value<int?>() ?? 0;

        var options = group["Options"] as JArray;
        if (options == null)
        {
            options = new JArray();
            group["Options"] = options;
        }

        var duplicates = options.OfType<JObject>()
            .Where(option => string.Equals(option["Id"]?.ToString(), plan.OverrideId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var option = duplicates.FirstOrDefault();
        if (option == null)
        {
            option = new JObject();
            options.Add(option);
        }

        foreach (var duplicate in duplicates.Skip(1))
            duplicate.Remove();

        option["Id"] = plan.OverrideId;
        option["Name"] = plan.DisplayName;
        option["Description"] = plan.Description;
        option["Files"] = CreateFilesObject(mappings);
    }

    public static void UpsertLegacy(JObject group, OverridePlan plan, IReadOnlyDictionary<string, string> mappings)
    {
        group["Version"] = group["Version"]?.ToString() ?? "1.0.0";
        group["Name"] = GroupName;
        group["Description"] = "Created and maintained by Dancy";
        group["Type"] = "Multi";
        group["Priority"] = group["Priority"]?.Value<int?>() ?? 9999;
        group["DefaultSettings"] = group["DefaultSettings"]?.Value<int?>() ?? 0;

        var options = group["Options"] as JArray;
        if (options == null)
        {
            options = new JArray();
            group["Options"] = options;
        }
        var option = options.OfType<JObject>()
            .FirstOrDefault(candidate => string.Equals(candidate["DancyOverrideId"]?.ToString(), plan.OverrideId, StringComparison.OrdinalIgnoreCase));
        if (option == null)
        {
            option = new JObject();
            options.Add(option);
        }

        option["DancyOverrideId"] = plan.OverrideId;
        option["Name"] = plan.DisplayName;
        option["Description"] = plan.Description;
        option["Files"] = CreateFilesObject(mappings);
        option["FileSwaps"] = option["FileSwaps"] as JObject ?? new JObject();
        option["Manipulations"] = option["Manipulations"] as JArray ?? new JArray();
    }

    private static JObject CreateFilesObject(IReadOnlyDictionary<string, string> mappings)
    {
        var files = new JObject();
        foreach (var (gamePath, modPath) in mappings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            files[gamePath] = modPath;

        return files;
    }
}
