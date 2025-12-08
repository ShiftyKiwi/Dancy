using ECommons.DalamudServices;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using VfxEditor.PapFormat;
using VfxEditor.Parsing.String;
using VfxEditor.ScdFormat;
using VfxEditor.TmbFormat.Entries;

namespace Dancy.Pap;
public static class PapEditor
{
    public struct LoadedPap
    {
        public PapFile File;
        public string TempHkxPath;
    }

    public static LoadedPap LoadPap(string path)
    {
        var random = new Random();
        int rand = random.Next(9999);
        int rand2 = random.Next(9999);

        try
        {
            string hkxTemp = Path.Combine(Path.GetTempPath(), $"{rand}_{rand2}" + ".hkx");
            PapFile result;
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                // init = true: Havok-Daten werden normal gelesen
                result = new PapFile(br, path, hkxTemp, true, true);
            }
            // The temp HKX file is required later when the PapFile is
            // written back to disk. Deleting it here caused a
            // FileNotFoundException during the write step if a PAP was
            // created for an animation that previously had no sound.
            // Cleanup is handled separately by TempFileCleaner, so we
            // keep the file around for now.
            return new LoadedPap { File = result, TempHkxPath = hkxTemp };
        }
        catch (Exception ex)
        {
            throw ex;
        }

    }
    public static void ApplyOverride(string defaultPath, string papPath, string newPap)
    {

        var random = new Random();
        int rand = random.Next(9999);
        int rand2 = random.Next(9999);
        string hkxTemp = string.Empty;

        bool changedSomething = false;

        string eventIdentifier = string.Empty;

        try
        {
            var tempFile = Plugin.DataManager.GetFile(defaultPath);
            if (tempFile == null)
                throw new FileNotFoundException($"File {defaultPath} not found in game data.");

            hkxTemp = Path.Combine(Path.GetTempPath(), $"{rand}_{rand2}" + ".hkx");
            PapFile file;
            using (var br = new BinaryReader(tempFile.Reader.BaseStream))
            {
                // init = true: Havok-Daten werden normal gelesen
                file = new PapFile(br, defaultPath, hkxTemp, true, true);

                eventIdentifier = file.Animations[0].GetName();
            }
        }
        catch (Exception ex)
        {
            Svc.Chat.PrintError($"Failed to load default PAP file: {ex.Message}");
        }
        finally
        {
            if (File.Exists(hkxTemp))
            {
                File.Delete(hkxTemp);
            }
        }

        try
        {
            hkxTemp = Path.Combine(Path.GetTempPath(), $"{rand}_{rand2}" + ".hkx");
            PapFile file;

            using (var fs = File.OpenRead(papPath))
            using (var br = new BinaryReader(fs))
            {
                // defaultPath: der Ingame-Pfad, den du vorher für eventIdentifier benutzt hast
                file = new PapFile(br, defaultPath, hkxTemp, true, true);

                var actors = file.Animations[0].Tmb.Actors;

                foreach (var actor in actors)
                {
                    foreach (var track in actor.Tracks)
                    {
                        var animTimeline = track.Entries.OfType<C009>().FirstOrDefault();
                        if (animTimeline == null) continue;

                        var pathField = typeof(C009).GetField("Path",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        var pathObj = pathField?.GetValue(animTimeline) as VfxEditor.TmbFormat.TmbOffsetString;
                        if (pathObj == null) continue;

                        // TMB-Animation-Path umbiegen
                        pathObj.Value = eventIdentifier; // oder timelineKey
                        changedSomething = true;
                    }
                }

                // PapAnimation.Name umbiegen
                if (file.Animations.Count > 0)
                {
                    var anim = file.Animations[0];

                    var nameField = typeof(PapAnimation).GetField("Name",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var nameObj = nameField?.GetValue(anim) as ParsedPaddedString;

                    if (nameObj != null)
                    {
                        nameObj.Value = eventIdentifier; // oder timelineKey
                        changedSomething = true;
                    }
                    else
                    {
                        Svc.Chat.PrintError("[Dancy] Could not access PapAnimation.Name via reflection.");
                    }
                }
            }

            if (changedSomething)
            {
                using (var fsOut = File.Create(newPap)) // oder newPap, je nachdem wo deine Kopie liegt
                using (var bw = new BinaryWriter(fsOut))
                {
                    file.Write(bw);
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Chat.PrintError($"Failed to load/modify PAP file: {ex.Message}");
        }
        finally
        {
            if (File.Exists(hkxTemp))
            {
                File.Delete(hkxTemp);
            }
        }


    }

    private static LoadedPap LoadPapOnMainThread(string path)
    {
        var tcs = new TaskCompletionSource<LoadedPap>();
        Svc.Framework?.RunOnFrameworkThread(() =>
        {
            try
            {
                tcs.SetResult(LoadPap(path));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }
}
