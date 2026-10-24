using System.Reflection;

namespace PcDiag.Storage.Sqlite.Migrations;

/// <summary>The kinds of database the application manages.</summary>
public enum DatabaseKind
{
    /// <summary>telemetry.db: the rolling store.</summary>
    Telemetry,

    /// <summary>incidents.db: the incident index.</summary>
    IncidentIndex,

    /// <summary>incidents/&lt;key&gt;/incident.db: one incident's evidence (telemetry schema plus timeline and findings).</summary>
    Incident,
}

/// <summary>One schema version, made of one or more embedded SQL scripts applied in order.</summary>
internal sealed record Migration(int Version, string Description, IReadOnlyList<string> ScriptNames);

/// <summary>Lists the migrations for each <see cref="DatabaseKind"/> and loads their embedded SQL.</summary>
internal static class MigrationCatalog
{
    private static readonly Assembly ResourceAssembly = typeof(MigrationCatalog).Assembly;

    /// <summary>Ordered migrations for a database kind. Versions start at 1 and are contiguous.</summary>
    public static IReadOnlyList<Migration> For(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Telemetry =>
        [
            new Migration(1, "Initial telemetry schema", ["Telemetry.0001_initial.sql"]),
            new Migration(2, "GPU power percent and total VRAM", ["Telemetry.0002_gpu_power_and_vram_total.sql"]),
            new Migration(3, "Volume capacity and free space", ["Telemetry.0003_volumes.sql"]),
            new Migration(4, "Windows event details", ["Telemetry.0004_windows_event_details.sql"]),
            new Migration(5, "Display event target keys", ["Telemetry.0005_display_events.sql"]),
            new Migration(6, "Heartbeat recording flag and retention indexes", ["Telemetry.0006_recorder_health.sql"]),
            new Migration(7, "Anomaly device", ["Telemetry.0007_anomaly_device.sql"]),
            new Migration(8, "True per-process disk and network I/O", ["Telemetry.0008_process_true_io.sql"]),
            new Migration(9, "Custom recording selection", ["Telemetry.0009_recording_selection.sql"]),
            new Migration(10, "Service event process", ["Telemetry.0010_service_event_pid.sql"]),
            new Migration(11, "Device register", ["Telemetry.0011_io_devices.sql"]),
        ],
        DatabaseKind.IncidentIndex =>
        [
            new Migration(1, "Initial incident index schema", ["IncidentIndex.0001_initial.sql"]),
            new Migration(2, "Boot recoveries", ["IncidentIndex.0002_boot_recoveries.sql"]),
            new Migration(3, "Boot recoveries processed flag", ["IncidentIndex.0003_boot_recovery_processed.sql"]),
            new Migration(4, "Analyzer version", ["IncidentIndex.0004_analysis_version.sql"]),
        ],
        DatabaseKind.Incident =>
        [
            new Migration(1, "Telemetry schema plus incident timeline and findings", ["Telemetry.0001_initial.sql", "Incident.0001_incident.sql"]),
            new Migration(2, "GPU power percent and total VRAM", ["Telemetry.0002_gpu_power_and_vram_total.sql"]),
            new Migration(3, "Volume capacity and free space", ["Telemetry.0003_volumes.sql"]),
            new Migration(4, "Windows event details", ["Telemetry.0004_windows_event_details.sql"]),
            new Migration(5, "Display event target keys", ["Telemetry.0005_display_events.sql"]),
            new Migration(6, "Heartbeat recording flag and retention indexes", ["Telemetry.0006_recorder_health.sql"]),
            new Migration(7, "Anomaly device", ["Telemetry.0007_anomaly_device.sql"]),
            new Migration(8, "True per-process disk and network I/O", ["Telemetry.0008_process_true_io.sql"]),
            new Migration(9, "Custom recording selection", ["Telemetry.0009_recording_selection.sql"]),
            new Migration(10, "Service event process", ["Telemetry.0010_service_event_pid.sql"]),
            new Migration(11, "Device register", ["Telemetry.0011_io_devices.sql"]),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown database kind."),
    };

    /// <summary>The latest schema version for a database kind.</summary>
    public static int LatestVersion(DatabaseKind kind) => For(kind)[^1].Version;

    /// <summary>Loads an embedded script by its name relative to the Migrations folder (e.g. "Telemetry.0001_initial.sql").</summary>
    /// <exception cref="InvalidOperationException">The script is not embedded in the assembly.</exception>
    public static string LoadScript(string scriptName)
    {
        var suffix = ".Migrations." + scriptName;
        var resourceName = ResourceAssembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Migration script '{scriptName}' is not embedded in {ResourceAssembly.GetName().Name}.");

        using var stream = ResourceAssembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
