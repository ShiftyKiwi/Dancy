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
        var timeline = Path.GetFileNameWithoutExtension(fileName);
        if (timeline.Contains("loop", StringComparison.OrdinalIgnoreCase))
            return AnimationPhase.Loop;
        if (timeline.Contains("start", StringComparison.OrdinalIgnoreCase)
            || timeline.EndsWith("_st", StringComparison.OrdinalIgnoreCase))
            return AnimationPhase.Start;
        if (timeline.Contains("end", StringComparison.OrdinalIgnoreCase)
            || timeline.EndsWith("_ed", StringComparison.OrdinalIgnoreCase))
            return AnimationPhase.End;

        return AnimationPhase.Unknown;
    }
}
