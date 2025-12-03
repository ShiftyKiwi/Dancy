using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dancy.Pap
{
    public static class PapResolver
    {
        private static readonly string[] RaceIds =
        {
        "c0101","c0201","c0301","c0401","c0501","c0601",
        "c0701","c0801","c0901","c1001","c1101","c1201",
        "c1301","c1401","c1501","c1601","c1701","c1801"
    };

        private static readonly string[] AnimationLayers = { "a0001", "a0002" };

        private static readonly string[] Subfolders =
        {
        "",              // direkt
        "bt_common/",    // Emotes
        "resident/",
        "nonresident/"
    };

        public static string? SelectBestKey(List<(string Key, byte LoadType)> keys)
        {
            if (keys.Count == 0) return null;

            // 1) Loop bevorzugen
            var loop = keys.FirstOrDefault(k =>
                k.Key.Contains("loop", StringComparison.OrdinalIgnoreCase)).Key;
            if (!string.IsNullOrEmpty(loop))
                return loop;

            // 2) Start, falls vorhanden
            var start = keys.FirstOrDefault(k =>
                k.Key.Contains("start", StringComparison.OrdinalIgnoreCase)).Key;
            if (!string.IsNullOrEmpty(start))
                return start;

            // 3) Fallback: erster Key (z.B. emote/goodbye_st bei /wave)
            return keys[0].Key;
        }

        public static List<string> ResolvePapFiles(string timelineKey)
        {
            var results = new List<string>();

            // globaler Pfad (gibt es selten, aber schadet nicht)
            string global = $"chara/animation/{timelineKey}.pap";
            if (Plugin.DataManager.FileExists(global))
                results.Add(global);

            foreach (var race in RaceIds)
            {
                foreach (var layer in AnimationLayers)
                {
                    foreach (var sub in Subfolders)
                    {
                        string path = $"chara/human/{race}/animation/{layer}/{sub}{timelineKey}.pap";
                        if (Plugin.DataManager.FileExists(path))
                            results.Add(path);
                    }
                }
            }

            return results;
        }
    }


}
