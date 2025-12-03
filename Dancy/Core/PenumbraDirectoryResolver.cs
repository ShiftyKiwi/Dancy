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
    {
        var configDir = Plugin.PluginInterface.ConfigDirectory.FullName;
        string? parent = Directory.GetParent(configDir)?.FullName;

        if (string.IsNullOrWhiteSpace(parent))
            return null;

        string penumbraJson = Path.Combine(parent, "Penumbra.json");
        if (!File.Exists(penumbraJson))
            return null;

        try
        {
            var cfg = JsonConvert.DeserializeObject<PenumbraJsonConfig>(File.ReadAllText(penumbraJson));
            return cfg?.ModDirectory;
        }
        catch
        {
            return null;
        }
    }
}
