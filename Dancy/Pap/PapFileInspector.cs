using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dancy.Pap;

/// <summary>
/// Performs the format checks Dancy needs before it reads or writes a PAP.
/// This deliberately does not interpret TMB commands; VFXEditor remains the
/// production parser for that part of the file.
/// </summary>
public static class PapFileInspector
{
    private const int PapMagic = 0x20706170;
    private const int AnimationCountOffset = 8;
    private const int AnimationHeaderOffsetPosition = 14;
    private const int HavokOffsetPosition = 18;
    private const int TimelineOffsetPosition = 22;
    private const int HeaderSize = 26;
    private const int AnimationHeaderSize = 40;
    private const int AnimationNameSize = 32;

    public sealed class PapFileInspection
    {
        public int AnimationCount { get; init; }
        public int AnimationHeaderOffset { get; init; }
        public int HavokOffset { get; init; }
        public int TimelineOffset { get; init; }
        public IReadOnlyList<string> AnimationNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<int> TimelineSectionSizes { get; init; } = Array.Empty<int>();
        public long Length { get; init; }
    }

    public static PapFileInspection InspectFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Inspect(File.ReadAllBytes(path));
    }

    public static PapFileInspection Inspect(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < HeaderSize)
            throw new InvalidDataException("PAP header is truncated.");
        if (ReadInt32(bytes, 0) != PapMagic)
            throw new InvalidDataException("PAP magic is invalid.");

        var animationCount = BitConverter.ToInt16(bytes, AnimationCountOffset);
        if (animationCount <= 0)
            throw new InvalidDataException("PAP contains no animations.");

        var animationHeaderOffset = ReadInt32(bytes, AnimationHeaderOffsetPosition);
        var havokOffset = ReadInt32(bytes, HavokOffsetPosition);
        var timelineOffset = ReadInt32(bytes, TimelineOffsetPosition);
        var headersEnd = (long)animationHeaderOffset + animationCount * AnimationHeaderSize;
        if (animationHeaderOffset < HeaderSize || headersEnd > bytes.Length || havokOffset < headersEnd || timelineOffset <= havokOffset || timelineOffset > bytes.Length)
            throw new InvalidDataException("PAP header offsets are invalid.");

        var names = new List<string>(animationCount);
        for (var index = 0; index < animationCount; index++)
        {
            var nameOffset = animationHeaderOffset + index * AnimationHeaderSize;
            names.Add(ReadNullTerminatedString(bytes, nameOffset, AnimationNameSize));
        }

        var sections = new List<int>(animationCount);
        var position = timelineOffset;
        var customOffset = timelineOffset % 4;
        for (var index = 0; index < animationCount; index++)
        {
            if (position < 0 || position + 8 > bytes.Length)
                throw new InvalidDataException("TMB section is outside the PAP file.");

            var size = ReadInt32(bytes, position + 4);
            if (size <= 0 || (long)position + size > bytes.Length)
                throw new InvalidDataException("TMB section size is invalid.");

            sections.Add(size);
            position += size;
            if (index < animationCount - 1)
            {
                var remainder = (position - customOffset) % 4;
                position += remainder == 0 ? 0 : 4 - remainder;
            }
        }

        return new PapFileInspection
        {
            AnimationCount = animationCount,
            AnimationHeaderOffset = animationHeaderOffset,
            HavokOffset = havokOffset,
            TimelineOffset = timelineOffset,
            AnimationNames = names,
            TimelineSectionSizes = sections,
            Length = bytes.Length,
        };
    }

    private static int ReadInt32(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + sizeof(int) > bytes.Length)
            throw new InvalidDataException("PAP header is truncated.");
        return BitConverter.ToInt32(bytes, offset);
    }

    private static string ReadNullTerminatedString(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || offset + length > bytes.Length)
            throw new InvalidDataException("PAP animation header is outside the file.");

        var actualLength = 0;
        while (actualLength < length && bytes[offset + actualLength] != 0)
            actualLength++;
        return Encoding.UTF8.GetString(bytes, offset, actualLength);
    }
}
