using Microsoft.Extensions.Hosting;
using PcDiag.Storage.Sqlite;

namespace PcDiag.Hosting;

/// <summary>Creates or migrates the databases during host startup, before other hosted services run.</summary>
internal sealed class DatabaseInitializationService(DatabaseInitializer initializer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => initializer.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
