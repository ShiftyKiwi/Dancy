using System;
using System.IO;
using System.Linq;

namespace Dancy.Domain;

public enum AnimationPhase
{
    Unknown,
    Start,
    Loop,
    End,
}

public sealed class GamePathIdentity
{
    private GamePathIdentity(string normalizedPath, string directory, string fileName, CharacterPathIdentity character, string layer, AnimationPhase phase)
    {
        NormalizedPath = normalizedPath;
        Directory = directory;
        FileName = fileName;
        Character = character;
        Layer = layer;
        Phase = phase;
    }

    public string NormalizedPath { get; }
    public string Directory { get; }
    public string FileName { get; }
    public CharacterPathIdentity Character { get; }
    public string Layer { get; }
    public AnimationPhase Phase { get; }

    public static GamePathIdentity Parse(string? path)
    {
        var normalized = Normalize(path);
        var lastSlash = normalized.LastIndexOf('/');
        var directory = lastSlash < 0 ? string.Empty : normalized[..lastSlash];
        var fileName = lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
        var layer = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(IsAnimationLayer)
            ?.ToLowerInvariant() ?? string.Empty;

        return new GamePathIdentity(
            normalized,
            directory,
            fileName,
            CharacterPathIdentity.FromGamePath(normalized),
            layer,
            DetectPhase(fileName));
    }

    public static string Normalize(string? path)
        => (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    public static bool TryReplaceCharacter(
        string sourcePath,
        CharacterPathIdentity replacement,
        out string replacedPath,
        out string error)
    {
        replacedPath = string.Empty;
        error = string.Empty;
        if (!replacement.IsKnown)
        {
            error = "The requested logical race identity is unknown.";
            return false;
        }

        var source = Parse(sourcePath);
        if (!source.Character.IsKnown)
        {
            error = "Dancy could not determine a race identity from the selected source path.";
            return false;
        }

        var segments = source.NormalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(segments, segment => string.Equals(segment, source.Character.Code, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            error = "Dancy could not form a logical race path from the selected source path.";
            return false;
        }

        segments[index] = replacement.Code;
        replacedPath = string.Join('/', segments);
        var result = Parse(replacedPath);
        if (!string.Equals(result.Character.Code, replacement.Code, StringComparison.OrdinalIgnoreCase))
        {
            replacedPath = string.Empty;
            error = "Dancy could not verify the requested logical race path.";
            return false;
        }

        return true;
    }

    private static bool IsAnimationLayer(string segment)
        => segment.Length == 5
           && segment[0] is 'a' or 'A'
           && segment.Skip(1).All(char.IsDigit);

    private static AnimationPhase DetectPhase(string fileName)
    {
        var timeline = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        // FFXIV commonly prefixes every phase in a family with "loop" (for example,
        // loop_emot08_start). Terminal phase markers must therefore win over the
        // family name: a start PAP is never a normal looping-override source.
        if (HasTerminalPhase(timeline, "start") || HasTerminalPhase(timeline, "st"))
            return AnimationPhase.Start;
        if (HasTerminalPhase(timeline, "end") || HasTerminalPhase(timeline, "ed"))
            return AnimationPhase.End;
        if (HasTerminalPhase(timeline, "loop") || HasTerminalPhase(timeline, "lp"))
            return AnimationPhase.Loop;

        return AnimationPhase.Unknown;
    }

    private static bool HasTerminalPhase(string timeline, string phase)
        => timeline.Equals(phase, StringComparison.Ordinal)
           || timeline.EndsWith($"_{phase}", StringComparison.Ordinal)
           || timeline.EndsWith($"-{phase}", StringComparison.Ordinal);
}
