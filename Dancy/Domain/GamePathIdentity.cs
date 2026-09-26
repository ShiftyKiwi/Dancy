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
