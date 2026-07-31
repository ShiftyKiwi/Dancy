using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using VfxEditor.TmbFormat;
using VfxEditor.TmbFormat.Entries;

namespace Dancy.Pap;

public static class PapEditor
{
    private const int PapMagic = 0x20706170;
    private const int PapInfoOffsetPosition = 14;
    private const int PapHavokOffsetPosition = 18;
    private const int PapTimelineOffsetPosition = 22;
    private const int PapHeaderSize = 26;
    private const int PapAnimationHeaderSize = 40;
    private const int PapAnimationNameSize = 32;

    public static void ApplyOverride(string defaultPath, string papPath, string newPap)
    {
        try
        {
            var defaultFile = Plugin.DataManager.GetFile(defaultPath);
            if (defaultFile == null)
                throw new FileNotFoundException($"File {defaultPath} not found in game data.");

            var defaultBytes = ReadAllBytes(defaultFile.Reader.BaseStream);
            var eventIdentifier = ReadPapAnimationName(defaultBytes, 0);
            if (string.IsNullOrWhiteSpace(eventIdentifier))
            {
                eventIdentifier = Path.GetFileNameWithoutExtension(NormalizeGamePath(defaultPath));
                if (string.IsNullOrWhiteSpace(eventIdentifier))
                    throw new InvalidDataException($"Could not infer animation name from {defaultPath}.");

                Svc.Log.Warning($"[Dancy] Could not read animation name from {defaultPath}; using {eventIdentifier}.");
            }

            var sourceBytes = File.ReadAllBytes(papPath);
            var patchedBytes = PatchPap(sourceBytes, eventIdentifier);

            Directory.CreateDirectory(Path.GetDirectoryName(newPap)!);
            File.WriteAllBytes(newPap, patchedBytes);
        }
        catch (Exception ex)
        {
            Svc.Chat.PrintError($"Failed to load/modify PAP file: {ex.Message}");
            Svc.Log.Error(ex, $"[Dancy] Failed to patch PAP. Default={defaultPath}, Source={papPath}, Output={newPap}");
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] PatchPap(byte[] papBytes, string eventIdentifier)
    {
        ValidatePapMagic(papBytes);

        var animationCount = ReadAnimationCount(papBytes);
        if (animationCount <= 0)
            throw new InvalidDataException("PAP contains no animations.");

        var animationHeaderOffset = ReadInt32(papBytes, PapInfoOffsetPosition);
        var originalHkxOffset = ReadInt32(papBytes, PapHavokOffsetPosition);
        var originalTmbOffset = ReadInt32(papBytes, PapTimelineOffsetPosition);
        if (animationHeaderOffset <= 0 || originalHkxOffset <= animationHeaderOffset || originalTmbOffset <= originalHkxOffset)
            throw new InvalidDataException("PAP header offsets are invalid.");

        var animationHeaders = ReadAnimationHeaders(papBytes, animationHeaderOffset, animationCount);
        WritePaddedString(animationHeaders[0], 0, PapAnimationNameSize, eventIdentifier);

        var hkxData = new byte[originalTmbOffset - originalHkxOffset];
        Buffer.BlockCopy(papBytes, originalHkxOffset, hkxData, 0, hkxData.Length);

        var tmbOffsetMod = originalTmbOffset % 4;
        var tmbSections = ReadTmbSections(papBytes, originalTmbOffset, animationCount, tmbOffsetMod);
        tmbSections[0] = PatchTmb(tmbSections[0], eventIdentifier);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        writer.Write(papBytes, 0, PapInfoOffsetPosition);

        var newAnimationHeaderOffset = PapHeaderSize;
        var newHkxOffset = newAnimationHeaderOffset + animationHeaders.Sum(h => h.Length);
        var newTmbOffset = newHkxOffset + hkxData.Length;
        writer.Write(newAnimationHeaderOffset);
        writer.Write(newHkxOffset);
        writer.Write(newTmbOffset);

        foreach (var header in animationHeaders)
            writer.Write(header);

        writer.Write(hkxData);

        for (var i = 0; i < tmbSections.Count; i++)
        {
            writer.Write(tmbSections[i]);
            WritePadding(writer, Padding(output.Position, i, tmbSections.Count, tmbOffsetMod));
        }

        return output.ToArray();
    }

    private static List<byte[]> ReadAnimationHeaders(byte[] papBytes, int animationHeaderOffset, int animationCount)
    {
        var headers = new List<byte[]>(animationCount);
        for (var i = 0; i < animationCount; i++)
        {
            var sourceOffset = animationHeaderOffset + i * PapAnimationHeaderSize;
            if (sourceOffset < 0 || sourceOffset + PapAnimationHeaderSize > papBytes.Length)
                throw new InvalidDataException("PAP animation header is outside the file.");

            var header = new byte[PapAnimationHeaderSize];
            Buffer.BlockCopy(papBytes, sourceOffset, header, 0, PapAnimationHeaderSize);
            headers.Add(header);
        }

        return headers;
    }

    private static List<byte[]> ReadTmbSections(byte[] papBytes, int tmbOffset, int animationCount, int customOffset)
    {
        var sections = new List<byte[]>(animationCount);
        var position = tmbOffset;

        for (var i = 0; i < animationCount; i++)
        {
            if (position + 8 > papBytes.Length)
                throw new InvalidDataException("TMB section is outside the PAP file.");

            var size = ReadInt32(papBytes, position + 4);
            if (size <= 0 || position + size > papBytes.Length)
                throw new InvalidDataException("TMB section size is invalid.");

            var section = new byte[size];
            Buffer.BlockCopy(papBytes, position, section, 0, size);
            sections.Add(section);

            position += size;
            position += Padding(position, i, animationCount, customOffset);
        }

        return sections;
    }

    private static byte[] PatchTmb(byte[] tmbBytes, string eventIdentifier)
    {
        using var input = new MemoryStream(tmbBytes);
        using var reader = new BinaryReader(input);
        var tmb = new TmbFile(reader, null!, verify: false);
        try
        {
            var changed = false;
            var pathField = typeof(C009).GetField("Path", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var entry in tmb.AllEntries.OfType<C009>())
            {
                if (pathField?.GetValue(entry) is not TmbOffsetString pathObj)
                    continue;

                pathObj.Value = eventIdentifier;
                changed = true;
            }

            if (!changed)
                throw new InvalidDataException("No C009 animation timeline entries found in source PAP.");

            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            tmb.Write(writer);
            return output.ToArray();
        }
        finally
        {
            tmb.Dispose();
        }
    }

    private static short ReadAnimationCount(byte[] papBytes)
        => BitConverter.ToInt16(papBytes, 8);

    private static string ReadPapAnimationName(byte[] papBytes, int animationIndex)
    {
        if (!HasPapMagic(papBytes))
            return string.Empty;

        var animationHeaderOffset = ReadInt32(papBytes, PapInfoOffsetPosition);
        var nameOffset = animationHeaderOffset + animationIndex * PapAnimationHeaderSize;
        if (nameOffset < 0 || nameOffset + PapAnimationNameSize > papBytes.Length)
            return string.Empty;

        var length = 0;
        while (length < PapAnimationNameSize && papBytes[nameOffset + length] != 0)
            length++;

        return Encoding.UTF8.GetString(papBytes, nameOffset, length);
    }

    private static int ReadInt32(byte[] bytes, int offset)
        => BitConverter.ToInt32(bytes, offset);

    private static bool HasPapMagic(byte[] bytes)
        => bytes.Length >= 4 && ReadInt32(bytes, 0) == PapMagic;

    private static void ValidatePapMagic(byte[] bytes)
    {
        if (!HasPapMagic(bytes))
            throw new InvalidDataException("PAP magic is invalid.");
    }

    private static string NormalizeGamePath(string path)
        => path.Replace('\\', '/');

    private static void WritePaddedString(byte[] bytes, int offset, int length, string value)
    {
        var valueBytes = Encoding.UTF8.GetBytes(value);
        if (valueBytes.Length >= length)
            throw new InvalidDataException($"Animation name {value} is too long for a PAP animation header.");

        Array.Clear(bytes, offset, length);
        Buffer.BlockCopy(valueBytes, 0, bytes, offset, valueBytes.Length);
    }

    private static int Padding(long position, int itemIdx, int numItems, int customOffset)
    {
        if (numItems <= 1 || itemIdx >= numItems - 1)
            return 0;

        var remainder = (position - customOffset) % 4;
        return (int)(remainder == 0 ? 0 : 4 - remainder);
    }

    private static void WritePadding(BinaryWriter writer, int count)
    {
        for (var i = 0; i < count; i++)
            writer.Write((byte)0);
    }
}
