using System.Collections.Generic;
using Newtonsoft.Json;

namespace Dancy.Files
{
    /// <summary>
    /// Repräsentiert eine Penumbra-Gruppen-JSON (group_*.json).
    /// Die Struktur ist kompatibel zu deinen Soundy-JSONs.
    /// </summary>
    public class PenumbraGroupJson
    {
        [JsonProperty("Version")]
        public string? Version { get; set; }

        [JsonProperty("Name")]
        public string? Name { get; set; } = "";

        [JsonProperty("Files")]
        public Dictionary<string, string>? Files { get; set; } = new();

        [JsonProperty("Description")]
        public string? Description { get; set; }

        [JsonProperty("Priority")]
        public int? Priority { get; set; }

        [JsonProperty("Type")]
        public string? Type { get; set; } = "";

        [JsonProperty("DefaultSettings")]
        public int? DefaultSettings { get; set; }

        [JsonProperty("Options")]
        public List<PenumbraOption>? Options { get; set; } = new();
    }

    public class PenumbraOption
    {
        [JsonProperty("Name")]
        public string? Name { get; set; } = "";

        [JsonProperty("Description")]
        public string? Description { get; set; }

        [JsonProperty("Files")]
        public Dictionary<string, string>? Files { get; set; } = new();

        [JsonProperty("FileSwaps")]
        public Dictionary<string, string>? FileSwaps { get; set; } = new();

        [JsonProperty("Manipulations")]
        public List<object>? Manipulations { get; set; } = new();
    }
}
