using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Core.Incidents;
using PcDiag.Core.Storage;
using PcDiag.Engine.Storage;

namespace PcDiag.Engine.Tests.Storage;

public sealed class StorageManagerTests : IDisposable
{
    private const long MB = 1024 * 1024;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly DataPaths _paths;
    private readonly StorageGuard _guard = new();
    private readonly FakeDisk _disk = new();
    private readonly FakeIncidents _incidents;

    public StorageManagerTests()
    {
        _paths = new DataPaths(_root);
        _paths.EnsureCreated();
        _incidents = new FakeIncidents(_paths);
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

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>A manager whose "GB" is a megabyte (limits and the free space to keep), so the files stay small.</summary>
    private StorageManager Manager(StorageOptions options, FakeTelemetry telemetry, long unit = MB) =>
        new(_paths, new StaticOptionsMonitor<StorageOptions>(options), telemetry, _incidents, _incidents, _guard, _time, NullLogger<StorageManager>.Instance, _disk) { Unit = unit };

    private FakeTelemetry Telemetry(long fullBytes, TimeSpan fullSpan, long summaryBytes = 0, TimeSpan? summarySpan = null) =>
        new(_paths.TelemetryDatabase, _time, fullBytes, Now - fullSpan, summaryBytes, Now - (summarySpan ?? TimeSpan.FromDays(30)));

    [Fact]
    public async Task Beyond_the_most_incidents_kept_the_oldest_unpinned_go_and_pinned_ones_stay()
    {
        _incidents.Add(1, Now.AddDays(-5), MB, pinned: true);
        _incidents.Add(2, Now.AddDays(-4), MB);
        _incidents.Add(3, Now.AddDays(-3), MB);
        _incidents.Add(4, Now.AddDays(-2), MB);
        _incidents.Add(5, Now.AddDays(-1), MB);
        var manager = Manager(new StorageOptions { MaxIncidents = 3, DataLimitGb = 0, KeepFreeGb = 0 }, Telemetry(MB, TimeSpan.FromHours(24)));

        var result = await manager.PassAsync(manual: false, compact: false, CancellationToken.None);

        _incidents.Keys.ShouldBe(["INC-20261009-00001", "INC-20261009-00004", "INC-20261009-00005"]);
        result.FreedBytes.ShouldBe(2 * MB);
        var entry = manager.State.Recent.ShouldHaveSingleItem();
        entry.Kind.ShouldBe(StorageActionKinds.IncidentsRemoved);
        entry.Count.ShouldBe(2);
        entry.Automatic.ShouldBeTrue();
        entry.Detail.ShouldContain("INC-20261009-00002, INC-20261009-00003");
        StorageLog.Read(_paths.StorageLogFile).ShouldHaveSingleItem().Kind.ShouldBe(StorageActionKinds.IncidentsRemoved);
    }

    [Fact]
    public async Task The_character_generators_models_do_not_count_against_the_space_limit()
    {
        // The dev PC: 55 GB of generator models against a 10 GB limit held full detail at its 2-hour floor for nothing.
        var telemetry = Telemetry(2 * MB, TimeSpan.FromHours(24));
        var models = Path.Combine(_root, "characters", "kit", "hf", "model.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(models)!);
        await using (var file = File.Create(models))
        {
            file.SetLength(20 * MB);
        }

        var manager = Manager(new StorageOptions { DataLimitGb = 10, KeepFreeGb = 0 }, telemetry);
        await manager.PassAsync(manual: false, compact: false, CancellationToken.None);

        telemetry.FullDetailFrom.ShouldBe(Now.AddHours(-24)); // nothing shortened
        manager.State.Condition.ShouldBe(StorageCondition.Normal);
        manager.State.UsedBytes.ShouldBe(StorageMeter.Measure(_paths).LimitedBytes);
        StorageMeter.Measure(_paths).Kinds.Single(k => k.Id == "characters").Bytes.ShouldBe(20 * MB);
    }

    [Fact]
    public async Task Over_the_space_limit_full_detail_is_kept_for_a_shorter_time_before_any_incident_goes()
    {
        // 6 MB of full detail over 24 h and 4.5 MB of incidents: 10.5 MB against a 10 MB limit.
        _incidents.Add(1, Now.AddDays(-3), 3 * MB / 2);
        _incidents.Add(2, Now.AddDays(-2), 3 * MB / 2);
        _incidents.Add(3, Now.AddDays(-1), 3 * MB / 2);
        var telemetry = Telemetry(6 * MB, TimeSpan.FromHours(24));
        var manager = Manager(new StorageOptions { DataLimitGb = 10, KeepFreeGb = 0 }, telemetry);

        await manager.PassAsync(manual: false, compact: false, CancellationToken.None);

        _incidents.Keys.Count.ShouldBe(3);
        var trimmed = telemetry.FullDetailFrom!.Value;
        trimmed.ShouldBeGreaterThan(Now.AddHours(-24));
        trimmed.ShouldBeLessThan(Now - StorageLimits.FullDetailFloor);
        StorageMeter.Measure(_paths).TotalBytes.ShouldBeLessThanOrEqualTo((long)(10 * MB * StorageLimits.LimitTarget));
        var entry = manager.State.Recent[0];
        entry.Kind.ShouldBe(StorageActionKinds.FullDetailShortened);
        entry.Detail.ShouldContain("to stay within the 10 GB space limit");
        manager.State.Condition.ShouldBe(StorageCondition.Normal);
    }

    [Fact]
    public async Task Once_telemetry_is_at_its_floors_the_oldest_unpinned_incidents_go_for_the_limit()
    {
        // Telemetry already at its floors; 7 incidents of 2 MB (the oldest pinned): 15 MB against a 10 MB limit.
        _incidents.Add(1, Now.AddDays(-7), 2 * MB, pinned: true);
        for (var i = 2; i <= 7; i++)
        {
            _incidents.Add(i, Now.AddDays(-8 + i), 2 * MB);
        }

        var telemetry = Telemetry(MB, StorageLimits.FullDetailFloor, summaryBytes: 0, summarySpan: StorageLimits.SummariesFloor);
        var manager = Manager(new StorageOptions { DataLimitGb = 10, KeepFreeGb = 0 }, telemetry);

        await manager.PassAsync(manual: false, compact: false, CancellationToken.None);

        // 15 MB - 9.5 MB = 5.5 MB to give back: the three oldest unpinned incidents.
        _incidents.Keys.ShouldBe(["INC-20261009-00001", "INC-20261009-00005", "INC-20261009-00006", "INC-20261009-00007"]);
        telemetry.Trims.ShouldBeEmpty();
        manager.State.Recent[0].Detail.ShouldStartWith("3 oldest incidents removed to stay within the 10 GB space limit");
    }

    [Fact]
    public async Task Pinned_incidents_alone_over_the_limit_leave_it_exceeded_and_say_so()
    {
        _incidents.Add(1, Now.AddDays(-2), 6 * MB, pinned: true);
        _incidents.Add(2, Now.AddDays(-1), 6 * MB, pinned: true);
        var manager = Manager(new StorageOptions { DataLimitGb = 10, KeepFreeGb = 0 }, Telemetry(MB / 2, StorageLimits.FullDetailFloor, summarySpan: StorageLimits.SummariesFloor));

        await manager.PassAsync(manual: false, compact: false, CancellationToken.None);

        _incidents.Keys.Count.ShouldBe(2);
        manager.State.Condition.ShouldBe(StorageCondition.OverLimit);
        manager.State.Message!.ShouldContain("more than the 10 GB limit");
    }

    [Fact]
    public async Task Low_free_space_shortens_telemetry_down_to_its_floors_but_never_removes_incidents()
    {
        _incidents.Add(1, Now.AddDays(-2), MB);
        _incidents.Add(2, Now.AddDays(-1), MB);
        _disk.Free = 3 * StorageLimits.GiB;
        var telemetry = Telemetry(6 * MB, TimeSpan.FromHours(24), summaryBytes: MB, summarySpan: TimeSpan.FromDays(30));
        var manager = Manager(new StorageOptions { DataLimitGb = 0, KeepFreeGb = 5 }, telemetry, unit: StorageLimits.GiB);
        manager.CheckSpace();

        await manager.PassAsync(manual: false, compact: false, CancellationToken.None);

        _incidents.Keys.Count.ShouldBe(2);
        telemetry.FullDetailFrom.ShouldBe(Now - StorageLimits.FullDetailFloor);
        telemetry.SummariesFrom!.Value.ShouldBeGreaterThan(Now.AddDays(-30));
        manager.State.Condition.ShouldBe(StorageCondition.LowSpace);
        manager.State.Recent.Select(a => a.Kind).ShouldContain(StorageActionKinds.FullDetailShortened);
        manager.State.Recent.First(a => a.Kind == StorageActionKinds.FullDetailShortened).Detail.ShouldContain("less than the 5 GB to keep free");
        _guard.WritesPaused.ShouldBeFalse();
    }

    [Fact]
    public void Below_one_gigabyte_free_writing_pauses_and_resumes_above_one_and_a_half()
    {
        var manager = Manager(new StorageOptions { KeepFreeGb = 0 }, Telemetry(MB, TimeSpan.FromHours(1)));

        _disk.Free = 900 * MB;
        manager.CheckSpace();
        _guard.WritesPaused.ShouldBeTrue();
        manager.State.Condition.ShouldBe(StorageCondition.WritesPaused);
        manager.State.Message!.ShouldContain("nothing new is recorded");

        _disk.Free = 1200 * MB; // not yet: half a gigabyte above the pause
        manager.CheckSpace();
        _guard.WritesPaused.ShouldBeTrue();

        _disk.Free = 1600 * MB;
        manager.CheckSpace();
        _guard.WritesPaused.ShouldBeFalse();
        manager.State.Condition.ShouldBe(StorageCondition.Normal);
        manager.State.Recent.Select(a => a.Kind).ShouldBe([StorageActionKinds.WritesResumed, StorageActionKinds.WritesPaused]);
    }

    [Fact]
    public async Task A_clean_up_asked_for_compacts_and_reports_what_it_gave_back()
    {
        var telemetry = Telemetry(4 * MB, TimeSpan.FromHours(12));
        telemetry.AddFreePages(MB);
        var manager = Manager(new StorageOptions { DataLimitGb = 0, KeepFreeGb = 0 }, telemetry);

        var result = await manager.CleanAsync(compact: true, CancellationToken.None);

        result.FreedBytes.ShouldBe(MB);
        result.Message.ShouldBe("Database compacted: 1 MB given back.");
        var entry = result.State.Recent.ShouldHaveSingleItem();
        entry.Kind.ShouldBe(StorageActionKinds.Compacted);
        entry.Automatic.ShouldBeFalse();
        new FileInfo(_paths.TelemetryDatabase).Length.ShouldBe(4 * MB);
    }

    [Fact]
    public async Task Folders_left_by_deleted_incidents_are_removed_once_old_and_new_ones_are_left_alone()
    {
        var old = Directory.CreateDirectory(Path.Combine(_paths.IncidentsDirectory, "INC-20261001-00042"));
        File.WriteAllBytes(Path.Combine(old.FullName, "incident.db"), new byte[4096]);
        Directory.SetLastWriteTimeUtc(old.FullName, Now.UtcDateTime.AddHours(-3));
        var fresh = Directory.CreateDirectory(Path.Combine(_paths.IncidentsDirectory, "INC-20261009-00043"));
        Directory.SetLastWriteTimeUtc(fresh.FullName, Now.UtcDateTime.AddMinutes(-5));
        var other = Directory.CreateDirectory(Path.Combine(_paths.IncidentsDirectory, "not-an-incident"));
        Directory.SetLastWriteTimeUtc(other.FullName, Now.UtcDateTime.AddDays(-3));
        var manager = Manager(new StorageOptions { DataLimitGb = 0, KeepFreeGb = 0 }, Telemetry(MB, TimeSpan.FromHours(1)));

        await manager.PassAsync(manual: false, compact: false, CancellationToken.None);

        Directory.Exists(old.FullName).ShouldBeFalse();
        Directory.Exists(fresh.FullName).ShouldBeTrue();
        Directory.Exists(other.FullName).ShouldBeTrue();
        manager.State.Recent.ShouldHaveSingleItem().Detail.ShouldBe("Files left by 1 deleted incident removed: INC-20261001-00042.");
    }

    [Fact]
    public async Task The_incident_still_recording_is_never_removed()
    {
        _incidents.Add(1, Now.AddDays(-2), MB);
        _incidents.Add(2, Now.AddMinutes(-1), MB);
        _incidents.Active = "INC-20261009-00001";
        var manager = Manager(new StorageOptions { MaxIncidents = 1, DataLimitGb = 0, KeepFreeGb = 0 }, Telemetry(MB, TimeSpan.FromHours(1)));

        await manager.PassAsync(manual: false, compact: false, CancellationToken.None);

        _incidents.Keys.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Within_every_limit_nothing_is_removed()
    {
        _incidents.Add(1, Now.AddDays(-2), MB);
        var telemetry = Telemetry(2 * MB, TimeSpan.FromHours(24));
        var manager = Manager(new StorageOptions(), telemetry, unit: StorageLimits.GiB);
        manager.CheckSpace();

        var result = await manager.CleanAsync(compact: false, CancellationToken.None);

        result.Message.ShouldBe("Everything was already within the limits.");
        telemetry.Trims.ShouldBeEmpty();
        _incidents.Keys.Count.ShouldBe(1);
        result.State.Condition.ShouldBe(StorageCondition.Normal);
        result.State.UsedBytes.ShouldBe(StorageMeter.Measure(_paths).TotalBytes);
        File.Exists(_paths.StorageLogFile).ShouldBeFalse();
    }

    private sealed class FakeDisk : IDiskSpace
    {
        public long Free { get; set; } = 500 * StorageLimits.GiB;

        public DiskSpace? Read(string path) => new("C:", 2000 * StorageLimits.GiB, Free);
    }

    /// <summary>
    /// telemetry.db as a file of the right size: full detail spread evenly from <see cref="FullDetailFrom"/> to now,
    /// summaries from <see cref="SummariesFrom"/>; trimming frees pages, compacting shrinks the file.
    /// </summary>
    private sealed class FakeTelemetry : ITelemetrySpace
    {
        private readonly string _file;
        private readonly TimeProvider _time;
        private long _full;
        private long _summaries;
        private long _free;

        public FakeTelemetry(string file, TimeProvider time, long full, DateTimeOffset fullFrom, long summaries, DateTimeOffset summariesFrom)
        {
            _file = file;
            _time = time;
            _full = full;
            _summaries = summaries;
            FullDetailFrom = fullFrom;
            SummariesFrom = summariesFrom;
            Resize();
        }

        public DateTimeOffset? FullDetailFrom { get; private set; }

        public DateTimeOffset? SummariesFrom { get; private set; }

        public List<(DateTimeOffset Full, DateTimeOffset Summaries)> Trims { get; } = [];

        public void AddFreePages(long bytes)
        {
            _free += bytes;
            Resize();
        }

        public Task<TelemetryCoverage> CoverageAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TelemetryCoverage(FullDetailFrom, SummariesFrom, new FileInfo(_file).Length, _free));

        public Task<long> TrimAsync(DateTimeOffset fullDetailBefore, DateTimeOffset summariesBefore, CancellationToken cancellationToken)
        {
            var now = _time.GetUtcNow();
            if (FullDetailFrom is { } from && fullDetailBefore > from)
            {
                var removed = (long)(_full * Math.Min(1, (fullDetailBefore - from) / (now - from)));
                _full -= removed;
                _free += removed;
                FullDetailFrom = fullDetailBefore;
                Trims.Add((fullDetailBefore, summariesBefore));
            }

            if (SummariesFrom is { } first && summariesBefore > first)
            {
                var removed = (long)(_summaries * Math.Min(1, (summariesBefore - first) / (now - first)));
                _summaries -= removed;
                _free += removed;
                SummariesFrom = summariesBefore;
                Trims.Add((fullDetailBefore, summariesBefore));
            }

            return Task.FromResult(0L);
        }

        public Task<long> CompactAsync(CancellationToken cancellationToken)
        {
            var back = _free;
            _free = 0;
            Resize();
            return Task.FromResult(back);
        }

        private void Resize()
        {
            using var stream = File.Open(_file, FileMode.OpenOrCreate);
            stream.SetLength(_full + _summaries + _free);
        }
    }

    /// <summary>Incidents as folders of a given size, listed and deleted like the recorder's.</summary>
    private sealed class FakeIncidents(DataPaths paths) : IIncidentInventory, IIncidentCapture
    {
        private readonly List<StoredIncident> _items = [];

        event EventHandler<IncidentCapturedEventArgs>? IIncidentCapture.Captured
        {
            add { }
            remove { }
        }

        event EventHandler<IncidentCapturedEventArgs>? IIncidentCapture.Finished
        {
            add { }
            remove { }
        }

        /// <summary>The key of the incident still recording.</summary>
        public string? Active { get; set; }

        Incident? IIncidentCapture.Active => null;

        public DateTimeOffset? ActiveUntil => null;

        public List<string> Keys => _items.Select(i => i.Key).ToList();

        public void Add(int number, DateTimeOffset at, long bytes, bool pinned = false)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"INC-20261009-{number:00000}");
            var folder = Directory.CreateDirectory(paths.GetIncidentDirectory(IncidentKey.Parse(key, CultureInfo.InvariantCulture)));
            using (var stream = File.Open(Path.Combine(folder.FullName, "incident.db"), FileMode.Create))
            {
                stream.SetLength(bytes);
            }

            _items.Add(new StoredIncident(key, at, pinned, Finished: true));
        }

        public Task<IReadOnlyList<StoredIncident>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredIncident>>(_items.OrderBy(i => i.CenterAt).ToList());

        public Task<IncidentEditResult> DeleteAsync(IncidentKey key, CancellationToken cancellationToken)
        {
            var name = key.ToString();
            if (name == Active)
            {
                return Task.FromResult(IncidentEditResult.StillRecording);
            }

            if (_items.RemoveAll(i => i.Key == name) == 0)
            {
                return Task.FromResult(IncidentEditResult.NotFound);
            }

            Directory.Delete(paths.GetIncidentDirectory(key), recursive: true);
            return Task.FromResult(IncidentEditResult.Done);
        }

        public Task<CaptureOutcome> CaptureAsync(IncidentTrigger trigger, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IncidentEditResult> UpdateAsync(IncidentKey key, IncidentUpdate update, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
