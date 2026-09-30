using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dancy.Domain;
using ECommons.DalamudServices;
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
    private const string VfxCapabilitiesEndpoint = "VFXEditor.PapAnimationRebuild.Capabilities";
    private const string VfxStandingIdleMotion0Endpoint = "VFXEditor.PapAnimationRebuild.StandingIdleMotion0";
    private const string VfxFingerprintEndpoint = "VFXEditor.PapAnimationRebuild.Fingerprint";
    private const string FingerprintScheme = "vfxeditor-pap-motion-sample-sha256-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public sealed class PapPatchResult
    {
        public string TargetGamePath { get; init; } = string.Empty;
        public string EventIdentifier { get; init; } = string.Empty;
        public PapOverrideWriteStrategy WriteStrategy { get; init; } = PapOverrideWriteStrategy.SingleSectionEventPatch;
        public int AnimationCount { get; init; }
        public int PatchedTimelineEntries { get; init; }
        public long OutputLength { get; init; }
        public PapFileInspector.PapFileInspection OutputInspection { get; init; } = new();
        public SourceAnimationSelection? SourceSelection { get; init; }
        public string SourceMotionFingerprint { get; init; } = string.Empty;
        public string PreservedTargetMotionFingerprint { get; init; } = string.Empty;
        public IReadOnlyList<string> PreservedTargetTimelineHashes { get; init; } = Array.Empty<string>();
    }

    public static PapPatchResult ApplyOverride(string defaultPath, string papPath, string newPap)
    {
        var sourceInspection = PapFileInspector.InspectFile(papPath);
        if (sourceInspection.AnimationCount != 1 || sourceInspection.TimelineSectionSizes.Count != 1)
            throw new InvalidDataException("A source-motion selection is required for a multi-motion PAP.");

        var selection = new SourceAnimationSelection(
            string.Empty,
            papPath,
            0,
            sourceInspection.AnimationNames[0],
            sourceInspection.HavokIndices[0],
            0,
            SourceAnimationSelectionMethod.SingleMotion,
            "The source PAP contains one animation header and one embedded timeline.");
        return ApplyOverride(defaultPath, papPath, selection, newPap);
    }

    public static PapPatchResult ApplyOverride(
        string defaultPath,
        string papPath,
        SourceAnimationSelection selection,
        string newPap)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var eventIdentifier = ReadTargetEventIdentifier(defaultPath);
        var sourceBytes = PapFileInspector.ReadFileWithRetry(papPath);
        var patched = PatchPap(sourceBytes, eventIdentifier, selection);
        var inspection = PapFileInspector.Inspect(patched.Bytes);

        var outputDirectory = Path.GetDirectoryName(newPap);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException("The generated PAP path has no parent directory.");

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(newPap, patched.Bytes);
        var outputLength = new FileInfo(newPap).Length;
        if (outputLength == 0)
            throw new InvalidDataException("The generated PAP file is empty.");

        var sourceFingerprint = string.Empty;
        if (selection.Method == SourceAnimationSelectionMethod.CompanionTimelineEvent)
            sourceFingerprint = VerifySelectedSourceMotionFingerprint(papPath, newPap, selection);

        return new PapPatchResult
        {
            TargetGamePath = defaultPath,
            EventIdentifier = eventIdentifier,
            WriteStrategy = selection.Method == SourceAnimationSelectionMethod.CompanionTimelineEvent
                ? PapOverrideWriteStrategy.SelectorBankEventPatch
                : PapOverrideWriteStrategy.SingleSectionEventPatch,
            AnimationCount = patched.AnimationCount,
            PatchedTimelineEntries = patched.PatchedTimelineEntries,
            OutputLength = outputLength,
            OutputInspection = inspection,
            SourceSelection = selection,
            SourceMotionFingerprint = sourceFingerprint,
        };
    }

    /// <summary>
    /// Creates Dancy's one narrowly supported multi-section output. The source
    /// loop's complete Havok motion 0 replaces Standing Idle's motion 0; both
    /// target-native timeline sections and target motion 1 are verified intact.
    /// </summary>
    public static PapPatchResult ApplyStandingIdleMotion0Override(string targetGamePath, string sourcePapPath, string outputPath)
    {
        var sourceInspection = PapFileInspector.InspectFile(sourcePapPath);
        var targetInspection = InspectTargetPap(targetGamePath);
        var compatibility = PapCompatibilityPreflight.Evaluate(sourceInspection, new PapTargetInspection(targetGamePath, targetInspection));
        if (compatibility.WriteStrategy != PapOverrideWriteStrategy.StandingIdleMotion0 || !compatibility.CanCreate)
            throw new InvalidOperationException($"Dancy cannot use the Standing Idle writer: {compatibility.Reason}");

        var character = GamePathIdentity.Parse(targetGamePath).Character;
        if (!character.IsKnown)
            throw new InvalidOperationException($"Dancy cannot determine a supported target skeleton from {targetGamePath}.");

        var workspace = Path.Combine(Path.GetTempPath(), "Dancy", "StandingIdleWriter", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workspace);
            var targetPath = Path.Combine(workspace, "target-idle.pap");
            var skeletonPath = Path.Combine(workspace, $"skl_{character.Code}b0001.sklb");
            CopyGameAsset(targetGamePath, targetPath);
            CopyGameAsset($"chara/human/{character.Code}/skeleton/base/b0001/skl_{character.Code}b0001.sklb", skeletonPath);

            var targetIntegrity = CaptureIntegrity(targetPath);
            var sourceMotion = Fingerprint(sourcePapPath, skeletonPath, 0);
            var targetMotion1 = Fingerprint(targetPath, skeletonPath, 1);
            EnsureStandingIdleCapability();

            var request = new VfxStandingIdleMotion0Request
            {
                SchemaVersion = 1,
                SourcePath = sourcePapPath,
                TargetPath = targetPath,
                OutputPath = outputPath,
                TargetSkeletonPath = skeletonPath,
            };
            var raw = Plugin.PluginInterface.GetIpcSubscriber<string, string>(VfxStandingIdleMotion0Endpoint)
                .InvokeFunc(JsonSerializer.Serialize(request, JsonOptions));
            var response = JsonSerializer.Deserialize<VfxStandingIdleMotion0Response>(raw, JsonOptions)
                ?? throw new InvalidOperationException("VFXEditor returned no Standing Idle writer response.");
            if (!response.Success)
                throw new InvalidOperationException($"VFXEditor Standing Idle writer failed [{response.ErrorCode}]: {response.Error}");
            if (!File.Exists(outputPath))
                throw new FileNotFoundException("VFXEditor reported Standing Idle success but did not create Dancy's output PAP.", outputPath);
            if (!string.Equals(response.SourceSha256, HashFile(sourcePapPath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(response.TargetSha256, HashFile(targetPath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(response.OutputSha256, HashFile(outputPath), StringComparison.OrdinalIgnoreCase)
                || response.OutputAnimationCount != 2
                || response.TargetHavokBindingCount != response.OutputHavokBindingCount
                || !response.OutputPapParsed
                || !response.OutputHavokParsed
                || !response.Motion0BindingValid
                || !response.Motion1BindingValid
                || !response.TargetTimelinesPreserved)
            {
                throw new InvalidDataException("VFXEditor did not attest to the required fixed Standing Idle output structure.");
            }

            var outputIntegrity = CaptureIntegrity(outputPath);
            RequireSameHeaders(targetIntegrity, outputIntegrity);
            RequireSameTimelines(targetIntegrity, outputIntegrity);
            var outputInspection = PapFileInspector.InspectFile(outputPath);
            var outputMotion0 = Fingerprint(outputPath, skeletonPath, 0);
            var outputMotion1 = Fingerprint(outputPath, skeletonPath, 1);
            RequireFingerprint(outputMotion0, sourceMotion, "Standing Idle motion 0");
            RequireFingerprint(outputMotion1, targetMotion1, "Standing Idle motion 1");

            return new PapPatchResult
            {
                TargetGamePath = targetGamePath,
                EventIdentifier = outputInspection.AnimationNames[0],
                WriteStrategy = PapOverrideWriteStrategy.StandingIdleMotion0,
                AnimationCount = outputInspection.AnimationCount,
                PatchedTimelineEntries = 0,
                OutputLength = new FileInfo(outputPath).Length,
                OutputInspection = outputInspection,
                SourceMotionFingerprint = sourceMotion.FingerprintSha256,
                PreservedTargetMotionFingerprint = targetMotion1.FingerprintSha256,
                PreservedTargetTimelineHashes = targetIntegrity.TimelineHashes,
            };
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception exception)
                {
                    // The committed PAP lives in the mod directory, never in this input-only workspace.
                    Svc.Log.Warning(exception, "[Dancy] Could not remove the temporary Standing Idle writer workspace.");
                }
            }
        }
    }

    public static string ReadTargetEventIdentifier(string defaultPath)
    {
        var inspection = InspectTargetPap(defaultPath);
        var eventIdentifier = inspection.AnimationNames.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(eventIdentifier))
            throw new InvalidDataException($"Could not read an animation name from {defaultPath}.");
        return eventIdentifier;
    }

    public static PapFileInspector.PapFileInspection InspectTargetPap(string defaultPath)
    {
        var defaultFile = Plugin.DataManager.GetFile(defaultPath);
        if (defaultFile == null)
            throw new FileNotFoundException($"File {defaultPath} was not found in game data.");

        return PapFileInspector.Inspect(ReadAllBytes(defaultFile.Reader.BaseStream));
    }

    private static void EnsureStandingIdleCapability()
    {
        var raw = Plugin.PluginInterface.GetIpcSubscriber<string>(VfxCapabilitiesEndpoint).InvokeFunc();
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("supportsStandingIdleMotion0Replacement", out var supported) || !supported.GetBoolean())
        {
            throw new InvalidOperationException(
                "Standing Idle requires a VFXEditor build that supports Dancy's fixed motion-0 replacement endpoint.");
        }
    }

    private static void CopyGameAsset(string gamePath, string outputPath)
    {
        var gameFile = Plugin.DataManager.GetFile(gamePath)
            ?? throw new FileNotFoundException($"Game asset {gamePath} was not found.");
        if (gameFile.Reader.BaseStream.CanSeek)
            gameFile.Reader.BaseStream.Position = 0;
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        gameFile.Reader.BaseStream.CopyTo(output);
    }

    private static VfxMotionFingerprintResponse Fingerprint(string papPath, string skeletonPath, int motionIndex)
    {
        var raw = Plugin.PluginInterface.GetIpcSubscriber<string, string>(VfxFingerprintEndpoint).InvokeFunc(
            JsonSerializer.Serialize(new VfxMotionFingerprintRequest
            {
                SchemaVersion = 1,
                SourcePath = papPath,
                TargetSkeletonPath = skeletonPath,
                MotionIndex = motionIndex,
            }, JsonOptions));
        var response = JsonSerializer.Deserialize<VfxMotionFingerprintResponse>(raw, JsonOptions)
            ?? throw new InvalidOperationException("VFXEditor returned no motion fingerprint response.");
        if (!response.Success || !string.Equals(response.FingerprintScheme, FingerprintScheme, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(response.FingerprintSha256))
        {
            throw new InvalidOperationException($"VFXEditor motion fingerprint failed [{response.ErrorCode}]: {response.Error}");
        }
        return response;
    }

    private static string VerifySelectedSourceMotionFingerprint(
        string sourcePapPath,
        string outputPapPath,
        SourceAnimationSelection selection)
    {
        var character = GamePathIdentity.Parse(selection.LogicalGamePath).Character;
        if (!character.IsKnown)
            throw new InvalidDataException("Dancy could not determine the selected source animation's skeleton for fingerprint validation.");

        var workspace = Path.Combine(Path.GetTempPath(), "Dancy", "SourceMotionFingerprint", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workspace);
            var skeletonPath = Path.Combine(workspace, $"skl_{character.Code}b0001.sklb");
            CopyGameAsset($"chara/human/{character.Code}/skeleton/base/b0001/skl_{character.Code}b0001.sklb", skeletonPath);

            var source = Fingerprint(sourcePapPath, skeletonPath, selection.HavokMotionIndex);
            var output = Fingerprint(outputPapPath, skeletonPath, selection.HavokMotionIndex);
            RequireFingerprint(output, source, "Selected source motion");
            return source.FingerprintSha256;
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception exception)
                {
                    Svc.Log.Warning(exception, "[Dancy] Could not remove the temporary source-motion fingerprint workspace.");
                }
            }
        }
    }

    private static PapIntegrity CaptureIntegrity(string path)
    {
        var bytes = PapFileInspector.ReadFileWithRetry(path);
        var inspection = PapFileInspector.Inspect(bytes);
        var headers = inspection.AnimationHeaders
            .Select(header => Hash(bytes, header.Offset, header.Size))
            .ToArray();
        var timelines = inspection.TimelineSections
            .Select(section => Hash(bytes, section.Offset, section.Size))
            .ToArray();
        return new PapIntegrity(headers, timelines);
    }

    private static string Hash(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
            throw new InvalidDataException("PAP integrity range is invalid.");
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, length)));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void RequireSameHeaders(PapIntegrity target, PapIntegrity output)
    {
        if (!target.HeaderHashes.SequenceEqual(output.HeaderHashes, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The generated Standing Idle PAP changed a target animation header.");
    }

    private static void RequireSameTimelines(PapIntegrity target, PapIntegrity output)
    {
        if (!target.TimelineHashes.SequenceEqual(output.TimelineHashes, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The generated Standing Idle PAP changed a target-native TMB section.");
    }

    private static void RequireFingerprint(VfxMotionFingerprintResponse actual, VfxMotionFingerprintResponse expected, string label)
    {
        if (!string.Equals(actual.FingerprintSha256, expected.FingerprintSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} did not match its required source or preserved target fingerprint.");
    }

    /// <summary>
    /// Reads the animation event references embedded in a local PAP's timeline.
    /// This is diagnostic-only and does not mutate the file.
    /// </summary>
    public static IReadOnlyList<string> ReadTimelineEventIdentifiers(string papPath)
    {
        var bytes = PapFileInspector.ReadFileWithRetry(papPath);
        var inspection = PapFileInspector.Inspect(bytes);
        var sections = ReadTmbSections(bytes, inspection.TimelineOffset, inspection.AnimationCount, inspection.TimelineOffset % 4);
        return sections.SelectMany(ReadPapAnimationEventIdentifiers).ToList();
    }

    public static IReadOnlyList<IReadOnlyList<string>> ReadEmbeddedTimelineEventIdentifiers(string papPath)
    {
        var bytes = PapFileInspector.ReadFileWithRetry(papPath);
        var inspection = PapFileInspector.Inspect(bytes);
        return ReadTmbSections(bytes, inspection.TimelineOffset, inspection.AnimationCount, inspection.TimelineOffset % 4)
            .Select(section => (IReadOnlyList<string>)ReadPapAnimationEventIdentifiers(section))
            .ToList();
    }

    public static IReadOnlyList<string> ReadCompanionTimelineEventIdentifiers(string timelinePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timelinePath);
        return ReadCompanionTimelineEventIdentifiers(PapFileInspector.ReadFileWithRetry(timelinePath));
    }

    private static List<string> ReadPapAnimationEventIdentifiers(byte[] tmbBytes)
        => ReadTmbEventIdentifiers(tmbBytes, entry => entry is C009);

    private static List<string> ReadCompanionTimelineEventIdentifiers(byte[] tmbBytes)
        => ReadTmbEventIdentifiers(tmbBytes, entry => entry is C009 or C010);

    private static List<string> ReadTmbEventIdentifiers(byte[] tmbBytes, Func<TmbEntry, bool> isAnimationEntry)
    {
        using var stream = new MemoryStream(tmbBytes);
        using var reader = new BinaryReader(stream);
        var tmb = new TmbFile(reader, null!, verify: false);
        try
        {
            return tmb.AllEntries
                .Where(isAnimationEntry)
                .Select(entry => entry.GetType().GetField("Path", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(entry) as TmbOffsetString)
                .Where(path => !string.IsNullOrWhiteSpace(path?.Value))
                .Select(path => path!.Value)
                .ToList();
        }
        finally { tmb.Dispose(); }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.CanSeek)
            stream.Position = 0;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static (byte[] Bytes, int AnimationCount, int PatchedTimelineEntries) PatchPap(
        byte[] papBytes,
        string eventIdentifier,
        SourceAnimationSelection selection)
    {
        var sourceInspection = PapFileInspector.Inspect(papBytes);
        ValidateSelection(sourceInspection, selection);
        var animationCount = sourceInspection.AnimationCount;
        var animationHeaderOffset = sourceInspection.AnimationHeaderOffset;
        var originalHkxOffset = sourceInspection.HavokOffset;
        var originalTmbOffset = sourceInspection.TimelineOffset;

        var animationHeaders = ReadAnimationHeaders(papBytes, animationHeaderOffset, animationCount);
        var preserveSourceBank = selection.Method == SourceAnimationSelectionMethod.CompanionTimelineEvent;
        if (preserveSourceBank
            && animationHeaders.Where((_, index) => index != selection.AnimationHeaderIndex)
                .Select(header => ReadPaddedString(header, 0, PapAnimationNameSize))
                .Any(name => string.Equals(name, eventIdentifier, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The target animation event already belongs to an untouched source-bank section.");
        }

        var selectedHeader = animationHeaders[selection.AnimationHeaderIndex];
        WritePaddedString(selectedHeader, 0, PapAnimationNameSize, eventIdentifier);

        var hkxData = new byte[originalTmbOffset - originalHkxOffset];
        Buffer.BlockCopy(papBytes, originalHkxOffset, hkxData, 0, hkxData.Length);

        var tmbOffsetMod = originalTmbOffset % 4;
        var tmbSections = ReadTmbSections(papBytes, originalTmbOffset, animationCount, tmbOffsetMod);
        var patchedTmb = PatchTmb(tmbSections[selection.EmbeddedTmbIndex], eventIdentifier);
        tmbSections[selection.EmbeddedTmbIndex] = patchedTmb.Bytes;
        var outputHeaders = preserveSourceBank
            ? animationHeaders
            : new List<byte[]> { selectedHeader };
        var outputTmbSections = preserveSourceBank
            ? tmbSections
            : new List<byte[]> { patchedTmb.Bytes };
        var outputAnimationCount = outputHeaders.Count;

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        var preamble = papBytes[..PapInfoOffsetPosition];
        BitConverter.GetBytes((short)outputAnimationCount).CopyTo(preamble, 8);
        writer.Write(preamble);

        var newAnimationHeaderOffset = PapHeaderSize;
        var newHkxOffset = newAnimationHeaderOffset + outputHeaders.Sum(header => header.Length);
        var newTmbOffset = newHkxOffset + hkxData.Length;
        writer.Write(newAnimationHeaderOffset);
        writer.Write(newHkxOffset);
        writer.Write(newTmbOffset);

        foreach (var header in outputHeaders)
            writer.Write(header);

        writer.Write(hkxData);

        for (var index = 0; index < outputTmbSections.Count; index++)
        {
            writer.Write(outputTmbSections[index]);
            WritePadding(writer, Padding(writer.BaseStream.Position, index, outputAnimationCount, newTmbOffset % 4));
        }

        var result = output.ToArray();
        if (preserveSourceBank)
            ValidatePreservedSelectorBank(sourceInspection, papBytes, result, selection, eventIdentifier);
        return (result, outputAnimationCount, patchedTmb.PatchedEntries);
    }

    private static void ValidateSelection(PapFileInspector.PapFileInspection inspection, SourceAnimationSelection selection)
    {
        if (selection.AnimationHeaderIndex < 0 || selection.AnimationHeaderIndex >= inspection.AnimationCount
            || selection.EmbeddedTmbIndex < 0 || selection.EmbeddedTmbIndex >= inspection.TimelineSectionSizes.Count
            || selection.EmbeddedTmbIndex != selection.AnimationHeaderIndex)
        {
            throw new InvalidDataException("The selected source animation header and embedded timeline are not an unambiguous matching pair.");
        }

        if (!string.Equals(inspection.AnimationNames[selection.AnimationHeaderIndex], selection.AnimationEvent, StringComparison.OrdinalIgnoreCase)
            || inspection.HavokIndices[selection.AnimationHeaderIndex] != selection.HavokMotionIndex
            || selection.HavokMotionIndex < 0)
        {
            throw new InvalidDataException("The selected source animation no longer matches its verified header or Havok motion binding.");
        }
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

    private static void ValidatePreservedSelectorBank(
        PapFileInspector.PapFileInspection source,
        byte[] sourceBytes,
        byte[] outputBytes,
        SourceAnimationSelection selection,
        string targetEvent)
    {
        var output = PapFileInspector.Inspect(outputBytes);
        if (output.AnimationCount != source.AnimationCount
            || output.TimelineSectionSizes.Count != source.TimelineSectionSizes.Count
            || output.HavokIndices.Count != source.HavokIndices.Count
            || output.HavokIndices[selection.AnimationHeaderIndex] != selection.HavokMotionIndex)
        {
            throw new InvalidDataException("The selector-backed output did not preserve the source animation-bank topology.");
        }

        if (output.AnimationNames.Count(name => string.Equals(name, targetEvent, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("The selector-backed output does not contain exactly one target animation event.");

        var sourceHeaders = ReadAnimationHeaders(sourceBytes, source.AnimationHeaderOffset, source.AnimationCount);
        var outputHeaders = ReadAnimationHeaders(outputBytes, output.AnimationHeaderOffset, output.AnimationCount);
        var sourceTimelines = ReadTmbSections(sourceBytes, source.TimelineOffset, source.AnimationCount, source.TimelineOffset % 4);
        var outputTimelines = ReadTmbSections(outputBytes, output.TimelineOffset, output.AnimationCount, output.TimelineOffset % 4);
        for (var index = 0; index < source.AnimationCount; index++)
        {
            if (index == selection.AnimationHeaderIndex)
                continue;
            if (!sourceHeaders[index].SequenceEqual(outputHeaders[index])
                || !sourceTimelines[index].SequenceEqual(outputTimelines[index]))
            {
                throw new InvalidDataException("The selector-backed output changed an unselected source-bank section.");
            }
        }

        var sourceHkx = sourceBytes.AsSpan(source.HavokOffset, source.HavokSize);
        var outputHkx = outputBytes.AsSpan(output.HavokOffset, output.HavokSize);
        if (!sourceHkx.SequenceEqual(outputHkx))
            throw new InvalidDataException("The selector-backed output changed the source Havok payload.");
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

    private static (byte[] Bytes, int PatchedEntries) PatchTmb(byte[] tmbBytes, string eventIdentifier)
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
            return (output.ToArray(), tmb.AllEntries.OfType<C009>().Count());
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

    private static void ValidatePatchedPap(byte[] bytes)
    {
        _ = PapFileInspector.Inspect(bytes);
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

    private static string ReadPaddedString(byte[] bytes, int offset, int length)
    {
        var actualLength = 0;
        while (actualLength < length && bytes[offset + actualLength] != 0)
            actualLength++;
        return Encoding.UTF8.GetString(bytes, offset, actualLength);
    }

    private static int Padding(long position, int itemIdx, int numItems, int customOffset)
    {
        if (numItems <= 1 || itemIdx >= numItems - 1)
            return 0;

        var remainder = (position - customOffset) % 4;
        return (int)(remainder == 0 ? 0 : 4 - remainder);
    }

    private sealed record PapIntegrity(IReadOnlyList<string> HeaderHashes, IReadOnlyList<string> TimelineHashes);

    private sealed class VfxStandingIdleMotion0Request
    {
        public int SchemaVersion { get; init; }
        public string SourcePath { get; init; } = string.Empty;
        public string TargetPath { get; init; } = string.Empty;
        public string OutputPath { get; init; } = string.Empty;
        public string TargetSkeletonPath { get; init; } = string.Empty;
    }

    private sealed class VfxStandingIdleMotion0Response
    {
        public bool Success { get; init; }
        public string SourceSha256 { get; init; } = string.Empty;
        public string TargetSha256 { get; init; } = string.Empty;
        public string OutputSha256 { get; init; } = string.Empty;
        public int? OutputAnimationCount { get; init; }
        public int? TargetHavokBindingCount { get; init; }
        public int? OutputHavokBindingCount { get; init; }
        public bool OutputPapParsed { get; init; }
        public bool OutputHavokParsed { get; init; }
        public bool Motion0BindingValid { get; init; }
        public bool Motion1BindingValid { get; init; }
        public bool TargetTimelinesPreserved { get; init; }
        public string? ErrorCode { get; init; }
        public string? Error { get; init; }
    }

    private sealed class VfxMotionFingerprintRequest
    {
        public int SchemaVersion { get; init; }
        public string SourcePath { get; init; } = string.Empty;
        public string TargetSkeletonPath { get; init; } = string.Empty;
        public int MotionIndex { get; init; }
    }

    private sealed class VfxMotionFingerprintResponse
    {
        public bool Success { get; init; }
        public string FingerprintScheme { get; init; } = string.Empty;
        public string FingerprintSha256 { get; init; } = string.Empty;
        public string? ErrorCode { get; init; }
        public string? Error { get; init; }
    }

    private static void WritePadding(BinaryWriter writer, int count)
    {
        for (var i = 0; i < count; i++)
            writer.Write((byte)0);
    }
}
