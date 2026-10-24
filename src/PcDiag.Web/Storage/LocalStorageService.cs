using PcDiag.Core.Configuration;
using PcDiag.Storage.Files;

namespace PcDiag.Web.Storage;

/// <summary>
/// Looks after PC Diagnostic Storage (SeaweedFS on this PC) while the Web host runs, when no storage host does (the
/// recorder's storage host normally holds it; this one takes over if that one goes): see <see cref="LocalObjectStoreHost"/>.
/// </summary>
internal sealed class LocalStorageService(FileStore store, DataPaths paths, ILogger<LocalObjectStoreHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var host = new LocalObjectStoreHost(store, paths.Root, logger);
        try
        {
            await host.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Web host is stopping (SeaweedFS with it, if it was looking after it).
        }
    }
}
