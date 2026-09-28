using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using VfxEditor.TmbFormat;
using VfxEditor.TmbFormat.Entries;

namespace Dancy.Pap;

/// <summary>
/// Read-only PAP structure inspection. It records raw TMB identity and the
/// C009 references exposed by VFXEditor's parser, but never opens Havok or
/// modifies a byte of the source asset.
/// </summary>
public static class PapStructureInspector
{
    public static PapStructure InspectFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return InspectBytes(path, PapFileInspector.ReadFileWithRetry(path));
    }

    public static PapStructure InspectTarget(string gamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        var file = Plugin.DataManager.GetFile(gamePath)
            ?? throw new FileNotFoundException($"File {gamePath} was not found in game data.");
        return InspectBytes(gamePath, ReadAllBytes(file.Reader.BaseStream));
    }

    public static PapStructure InspectBytes(string sourceIdentity, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        ArgumentNullException.ThrowIfNull(bytes);

        var inspection = PapFileInspector.Inspect(bytes);
        var sections = inspection.TimelineSections.Select(location =>
        {
            var raw = bytes.AsSpan(location.Offset, location.Size).ToArray();
            var events = ReadEventIdentifiers(raw);
            return new PapTimelineSection(
                location.Index,
                location.Offset,
                location.Size,
                Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
                events.Identifiers,
                events.Issue);
        }).ToList();
        return PapStructure.FromInspection(sourceIdentity, inspection, sections);
    }

    private static TimelineEventInspection ReadEventIdentifiers(byte[] timelineBytes)
    {
        try
        {
            using var stream = new MemoryStream(timelineBytes);
            using var reader = new BinaryReader(stream);
            var timeline = new TmbFile(reader, null!, verify: false);
            try
            {
                var pathField = typeof(C009).GetField("Path", BindingFlags.Instance | BindingFlags.NonPublic);
                return new TimelineEventInspection(
                    timeline.AllEntries
                        .OfType<C009>()
                        .Select(entry => pathField?.GetValue(entry) as TmbOffsetString)
                        .Where(path => path is not null && !string.IsNullOrWhiteSpace(path.Value))
                        .Select(path => path!.Value)
                        .ToList(),
                    null);
            }
            finally
            {
                timeline.Dispose();
            }
        }
        catch (Exception exception)
        {
            return new TimelineEventInspection(
                Array.Empty<string>(),
                $"VFXEditor TMB inspection unavailable: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private sealed record TimelineEventInspection(IReadOnlyList<string> Identifiers, string? Issue);

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.CanSeek)
            stream.Position = 0;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
