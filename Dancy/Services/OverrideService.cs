using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Dancy.Domain;
using Dancy.Files;
using Dancy.Penumbra;
using Dancy.Persistence;
using Penumbra.Api.IpcSubscribers;

namespace Dancy.Services;

public sealed class PenumbraIpcModReloader : IPenumbraModReloader
{
    private readonly ReloadMod reloadMod = new(Plugin.PluginInterface);

    public PenumbraReloadResult Reload(string modDirectory, string modName)
        => PenumbraReloadCapture.ExecuteWithSingleRetry(() => reloadMod.Invoke(modDirectory, modName).ToString());
}

public sealed class OverrideOperationResult
{
    public OverrideExecutionResult Execution { get; init; } = new();
    public PenumbraWriteResult Write { get; init; } = new();
    public PenumbraReloadResult Reload { get; init; } = new();
}

/// <summary>
/// The one production override pipeline shared by the UI and Debug integration fixture.
/// </summary>
public sealed class OverrideService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> OperationLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly OverrideExecutionService executionService;
    private readonly IPenumbraModReloader reloader;

    public OverrideService()
        : this(new OverrideExecutionService(), new PenumbraIpcModReloader())
    {
    }

    public OverrideService(OverrideExecutionService executionService, IPenumbraModReloader reloader)
    {
        this.executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        this.reloader = reloader ?? throw new ArgumentNullException(nameof(reloader));
    }

    public OverrideOperationResult CreateOrUpdate(
        string modFolder,
        string modDirectory,
        string modName,
        OverridePlan plan,
        AtomicJsonWriteOptions? writeOptions = null)
    {
        var operationLock = OperationLocks.GetOrAdd(modFolder, _ => new SemaphoreSlim(1, 1));
        operationLock.Wait();
        try
        {
            var execution = executionService.CreatePapCopies(modFolder, plan);
            try
            {
                var write = PenumbraGroupWriter.CreateOrUpdateDancyGroup(modFolder, plan, execution.FinalMappings, writeOptions);
                execution.Commit();
                // Metadata is the ownership source of truth after a successful write.
                // A later best-effort orphan sweep must never roll back those committed mappings.
                try
                {
                    _ = DancyFileManager.CollectGeneratedFiles(modFolder);
                }
                catch
                {
                    // The next create or removal retries this narrow yucksdancy-only sweep.
                }
                var reload = reloader.Reload(modDirectory, modName);
                return new OverrideOperationResult
                {
                    Execution = execution,
                    Write = write,
                    Reload = reload,
                };
            }
            catch
            {
                execution.Rollback();
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

}
