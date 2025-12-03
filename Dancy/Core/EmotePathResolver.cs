using Dancy.Core.Models;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.IO;

namespace Dancy.Core;

public static class EmotePathResolver
{
    public static ResolvedEmoteInfo? Resolve(string gamePath)
    {
        string file = Path.GetFileNameWithoutExtension(gamePath);
        if (string.IsNullOrEmpty(file))
            return null;

        string cleaned = file;

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
        if (sheet == null)
            return null;

        foreach (var emote in sheet)
        {
            foreach (var timeline in emote.ActionTimeline)
            {
                if (timeline.ValueNullable == null)
                    continue;

                string key = timeline.Value.Key.ToString();
                string keyName = Path.GetFileNameWithoutExtension(key);

                if (string.Equals(cleaned, keyName, StringComparison.OrdinalIgnoreCase))
                {
                    return new ResolvedEmoteInfo
                    {
                        Name = emote.Name.ExtractText(),
                        Command = emote.TextCommand.ValueNullable?.Command.ToString() ?? "",
                        RowId = emote.RowId
                    };
                }
            }
        }

        return null;
    }
}
