using PcDiag.Core.Configuration;
using PcDiag.Core.Storage;

namespace PcDiag.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly DataPaths _paths;

    public StorageTests()
    {
        _paths = new DataPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public void The_data_folder_is_measured_by_kind_and_by_incident()
    {
        Write("telemetry.db", 5000);
        Write("telemetry.db-wal", 1000);
        Write("telemetry.db-shm", 100);
        Write("incidents.db", 300);
        Write(@"incidents\INC-20261009-00001\incident.db", 2000);
        Write(@"incidents\INC-20261009-00002\incident.db", 700);
        Write(@"incidents\INC-20261009-00002\export.zip", 50);
        Write(@"logs\agent-20261009.log", 400);
        Write(@"diagnostics\d1.json", 30);
        Write("settings.json", 20);
        Write(@"launch\ticket", 10);
        Write(@"characters\kit\hf\model.safetensors", 900);
        Write(@"characters\generator-python\python.exe", 100);

        var usage = StorageMeter.Measure(_paths);

        usage.TotalBytes.ShouldBe(10_610);
        usage.LimitedBytes.ShouldBe(9_610); // the character generator's folder is not counted by the space limit
        usage.Kinds.Select(k => (k.Id, k.Bytes, k.Files)).ShouldBe(
        [
            ("telemetry", 6100, 3),
            ("incidents", 2750, 2),
            ("characters", 1000, 2),
            ("logs", 400, 1),
            ("index", 300, 1),
            ("diagnostics", 30, 1),
            ("other", 30, 2),
        ]);
        usage.IncidentBytes["INC-20261009-00001"].ShouldBe(2000);
        usage.IncidentBytes["INC-20261009-00002"].ShouldBe(750);
    }

    [Fact]
    public void A_missing_data_folder_measures_nothing()
    {
        var usage = StorageMeter.Measure(new DataPaths(Path.Combine(_root, "absent")));

        usage.TotalBytes.ShouldBe(0);
        usage.Kinds.ShouldBeEmpty();
    }

    [Fact]
    public void Repeated_automatic_clean_ups_merge_and_the_log_survives_a_round_trip()
    {
        IReadOnlyList<StorageAction> log = [];
        log = StorageLog.Add(log, new StorageAction(Now, StorageActionKinds.IncidentsRemoved, "2 removed", 100, true, 2));
        log = StorageLog.Add(log, new StorageAction(Now.AddMinutes(10), StorageActionKinds.FullDetailShortened, "kept for 20 h", 50, true));
        log = StorageLog.Add(log, new StorageAction(Now.AddMinutes(20), StorageActionKinds.FullDetailShortened, "kept for 19 h", 30, true));

        // Within the hour and the same kind: one entry with both passes' space.
        var merged = log[0];
        (merged.Detail, merged.FreedBytes, merged.Count, merged.At).ShouldBe(("kept for 19 h", 80L, 2, Now.AddMinutes(20)));

        // Asked for on the page, or more than an hour later: an entry of its own.
        log = StorageLog.Add(log, new StorageAction(Now.AddMinutes(25), StorageActionKinds.FullDetailShortened, "manual", 10, false));
        log = StorageLog.Add(log, new StorageAction(Now.AddHours(3), StorageActionKinds.FullDetailShortened, "later", 10, true));
        log.Count.ShouldBe(4);

        StorageLog.Write(_paths.StorageLogFile, log);
        StorageLog.Read(_paths.StorageLogFile).ShouldBe(log);
    }

    [Fact]
    public void The_log_keeps_the_most_recent_entries_and_a_broken_file_reads_as_empty()
    {
        IReadOnlyList<StorageAction> log = [];
        for (var i = 0; i < StorageLog.MaxEntries + 20; i++)
        {
            log = StorageLog.Add(log, new StorageAction(Now.AddMinutes(i), StorageActionKinds.IncidentsRemoved, $"#{i}", 1, true));
        }

        log.Count.ShouldBe(StorageLog.MaxEntries);
        log[0].Detail.ShouldBe($"#{StorageLog.MaxEntries + 19}");

        File.WriteAllText(_paths.StorageLogFile, "{ not json");
        StorageLog.Read(_paths.StorageLogFile).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(10_000, 1000, true)]
    [InlineData(-1, 5, false)]
    [InlineData(10_001, 5, false)]
    [InlineData(10, 1001, false)]
    public void The_space_limit_and_the_free_space_to_keep_are_checked(int limit, int keepFree, bool valid)
    {
        var result = new StorageOptionsValidator().Validate(null, new StorageOptions { DataLimitGb = limit, KeepFreeGb = keepFree });

        result.Succeeded.ShouldBe(valid);
    }

    [Fact]
    public void The_defaults_limit_the_data_to_10_GB_and_keep_5_GB_free()
    {
        var options = new StorageOptions();

        (options.DataLimitGb, options.KeepFreeGb, options.MaxIncidents).ShouldBe((10, 5, 0));
    }

    [Fact]
    public void The_drive_of_the_data_folder_is_read()
    {
        var drive = new DriveDiskSpace().Read(_root).ShouldNotBeNull();

        drive.TotalBytes.ShouldBeGreaterThan(0);
        drive.FreeBytes.ShouldBeInRange(0, drive.TotalBytes);
        drive.Name.ShouldEndWith(":");
    }
}
