using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ECommons.ChatMethods;
using ECommons.DalamudServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dancy.Files
{
    /// <summary>
    /// Einzelner PAP-Trefffer in einer Mod:
    /// - JsonFile: welche group_*.json
    /// - GroupName: Name der Gruppe (aus JSON)
    /// - OptionName: Name der Option oder "(root)"
    /// - GamePath: Ingame-Route (Key im Files-Dict)
    /// - ModRelativePath: Pfad zur PAP-Datei innerhalb der Mod
    /// </summary>
    public sealed class DancyPapReference
    {
        public string JsonFile { get; init; } = string.Empty;
        public string GroupName { get; init; } = string.Empty;
        public string OptionName { get; init; } = string.Empty;
        public string GamePath { get; init; } = string.Empty;
        public string ModRelativePath { get; init; } = string.Empty;
    }

    public static class DancyFileManager
    {
        /// <summary>
        /// Scannt einen Penumbra-Mod-Ordner nach PAP-Referenzen in allen group_*.json Dateien.
        /// Wir fassen nichts an, wir lesen nur.
        /// </summary>
        public static List<DancyPapReference> ScanForPapReferences(string modRootPath)
        {
            var result = new List<DancyPapReference>();

            if (string.IsNullOrWhiteSpace(modRootPath) || !Directory.Exists(modRootPath))
                return result;

            var groupFiles = Directory.GetFiles(modRootPath, "*.json", SearchOption.TopDirectoryOnly);

            foreach (var file in groupFiles)
            {
                PenumbraGroupJson? root;
                try
                {
                    var json = File.ReadAllText(file);
                    root = JsonConvert.DeserializeObject<PenumbraGroupJson>(json);
                }
                catch
                {
                    // Ungültige JSON ignorieren
                    continue;
                }

                if (root == null)
                    continue;

                var groupName = root.Name ?? Path.GetFileNameWithoutExtension(file);

                // 1) Root.Files (Optionsebene "global")
                if (root.Files != null)
                {
                    foreach (var kv in root.Files)
                    {
                        var gamePath = kv.Key;
                        var modPath = kv.Value ?? string.Empty;

                        if (modPath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(new DancyPapReference
                            {
                                JsonFile = file,
                                GroupName = groupName,
                                OptionName = "(root)",
                                GamePath = gamePath,
                                ModRelativePath = modPath
                            });
                        }
                    }
                }

                // 2) Options[*].Files
                if (root.Options != null)
                {
                    foreach (var opt in root.Options)
                    {
                        if (opt.Files == null)
                            continue;

                        var optionName = string.IsNullOrWhiteSpace(opt.Name) ? "(no name)" : opt.Name!;

                        foreach (var kv in opt.Files)
                        {
                            var gamePath = kv.Key;
                            var modPath = kv.Value ?? string.Empty;

                            if (!modPath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                                continue;

                            result.Add(new DancyPapReference
                            {
                                JsonFile = file,
                                GroupName = groupName,
                                OptionName = optionName,
                                GamePath = gamePath,
                                ModRelativePath = modPath
                            });
                        }
                    }
                }
            }

            // optional: duplizierte Einträge zusammenfassen
            return result
                .OrderBy(r => r.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.OptionName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.GamePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool DancyExists(string modPath)
        {
            if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
                return false;

            if (TryReadMeta(modPath, out var meta)
                && meta["Groups"] is JArray groups
                && groups.OfType<JObject>().Any(IsDancyGroup))
                return true;
            
            bool dancyGroupFound = Directory.GetFiles(modPath, "group_*.json", SearchOption.TopDirectoryOnly)
                .Any(f => Path.GetFileNameWithoutExtension(f)
                    .Contains("yucksdancy", StringComparison.OrdinalIgnoreCase));

            bool directoryFound = Directory.GetDirectories(modPath, "yucksdancy", SearchOption.TopDirectoryOnly)
                .Any();

            return dancyGroupFound || directoryFound;

        }

        public static bool RemoveDancy(string modPath)
        {
            if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
                return false;

            bool anyDeleted = false;
            var metaPath = Path.Combine(modPath, "meta.json");
            if (TryReadMeta(modPath, out var meta) && meta["Groups"] is JArray groups)
            {
                var dancyGroups = groups
                    .OfType<JObject>()
                    .Where(IsDancyGroup)
                    .ToList();

                foreach (var group in dancyGroups)
                    group.Remove();

                if (dancyGroups.Count > 0)
                {
                    try
                    {
                        File.WriteAllText(metaPath, meta.ToString(Formatting.Indented));
                        anyDeleted = true;
                    }
                    catch
                    {
                        // Ignorieren
                    }
                }
            }

            // 1) group_*.json Dateien mit "dancy" im Namen löschen
            var groupFiles = Directory.GetFiles(modPath, "group_*.json", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileNameWithoutExtension(f)
                    .Contains("yucksdancy", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var file in groupFiles)
            {
                try
                {
                    File.Delete(file);
                    anyDeleted = true;
                }
                catch
                {
                    // Ignorieren
                }
            }
            // 2) "dancy"-Verzeichnis löschen
            var dancyDirs = Directory.GetDirectories(modPath, "yucksdancy", SearchOption.TopDirectoryOnly);
            foreach (var dir in dancyDirs)
            {
                try
                {
                    Directory.Delete(dir, true);
                    anyDeleted = true;
                }
                catch
                {
                    // Ignorieren
                }
            }
            return anyDeleted;
        }

        private static bool TryReadMeta(string modPath, out JObject meta)
        {
            var metaPath = Path.Combine(modPath, "meta.json");
            try
            {
                meta = JObject.Parse(File.ReadAllText(metaPath));
                return true;
            }
            catch
            {
                meta = new JObject();
                return false;
            }
        }

        private static bool IsDancyGroup(JObject group)
            => string.Equals(group["Name"]?.ToString(), "Yuck's Dancy", StringComparison.OrdinalIgnoreCase);
    }

}
