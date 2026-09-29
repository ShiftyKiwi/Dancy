using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dancy.Domain;
using Dancy.Pap;
using Dancy.Persistence;
using ECommons.DalamudServices;

namespace Dancy.Services;

public sealed class OverrideExecutionResult
{
    public IReadOnlyDictionary<string, string> FinalMappings { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<PapEditor.PapPatchResult> PapResults { get; init; } = Array.Empty<PapEditor.PapPatchResult>();
    public IReadOnlyList<string> GeneratedFiles { get; init; } = Array.Empty<string>();
    public PapCompatibilityResult Compatibility { get; init; } = new(PapCompatibilityStatus.Unknown, "Compatibility was not inspected.");
    internal IReadOnlyList<GeneratedPapTransaction> Transactions { get; init; } = Array.Empty<GeneratedPapTransaction>();

    internal void Commit()
    {
        foreach (var transaction in Transactions)
        {
            if (!string.IsNullOrEmpty(transaction.BackupPath) && File.Exists(transaction.BackupPath))
                File.Delete(transaction.BackupPath);
        }
    }

    internal void Rollback()
    {
        foreach (var transaction in Transactions.Reverse())
        {
            try
            {
                if (transaction.WasExisting && !string.IsNullOrEmpty(transaction.BackupPath) && File.Exists(transaction.BackupPath))
                    File.Move(transaction.BackupPath, transaction.OutputPath, overwrite: true);
                else if (File.Exists(transaction.OutputPath))
                    File.Delete(transaction.OutputPath);
            }
            finally
            {
                if (!string.IsNullOrEmpty(transaction.BackupPath) && File.Exists(transaction.BackupPath))
                    File.Delete(transaction.BackupPath);
            }
        }
    }
}

internal sealed class GeneratedPapTransaction
{
    public string OutputPath { get; init; } = string.Empty;
    public string? BackupPath { get; init; }
    public bool WasExisting { get; init; }
}

public sealed class OverrideExecutionService
{
    public OverrideExecutionResult CreatePapCopies(string modFolder, OverridePlan plan)
    {
        if (!plan.IsValid)
            throw new InvalidOperationException("Dancy cannot execute an invalid override plan.");

        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var papResults = new List<PapEditor.PapPatchResult>();
        var generatedFiles = new List<string>();
        var transactions = new List<GeneratedPapTransaction>();
        var compatibilityResults = new List<PapCompatibilityResult>();

        try
        {
            foreach (var copy in plan.PapCopies)
            {
                if (!PathSafety.TryResolveInsideRoot(modFolder, copy.SourcePapPath, out var sourcePapPath))
                    throw new InvalidOperationException($"Dancy refused an unsafe source PAP path: {copy.SourcePapPath}");
                if (!File.Exists(sourcePapPath))
                    throw new FileNotFoundException("The selected source PAP does not exist.", sourcePapPath);

                var sourceInspection = PapFileInspector.InspectFile(sourcePapPath);
                var selectionResult = SourceAnimationSelectionResolver.Resolve(modFolder, copy);
                if (!selectionResult.IsSuccess || selectionResult.Selection is null)
                    throw new InvalidOperationException(selectionResult.Error ?? "Dancy could not select one source animation from the PAP.");
                var sourceSelection = selectionResult.Selection;
                var targetInspections = OnFrameworkThread(() => copy.TargetGamePaths
                    .Select(path => new PapTargetInspection(path, PapEditor.InspectTargetPap(path)))
                    .ToList());
                var targetStrategies = targetInspections
                    .Select(target => (Target: target, Compatibility: PapCompatibilityPreflight.Evaluate(sourceInspection, sourceSelection, target)))
                    .ToList();
                compatibilityResults.AddRange(targetStrategies.Select(pair => pair.Compatibility));
                var unsupported = targetStrategies.FirstOrDefault(pair => !pair.Compatibility.CanCreate);
                if (unsupported.Target is not null)
                {
                    throw new InvalidOperationException(
                        $"Dancy cannot safely create {unsupported.Target.GamePath}: {unsupported.Compatibility.Reason}");
                }

                foreach (var standingIdle in targetStrategies.Where(pair => pair.Compatibility.WriteStrategy == PapOverrideWriteStrategy.StandingIdleMotion0))
                {
                    var outputRelativePath = BuildOutputPathForTarget(copy.OutputRelativePath, standingIdle.Target.GamePath);
                    if (!PathSafety.TryResolveInsideRoot(modFolder, outputRelativePath, out var outputPath))
                        throw new InvalidOperationException($"Dancy refused an unsafe generated PAP path: {outputRelativePath}");

                    var temporaryOutput = CreateTemporaryPapPath(outputPath);
                    var transaction = CreateTransaction(outputPath);
                    transactions.Add(transaction);
                    try
                    {
                        var patchResult = OnFrameworkThread(() => PapEditor.ApplyStandingIdleMotion0Override(
                            standingIdle.Target.GamePath,
                            sourcePapPath,
                            temporaryOutput));
                        File.Move(temporaryOutput, outputPath, overwrite: true);
                        papResults.Add(patchResult);
                        generatedFiles.Add(outputRelativePath);
                    }
                    finally
                    {
                        if (File.Exists(temporaryOutput))
                            File.Delete(temporaryOutput);
                    }

                    mappings[standingIdle.Target.GamePath] = outputRelativePath;
                }

                var targetEvents = OnFrameworkThread(() => targetStrategies
                    .Where(pair => pair.Compatibility.WriteStrategy == PapOverrideWriteStrategy.SingleSectionEventPatch)
                    .Select(pair => (GamePath: pair.Target.GamePath, EventIdentifier: PapEditor.ReadTargetEventIdentifier(pair.Target.GamePath)))
                    .ToList());
                var eventGroups = targetEvents
                    .GroupBy(pair => pair.EventIdentifier, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var eventGroup in eventGroups)
                {
                    var outputRelativePath = BuildOutputPath(copy.OutputRelativePath, eventGroup.Key, eventGroups.Count);
                    if (!PathSafety.TryResolveInsideRoot(modFolder, outputRelativePath, out var outputPath))
                        throw new InvalidOperationException($"Dancy refused an unsafe generated PAP path: {outputRelativePath}");

                    // VFXEditor validates the file extension before it fingerprints the selected source motion.
                    // Keep this transaction-local artifact visibly a PAP until it is atomically moved into place.
                    var temporaryOutput = CreateTemporaryPapPath(outputPath);
                    var transaction = CreateTransaction(outputPath);
                    transactions.Add(transaction);
                    try
                    {
                        var patchResult = OnFrameworkThread(() => PapEditor.ApplyOverride(
                            eventGroup.First().GamePath,
                            sourcePapPath,
                            sourceSelection,
                            temporaryOutput));
                        File.Move(temporaryOutput, outputPath, overwrite: true);
                        papResults.Add(patchResult);
                        generatedFiles.Add(outputRelativePath);
                    }
                    finally
                    {
                        if (File.Exists(temporaryOutput))
                            File.Delete(temporaryOutput);
                    }

                    foreach (var target in eventGroup)
                        mappings[target.GamePath] = outputRelativePath;
                }
            }

            if (mappings.Count == 0)
                throw new InvalidOperationException("Dancy did not produce any target PAP mappings.");

            return new OverrideExecutionResult
            {
                FinalMappings = mappings,
                PapResults = papResults,
                GeneratedFiles = generatedFiles,
                Compatibility = PapCompatibilityPreflight.Combine(compatibilityResults),
                Transactions = transactions,
            };
        }
        catch
        {
            new OverrideExecutionResult { Transactions = transactions }.Rollback();
            throw;
        }
    }

