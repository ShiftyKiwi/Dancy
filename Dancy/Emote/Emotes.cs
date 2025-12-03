using Dancy.Pap;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dancy.Emote
{
    public static class Emotes
    {
        public static string GetEmote(string input)
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            if (sheet == null)
                return "Action sheet not found.";

            foreach (var action in sheet)
            {
                string? name = action.Name.IsEmpty ? null : action.Name.ExtractText();
                if (name == null) continue;
                if (!name.Contains(input, StringComparison.OrdinalIgnoreCase)) continue;

                var keys = action.ActionTimeline
                    .Where(x => x.ValueNullable != null)
                    .Select(x => (Key: x.Value.Key.ToString(), LoadType: x.Value.LoadType))
                    .ToList();

                string? selectedKey = PapResolver.SelectBestKey(keys);
                var papPaths = selectedKey != null
                    ? PapResolver.ResolvePapFiles(selectedKey)
                    : new List<string>();

                string keysJoined = keys.Count > 0
                    ? string.Join(", ", keys.Select(k => $"{k.Key} (LoadType {k.LoadType})"))
                    : "None";

                string papSummary = papPaths.Count > 0
                    ? string.Join("\n", papPaths)
                    : "No valid PAP files found.";

                string category = action.EmoteCategory.ValueNullable.HasValue
                    ? action.EmoteCategory.Value.Name.ExtractText()
                    : "Unknown Category";

                string command = action.TextCommand.ValueNullable == null
                    ? ""
                    : action.TextCommand.Value.Command.ExtractText();

                return
                    $"{name} ({action.RowId})\n" +
                    $"Category: {category}\n" +
                    $"Command: {command}\n" +
                    $"Timeline Keys: {keysJoined}\n" +
                    $"Selected TimelineKey: {selectedKey ?? "None"}\n" +
                    $"Valid PAP Files:\n{papSummary}\n"+
                    $"{action.ActionTimeline[0].Value}";
            }

            return "Emote not found.";
        }


    }
}
