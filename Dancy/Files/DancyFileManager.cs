using System;
using System.IO;
using System.Linq;
using Dancy.Penumbra;
using Dancy.Persistence;
using Newtonsoft.Json.Linq;

namespace Dancy.Files;

public static class DancyFileManager
{
    public static bool DancyExists(string modPath)
    {
        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
            return false;

        if (TryReadMeta(modPath, out var meta)
            && meta["Groups"] is JArray groups
            && groups.OfType<JObject>().Any(DancyMetadataMutator.IsDancyGroup))
        {
            return true;
        }

        return Directory.GetFiles(modPath, "group_*.json", SearchOption.TopDirectoryOnly)
                   .Any(path => Path.GetFileNameWithoutExtension(path)
                       .Contains("yucksdancy", StringComparison.OrdinalIgnoreCase))
               || PathSafety.TryResolveInsideRoot(modPath, "yucksdancy", out var dancyDirectory)
                  && Directory.Exists(dancyDirectory);
    }

    public static bool RemoveDancy(string modPath)
    {
        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
            return false;

        var changed = false;
        var metaPath = Path.Combine(modPath, "meta.json");
        if (TryReadMeta(modPath, out var meta) && meta["Groups"] is JArray groups)
        {
            var dancyGroups = groups.OfType<JObject>()
                .Where(DancyMetadataMutator.IsDancyGroup)
                .ToList();
            if (dancyGroups.Count > 0)
            {
                foreach (var group in dancyGroups)
                    group.Remove();

                try
                {
                    AtomicJsonFile.Write(metaPath, meta);
                    changed = true;
                }
                catch
                {
                    // Do not remove generated PAPs when the metadata change was not persisted.
                    return false;
                }
            }
        }

        foreach (var groupFile in Directory.GetFiles(modPath, "group_*.json", SearchOption.TopDirectoryOnly)
                     .Where(path => Path.GetFileNameWithoutExtension(path)
                         .Contains("yucksdancy", StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(groupFile);
            changed = true;
        }

        if (PathSafety.TryResolveInsideRoot(modPath, "yucksdancy", out var dancyDirectory)
            && Directory.Exists(dancyDirectory))
        {
            Directory.Delete(dancyDirectory, recursive: true);
            changed = true;
        }

        return changed;
    }

    private static bool TryReadMeta(string modPath, out JObject meta)
    {
        try
        {
            meta = JObject.Parse(File.ReadAllText(Path.Combine(modPath, "meta.json")));
            return true;
        }
        catch
        {
            meta = new JObject();
            return false;
        }
    }
}