    private static string BuildOutputPath(string baseRelativePath, string eventIdentifier, int eventGroupCount)
    {
        if (eventGroupCount == 1)
            return baseRelativePath;

        var extension = Path.GetExtension(baseRelativePath);
        var withoutExtension = baseRelativePath[..^extension.Length];
        return $"{withoutExtension}-{ShortHash(eventIdentifier)}{extension}";
    }

    private static string BuildOutputPathForTarget(string baseRelativePath, string targetGamePath)
    {
        var extension = Path.GetExtension(baseRelativePath);
        var withoutExtension = baseRelativePath[..^extension.Length];
        return $"{withoutExtension}-standing-idle-{ShortHash(targetGamePath)}{extension}";
    }

    private static string CreateTemporaryPapPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Dancy could not determine the generated PAP directory.");
        var extension = Path.GetExtension(outputPath);
        var name = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(directory, $"{name}.{Guid.NewGuid():N}.tmp{extension}");
    }

    private static T OnFrameworkThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Svc.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task.GetAwaiter().GetResult();
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static GeneratedPapTransaction CreateTransaction(string outputPath)
    {
        var wasExisting = File.Exists(outputPath);
        var backupPath = wasExisting ? outputPath + $".{Guid.NewGuid():N}.dancy-backup" : null;
        if (backupPath != null)
            File.Copy(outputPath, backupPath, overwrite: false);

        return new GeneratedPapTransaction
        {
            OutputPath = outputPath,
            BackupPath = backupPath,
            WasExisting = wasExisting,
        };
    }
}
