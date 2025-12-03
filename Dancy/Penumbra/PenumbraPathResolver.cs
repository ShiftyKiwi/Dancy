using System;
using System.IO;
using Newtonsoft.Json;

namespace Dancy.Penumbra
{
    public static class PenumbraPathResolver
    {
        public class PenumbraJson
        {
            [JsonProperty("ModDirectory")]
            public string? ModDirectory { get; set; }
        }

        /// <summary>
        /// Attempts to read Penumbra.json and extract the ModDirectory path.
        /// Returns null if not found or invalid.
        /// </summary>
        public static string? ResolvePenumbraModDirectory(string dalamudConfigDirectory)
        {
            try
            {
                // Navigate into parent folder of Dalamud config
                var parentDir = Directory.GetParent(dalamudConfigDirectory)?.FullName;
                if (string.IsNullOrEmpty(parentDir))
                    return null;

                var penumbraJsonPath = Path.Combine(parentDir, "Penumbra.json");
                if (!File.Exists(penumbraJsonPath))
                    return null;

                var json = File.ReadAllText(penumbraJsonPath);
                var config = JsonConvert.DeserializeObject<PenumbraJson>(json);

                if (config == null || string.IsNullOrWhiteSpace(config.ModDirectory))
                    return null;

                return config.ModDirectory;
            }
            catch
            {
                return null;
            }
        }
    }
}
