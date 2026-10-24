using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PcDiag.Storage.Sqlite.Migrations;

namespace PcDiag.Storage.Sqlite;

/// <summary>DI registration for the SQLite storage layer.</summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the connection factory, migrator and initializer. Requires <see cref="Core.Configuration.DataPaths"/>
    /// and <see cref="TimeProvider"/> to be registered.
    /// </summary>
    public static IServiceCollection AddPcDiagStorage(this IServiceCollection services)
    {
        services.TryAddSingleton<SqliteConnectionFactory>();
        services.TryAddSingleton<SchemaMigrator>();
        services.TryAddSingleton<DatabaseInitializer>();
        services.TryAddSingleton<Incidents.IncidentIndexReader>();
        services.TryAddSingleton<Reading.HistoryReader>();
        services.TryAddSingleton<Reading.SystemReader>();
        services.TryAddSingleton<Reading.IncidentReader>();
        services.TryAddSingleton<Reading.AnomalyReader>();
        services.TryAddSingleton<Reading.ExportReader>();
        services.TryAddSingleton<Reading.PeriodEvidenceReader>();
        services.TryAddSingleton<Core.Diagnostics.IIncidentEvidenceSource, Reading.IncidentEvidenceReader>();
        return services;
    }

    /// <summary>
    /// Registers what the recorder needs to persist telemetry: the batched telemetry writer (a sample-bus consumer),
    /// the recording-session and heartbeat stores, telemetry maintenance (rollups, retention, space reclaim), and what
    /// the storage manager needs (trimming and compacting telemetry, the list of incidents).
    /// Used by the Agent only; the Web host never writes.
    /// </summary>
    public static IServiceCollection AddTelemetryPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton<Telemetry.TelemetryStorageConsumer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Core.Abstractions.ISampleConsumer, Telemetry.TelemetryStorageConsumer>(
            sp => sp.GetRequiredService<Telemetry.TelemetryStorageConsumer>()));
        services.TryAddSingleton<Core.Abstractions.IRecorderSessionStore, Telemetry.RecorderSessionStore>();
        services.TryAddSingleton<Core.Abstractions.IAgentHeartbeatStore, Telemetry.AgentHeartbeatStore>();
        services.TryAddSingleton<Telemetry.TelemetryMaintenance>();
        services.TryAddSingleton<Core.Storage.ITelemetrySpace>(sp => sp.GetRequiredService<Telemetry.TelemetryMaintenance>());
        services.TryAddSingleton<Incidents.IncidentStore>();
        services.TryAddSingleton<Core.Incidents.IIncidentStore>(sp => sp.GetRequiredService<Incidents.IncidentStore>());
        services.TryAddSingleton<Core.Storage.IIncidentInventory>(sp => sp.GetRequiredService<Incidents.IncidentStore>());
        services.TryAddSingleton<Core.Anomalies.IAnomalyStore, Telemetry.AnomalyStore>();
        services.AddHostedService<Telemetry.TelemetryMaintenanceService>();
        return services;
    }
}
