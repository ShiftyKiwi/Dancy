using System;
using System.Collections.Generic;
using System.Linq;
using Dancy.Core.Models;
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

                // Try to find a loop timeline first.
                string primaryTimelineKey = string.Empty;

                var loopTimeline = emote.ActionTimeline
                    .FirstOrDefault(t =>
                        t.ValueNullable != null &&
                        t.Value.Key.ToString().Contains("loop", StringComparison.OrdinalIgnoreCase));

                if (loopTimeline.ValueNullable != null)
                {
                    primaryTimelineKey = loopTimeline.Value.Key.ToString();
                }
                else
                {
                    // Fallback: any valid timeline key.
                    var anyTimeline = emote.ActionTimeline
                        .FirstOrDefault(t => t.ValueNullable != null);

                    if (anyTimeline.ValueNullable != null)
                        primaryTimelineKey = anyTimeline.Value.Key.ToString();
                }

                string category = emote.EmoteCategory.ValueNullable?.Name.ExtractText()
                                  ?? "Unknown";

                list.Add(new LuminaEmote
                {
                    Name = name,
                    Command = command,
                    RowId = emote.RowId,
                    PrimaryTimelineKey = primaryTimelineKey,
                    Category = category,
                });
            }

            AllEmotes = list
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _initialized = true;
        }
    }
}
