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
            IReadOnlyList<string> targetGamePaths,
            string newPapRel)
        {
            var groupFiles = Directory.GetFiles(modFolder, "group_*.json", SearchOption.TopDirectoryOnly);
            var targetGroup = groupFiles
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                    .Contains("dancy", StringComparison.OrdinalIgnoreCase));

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
                string fileName = $"group_{index}_Dancy.json";
                targetFilePath = Path.Combine(modFolder, fileName);

                group = new SoundyJsonRoot
                {
                    Version = "1.0.0",
                    Name = "Dancy",
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
