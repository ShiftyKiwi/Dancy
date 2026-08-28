using System;
using System.Collections.Generic;
using System.Linq;
using Dancy.Core.Models;
using Dancy.Domain;
using Lumina.Excel.Sheets;

namespace Dancy.Core
{
    /// <summary>
    /// Global in-memory cache of all player emotes for search / selection.
    /// Call EmoteLibrary.Initialize() once during plugin startup.
    /// </summary>
    public static class EmoteLibrary
    {
        public static List<LuminaEmote> AllEmotes { get; private set; } = new();
        public static List<LuminaEmote> LoopingEmotes { get; private set; } = new();

        private static readonly Dictionary<string, ResolvedEmoteInfo> EmotesByTimeline = new(StringComparer.OrdinalIgnoreCase);

        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized)
                return;

            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            if (sheet == null)
                return;

            var list = new List<LuminaEmote>();

            foreach (var emote in sheet)
            {
                if (emote.Name.IsEmpty)
                    continue;

                string name = emote.Name.ExtractText();
                string command = emote.TextCommand.ValueNullable?.Command.ToString() ?? string.Empty;

                // We only care about "real" player-emotes with a command.
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                var timelineKeys = emote.ActionTimeline
                    .Where(t => t.ValueNullable != null)
                    .Select(t => t.Value.Key.ToString())
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (timelineKeys.Count == 0)
                    continue;

                var loopTimelineKey = timelineKeys.FirstOrDefault(key =>
                    GamePathIdentity.Parse(key).Phase == AnimationPhase.Loop);
                var primaryTimelineKey = loopTimelineKey ?? timelineKeys[0];

                string category = emote.EmoteCategory.ValueNullable?.Name.ExtractText()
                                  ?? "Unknown";

                var resolved = new ResolvedEmoteInfo
                {
                    Name = name,
                    Command = command,
                    RowId = emote.RowId,
                };
                foreach (var timelineKey in timelineKeys)
                {
                    var filename = System.IO.Path.GetFileNameWithoutExtension(timelineKey);
                    if (!string.IsNullOrWhiteSpace(filename))
                        EmotesByTimeline.TryAdd(filename, resolved);
                }

                list.Add(new LuminaEmote
                {
                    Name = name,
                    Command = command,
                    RowId = emote.RowId,
                    PrimaryTimelineKey = primaryTimelineKey,
                    TimelineKeys = timelineKeys,
                    IsLoopCapable = loopTimelineKey != null,
                    PrimaryPhase = GamePathIdentity.Parse(primaryTimelineKey).Phase,
                    Category = category,
                });
            }

            AllEmotes = list
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            LoopingEmotes = AllEmotes.Where(e => e.IsLoopCapable).ToList();

            _initialized = true;
        }

        public static ResolvedEmoteInfo? ResolveTimeline(string gamePath)
        {
            var filename = System.IO.Path.GetFileNameWithoutExtension(gamePath);
            return !string.IsNullOrWhiteSpace(filename)
                   && EmotesByTimeline.TryGetValue(filename, out var emote)
                ? emote
                : null;
        }
    }
}
