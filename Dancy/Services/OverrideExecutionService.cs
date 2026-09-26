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
                var targetInspections = OnFrameworkThread(() => copy.TargetGamePaths
                    .Select(PapEditor.InspectTargetPap)
                    .ToList());
                var compatibility = PapCompatibilityPreflight.Evaluate(sourceInspection, targetInspections);
                compatibilityResults.Add(compatibility);
                if (!compatibility.CanCreate)
                    throw new InvalidOperationException($"Dancy cannot safely create this override: {compatibility.Reason}");

                var targetEvents = OnFrameworkThread(() => copy.TargetGamePaths
                    .Select(path => (GamePath: path, EventIdentifier: PapEditor.ReadTargetEventIdentifier(path)))
                    .ToList());
                var eventGroups = targetEvents
                    .GroupBy(pair => pair.EventIdentifier, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var eventGroup in eventGroups)
                {
                    var outputRelativePath = BuildOutputPath(copy.OutputRelativePath, eventGroup.Key, eventGroups.Count);
                    if (!PathSafety.TryResolveInsideRoot(modFolder, outputRelativePath, out var outputPath))
                        throw new InvalidOperationException($"Dancy refused an unsafe generated PAP path: {outputRelativePath}");

                    var temporaryOutput = outputPath + $".{Guid.NewGuid():N}.tmp";
                    var transaction = CreateTransaction(outputPath);
                    transactions.Add(transaction);
                    try
                    {
                        var patchResult = OnFrameworkThread(() => PapEditor.ApplyOverride(
                            eventGroup.First().GamePath,
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
