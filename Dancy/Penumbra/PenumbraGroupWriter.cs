using Dancy.Core.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Dancy.Penumbra
{
    public static class PenumbraGroupWriter
    {
        public static void CreateOrUpdateDancyGroup(
            string modFolder,
            RemappableOption originalOption,
            LuminaEmote replacementEmote,
            Dictionary<string, string> finalMappings)
        {
            var groupFiles = Directory.GetFiles(modFolder, "group_*.json", SearchOption.TopDirectoryOnly);
            string targetFilePath = groupFiles.FirstOrDefault(f =>
                Path.GetFileName(f)!.Contains("yucksdancy", StringComparison.OrdinalIgnoreCase))
                ?? Path.Combine(modFolder, $"group_{groupFiles.Length + 1:D3}yucksdancy.json");

            SoundyJsonRoot group;

            if (File.Exists(targetFilePath))
                group = JsonConvert.DeserializeObject<SoundyJsonRoot>(File.ReadAllText(targetFilePath)) ?? new SoundyJsonRoot();
            else
                group = new SoundyJsonRoot { Name = "Yuck\'s Dancy", Options = new List<Option>(), Type = "Multi", Priority = 9999 };

            var opt = new Option
            {
                Name = $"({originalOption.GroupName}) {originalOption.OptionName} -> {replacementEmote.Name}",
                Description = $"Override using {replacementEmote.Name} ({replacementEmote.Command})",
                Files = finalMappings,
                FileSwaps = new Dictionary<string, string>(),
                Manipulations = new List<object>()
            };

            group.Options!.Add(opt);

            File.WriteAllText(targetFilePath, JsonConvert.SerializeObject(group, Formatting.Indented));
        }


        public static void CreateOrUpdateDancyGroupOld(
            string modFolder,
            RemappableOption originalOption,
            LuminaEmote replacementEmote,
            IReadOnlyDictionary<string, string> finalMappings)
        {
            var metaPath = Path.Combine(modFolder, "meta.json");
            if (TryCreateOrUpdateMetaGroup(metaPath, originalOption, replacementEmote, finalMappings))
                return;

            var groupFiles = Directory.GetFiles(modFolder, "group_*.json", SearchOption.TopDirectoryOnly);
            var targetGroup = groupFiles
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                    .Contains("yucksdancy", StringComparison.OrdinalIgnoreCase));

            SoundyJsonRoot group;
            string targetFilePath;

            if (targetGroup != null)
            {
                targetFilePath = targetGroup;
                var json = File.ReadAllText(targetGroup);
                group = JsonConvert.DeserializeObject<SoundyJsonRoot>(json) ?? new SoundyJsonRoot();
            }
            else
            {
                string index = (groupFiles.Length + 1).ToString("D3");
                string fileName = $"group_{index}_yucksdancy.json";
                targetFilePath = Path.Combine(modFolder, fileName);

                group = new SoundyJsonRoot
                {
                    Version = "1.0.0",
                    Name = "Yuck\'s Dancy",
                    Description = "Created by Dancy",
                    Type = "Multi",
                    Priority = 9999,
                    DefaultSettings = 0,
                    Options = new List<Option>(),
                    Files = new Dictionary<string, string>(),
                };
            }

            // Normalize internal collections
            group.Options ??= new List<Option>();
            foreach (var opt in group.Options)
            {
                opt.Files ??= new Dictionary<string, string>();
                opt.FileSwaps ??= new Dictionary<string, string>();
                opt.Manipulations ??= new List<object>();
            }

            // Build option name
            string optionName =
                $"({originalOption.GroupName}) {originalOption.OptionName} -> {replacementEmote.Name}";

            // Build file dict: TARGET game path -> Dancy PAP
            var optionFiles = finalMappings
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            var newOption = new Option
            {
                Name = optionName,
                Description = $"Override using {replacementEmote.Name} ({replacementEmote.Command})",
                Files = optionFiles,
                FileSwaps = new Dictionary<string, string>(),
                Manipulations = new List<object>(),
            };

            group.Options.Add(newOption);

            File.WriteAllText(
                targetFilePath,
                JsonConvert.SerializeObject(group, Formatting.Indented)
            );
        }

        private static bool TryCreateOrUpdateMetaGroup(
            string metaPath,
            RemappableOption originalOption,
            LuminaEmote replacementEmote,
            IReadOnlyDictionary<string, string> finalMappings)
        {
            if (!File.Exists(metaPath))
                return false;

            Newtonsoft.Json.Linq.JObject meta;
            try
            {
                meta = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(metaPath));
            }
            catch
            {
                return false;
            }

            var fileVersion = meta["FileVersion"]?.ToObject<int?>() ?? 0;
            if (fileVersion < 4)
                return false;

            var groups = meta["Groups"] as Newtonsoft.Json.Linq.JArray;
            if (groups == null)
            {
                groups = new Newtonsoft.Json.Linq.JArray();
                meta["Groups"] = groups;
            }

            var group = groups
                .OfType<Newtonsoft.Json.Linq.JObject>()
                .FirstOrDefault(g => string.Equals(g["Name"]?.ToString(), "Yuck's Dancy", StringComparison.OrdinalIgnoreCase));

            if (group == null)
            {
                group = new Newtonsoft.Json.Linq.JObject
                {
                    ["Type"] = "Multi",
                    ["Id"] = Guid.NewGuid().ToString(),
                    ["Name"] = "Yuck's Dancy",
                    ["Description"] = "Created by Dancy",
                    ["Priority"] = 9999,
                    ["DefaultSettings"] = 0,
                    ["Options"] = new Newtonsoft.Json.Linq.JArray()
                };

                groups.Add(group);
            }

            var options = group["Options"] as Newtonsoft.Json.Linq.JArray;
            if (options == null)
            {
                options = new Newtonsoft.Json.Linq.JArray();
                group["Options"] = options;
            }

            var optionFiles = new Newtonsoft.Json.Linq.JObject();
            foreach (var (gamePath, newPapRel) in finalMappings)
                optionFiles[gamePath] = newPapRel;

            var optionName = $"({originalOption.GroupName}) {originalOption.OptionName} -> {replacementEmote.Name}";
            options.Add(new Newtonsoft.Json.Linq.JObject
            {
                ["Id"] = Guid.NewGuid().ToString(),
                ["Name"] = optionName,
                ["Description"] = $"Override using {replacementEmote.Name} ({replacementEmote.Command})",
                ["Files"] = optionFiles
            });

            File.WriteAllText(metaPath, meta.ToString(Formatting.Indented));
            return true;
        }

        // same helper types as before
        public class SoundyJsonRoot
        {
            public string? Version { get; set; }
            public string? Name { get; set; } = "";

            public Dictionary<string, string>? Files = new Dictionary<string, string>();
            public string? Description { get; set; }
            public int? Priority { get; set; }
            public string? Type { get; set; } = "";
            public int? DefaultSettings { get; set; }
            public List<Option>? Options { get; set; } = new List<Option>();
        }

        public class Option
        {
            public string? Name { get; set; } = "";
            public string? Description { get; set; }
            public Dictionary<string, string>? Files { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, string>? FileSwaps { get; set; } = new Dictionary<string, string>();
            public List<object>? Manipulations { get; set; } = new List<object>();
        }
    }
}
