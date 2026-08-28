using Newtonsoft.Json;
using System.IO;

namespace Dancy.Core;

public class PenumbraJsonConfig
{
    [JsonProperty("ModDirectory")]
    public string? ModDirectory { get; set; }
}

public static class PenumbraDirectoryResolver
{
    public static string? GetPenumbraDirectory()
        => GetPenumbraDirectory(Plugin.PluginInterface.ConfigDirectory.FullName);

    public static string? GetPenumbraDirectory(string dalamudConfigDirectory)
    {
        var parent = Directory.GetParent(dalamudConfigDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
            return null;

        var configPaths = new[]
        {
            Path.Combine(parent, "Penumbra", "config", "penumbra.json"),
            Path.Combine(parent, "Penumbra.json"),
        };

        foreach (var configPath in configPaths)
        {
            if (!File.Exists(configPath))
                continue;

            try
            {
                var config = JsonConvert.DeserializeObject<PenumbraJsonConfig>(File.ReadAllText(configPath));
                if (!string.IsNullOrWhiteSpace(config?.ModDirectory))
                    return config.ModDirectory;
            }
            catch
            {
                // Try the legacy config path if the preferred path cannot be read.
            }
        }

        return null;
    }
}
