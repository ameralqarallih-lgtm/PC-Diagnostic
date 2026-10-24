using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Migrations;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Tests;

public sealed class EventStorageTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.Zero));
    private readonly SqliteConnectionFactory _factory = new();

    public void Dispose() => _dir.Dispose();

    private static readonly ProcessKey CrashedProcess = new(2484, new DateTime(2026, 10, 2, 9, 24, 0, DateTimeKind.Utc).Ticks);

    private static SystemEvent Crash(long recordId) => new(
        new DateTimeOffset(2026, 10, 2, 9, 25, 21, TimeSpan.Zero), "Application", recordId, "Application Error", 1000, EventLevel.Error,
        EventCategory.ApplicationCrash, Severity.High, "IAStorDataMgrSvc.exe crashed (0xC0000005 in unknown)", "Faulting application name: ...",
        ProcessId: 2484, ProcessName: "IAStorDataMgrSvc.exe", RuleId: "app.crash", Process: CrashedProcess,
        Data: new Dictionary<string, string> { ["exceptionCode"] = "0xC0000005" });

    private static SystemEvent DotnetCrash(long recordId) => new(
        new DateTimeOffset(2026, 10, 2, 9, 25, 21, TimeSpan.Zero), "Application", recordId, ".NET Runtime", 1026, EventLevel.Error,
        EventCategory.ApplicationCrash, Severity.High, "IAStorDataMgrSvc.exe terminated by an unhandled .NET exception", "Application: ...",
        ProcessId: 2484, ProcessName: "IAStorDataMgrSvc.exe", RuleId: "app.dotnet-unhandled-exception", Process: CrashedProcess);

    private static SystemEvent ServiceCrash(long recordId) => new(
        new DateTimeOffset(2026, 10, 2, 9, 25, 22, TimeSpan.Zero), "System", recordId, "Service Control Manager", 7034, EventLevel.Error,
        EventCategory.Service, Severity.Medium, "Service terminated unexpectedly: Intel(R) Rapid Storage Technology", "The ... service terminated unexpectedly.",
        RuleId: "service.crashed", ServiceName: "IAStorDataMgrSvc");

    private static SystemEvent LinkDown() => new(
        new DateTimeOffset(2026, 10, 2, 9, 26, 0, TimeSpan.Zero), "pcdiag.network", null, "PcDiagnostic", 1, EventLevel.Warning,
        EventCategory.Network, Severity.Medium, "Network link down: Ethernet 1", "Ethernet 1 (Ethernet) went down.", RelatedDevice: "Ethernet 1");

    [Fact]
    public async Task Events_are_stored_once_with_crash_and_service_links()
    {
        var paths = await InitializeAsync();
        await using (var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance))
        {
            await consumer.ConsumeAsync(Frame(1, Crash(162590), DotnetCrash(162589), ServiceCrash(319001), LinkDown()), CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));

            // Read again by the backfill after a restart: same channel and record IDs, nothing new.
            await consumer.ConsumeAsync(Frame(2, Crash(162590), ServiceCrash(319001)), CancellationToken.None);
            await consumer.FlushAsync(CancellationToken.None);
        }

        await using var c = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM WindowsEvents;")).ShouldBe(4L);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM WindowsEvents WHERE record_id IS NULL;")).ShouldBe(1L);

        // The crash is linked to process history once, although two events reported it.
        (await ScalarAsync(c, "SELECT proc_key FROM WindowsEvents WHERE record_id = 162590;")).ShouldBe(CrashedProcess.ToStorageId());
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM ProcessEvents WHERE kind = {(int)ProcessEventKind.Crashed};")).ShouldBe(1L);
        (await ScalarAsync(c, "SELECT name FROM ProcessIdentity WHERE proc_key = $k;", CrashedProcess.ToStorageId())).ShouldBe("IAStorDataMgrSvc.exe");
        (await ScalarAsync(c, "SELECT data_json FROM WindowsEvents WHERE record_id = 162590;")).ShouldBe("""{"exceptionCode":"0xC0000005"}""");

        // The service failure references its Windows event.
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM ServiceEvents WHERE name = 'IAStorDataMgrSvc' AND kind = {(int)ServiceEventKind.Failed};")).ShouldBe(1L);
        (await ScalarAsync(c, "SELECT event_ref = (SELECT id FROM WindowsEvents WHERE record_id = 319001) FROM ServiceEvents;")).ShouldBe(1L);
        (await ScalarAsync(c, "SELECT service_name FROM WindowsEvents WHERE record_id = 319001;")).ShouldBe("IAStorDataMgrSvc");
    }

    [Fact]
    public async Task Events_without_a_stored_message_have_null_message()
    {
        var paths = await InitializeAsync();
        await using (var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance))
        {
            await consumer.ConsumeAsync(Frame(1, LinkDown() with { Message = string.Empty }), CancellationToken.None);
            await consumer.FlushAsync(CancellationToken.None);
        }

        await using var c = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(c, "SELECT message IS NULL FROM WindowsEvents;")).ShouldBe(1L);
    }

    private async Task<DataPaths> InitializeAsync()
    {
        var paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);
        return paths;
    }

    private SampleFrame Frame(long seq, params SystemEvent[] events) => new(seq, _time.GetUtcNow(), Events: events);

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, object? parameter = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameter is not null)
        {
            command.Parameters.AddWithValue("$k", parameter);
        }

        return await command.ExecuteScalarAsync();
    }

    private sealed class Monitor : IOptionsMonitor<StorageOptions>
    {
        public StorageOptions CurrentValue { get; } = new() { WriteBehindSeconds = 60 };

        public StorageOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<StorageOptions, string?> listener) => null;
    }
}
