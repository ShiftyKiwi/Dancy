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
                Path.GetFileName(f)!.Contains("yuck's dancy", StringComparison.OrdinalIgnoreCase))
                ?? Path.Combine(modFolder, $"group_{groupFiles.Length + 1:D3}yucksdancy.json");

            SoundyJsonRoot group;

            if (File.Exists(targetFilePath))
                group = JsonConvert.DeserializeObject<SoundyJsonRoot>(File.ReadAllText(targetFilePath)) ?? new SoundyJsonRoot();
            else
                group = new SoundyJsonRoot { Name = "Yuck\'s Dancy", Options = new List<Option>(), Type = "Multi", Priority = 9999 };

            var opt = new Option
            {
                Name = $"({originalOption.GroupName}) {originalOption.OptionName} → {replacementEmote.Name}",
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
            IReadOnlyList<string> targetGamePaths,
            string newPapRel)
        {
            var groupFiles = Directory.GetFiles(modFolder, "group_*.json", SearchOption.TopDirectoryOnly);
            var targetGroup = groupFiles
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                    .Contains("yuck's dancy", StringComparison.OrdinalIgnoreCase));

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
                $"({originalOption.GroupName}) {originalOption.OptionName} → {replacementEmote.Name}";

            // Build file dict: TARGET game path -> Dancy PAP
            var optionFiles = new Dictionary<string, string>();
            foreach (var gamePath in targetGamePaths)
            {
                optionFiles[gamePath] = newPapRel;
            }

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
