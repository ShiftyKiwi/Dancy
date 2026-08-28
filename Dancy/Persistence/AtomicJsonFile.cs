using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dancy.Persistence;

public enum AtomicJsonWriteCheckpoint
{
    AfterTemporaryWrite,
    AfterValidation,
    BeforeReplacement,
}

public sealed class AtomicJsonWriteOptions
{
    /// <summary>
    /// Debug/test hook for exercising the real atomic writer at deterministic boundaries.
    /// Production callers leave this null.
    /// </summary>
    public Action<AtomicJsonWriteCheckpoint>? Checkpoint { get; init; }
}

public static class AtomicJsonFile
{
    public static void Write(string path, JObject document, AtomicJsonWriteOptions? options = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The JSON file does not have a parent directory.");

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backupPath = path + ".dancy.bak";
        var content = document.ToString(Formatting.Indented);

        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            options?.Checkpoint?.Invoke(AtomicJsonWriteCheckpoint.AfterTemporaryWrite);
            _ = JObject.Parse(File.ReadAllText(temporaryPath));
            options?.Checkpoint?.Invoke(AtomicJsonWriteCheckpoint.AfterValidation);
            options?.Checkpoint?.Invoke(AtomicJsonWriteCheckpoint.BeforeReplacement);

            if (!File.Exists(path))
            {
                File.Move(temporaryPath, path);
                return;
            }

            try
            {
                File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(path, backupPath, overwrite: true);
                File.Move(temporaryPath, path, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
