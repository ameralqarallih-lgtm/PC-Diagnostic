using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Collectors.Display;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Events;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Display;
using Xunit.Abstractions;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>The collector's handling of notifications, driven by scripted configurations and notifications.</summary>
public sealed class DisplayCollectorLogicTests : IDisposable
{
    private const string MainKey = @"DISPLAY\AUS32F4\5&12862C70&0&UID4353";
    private const string MainPath = @"\\?\DISPLAY#AUS32F4#5&12862c70&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string SidePath = @"\\?\DISPLAY#AUS2721#5&12862c70&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly DisplayCollector _collector;
    private DisplaySnapshot? _current;

    public DisplayCollectorLogicTests()
    {
        _collector = new DisplayCollector(_time, NullLogger<DisplayCollector>.Instance);
        _current = Snapshot(Primary());
    }

    public void Dispose() => _collector.Dispose();

    private static DisplayTarget Primary(double hz = 240) => new(MainKey, "PG32UCDP", "AUS", "32F4", 3840, 2160, hz, DisplayConnection.DisplayPort,
        Reading<bool>.Of(false), Reading<bool>.Of(true), Reading<bool>.Of(true), 0, true, 0, 0, "gpu-10de", "NVIDIA GeForce RTX 4090", MonitorDevicePath: MainPath);

    private static DisplayTarget Side() => new(@"DISPLAY\AUS2721\5&12862C70&0&UID4352", "VG279QM", "AUS", "2721", 2560, 1440, 280, DisplayConnection.DisplayPort,
        Reading<bool>.Of(false), Reading<bool>.Of(false), Reading<bool>.Of(true), 0, false, 3840, 0, "gpu-10de", "NVIDIA GeForce RTX 4090", MonitorDevicePath: SidePath);

    private DisplaySnapshot Snapshot(params DisplayTarget[] displays) => new(_time.GetUtcNow(), displays);

    private DisplaySample? Tick()
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        return _collector.Collect(_time.GetUtcNow(), () => _current);
    }

    private (DisplaySample Sample, TimeSpan After) TickUntilSomething()
    {
        var start = _time.GetUtcNow();
        for (var i = 0; i < 10; i++)
        {
            if (Tick() is { } sample)
            {
                return (sample, _time.GetUtcNow() - start);
            }
        }

        throw new InvalidOperationException("Nothing was reported within 10 ticks.");
    }

    [Fact]
    public void The_first_tick_stores_the_configuration_and_quiet_ticks_store_nothing()
    {
        Tick().ShouldNotBeNull().Snapshot.ShouldNotBeNull();
        Tick().ShouldBeNull();
        Tick().ShouldBeNull();
    }

    [Fact]
    public void A_refresh_rate_change_is_reported_at_the_time_windows_announced_it()
    {
        Tick();
        var announced = _time.GetUtcNow().AddMilliseconds(300);
        _current = Snapshot(Primary(hz: 60));
        _collector.Notify(announced, new DisplayNotification(DisplayNotificationKind.DisplayChange));

        var sample = Tick().ShouldNotBeNull();

        var change = sample.Changes.ShouldHaveSingleItem();
        change.Kind.ShouldBe(DisplayEventKind.RefreshRateChanged);
        (change.OldValue, change.NewValue).ShouldBe(("240 Hz", "60 Hz"));
        change.Timestamp.ShouldBe(announced);
        sample.Snapshot.ShouldNotBeNull().Displays.Single().RefreshRateHz.ShouldBe(60);
    }

    [Fact]
    public void A_brief_unplug_and_replug_within_one_tick_is_still_recorded()
    {
        Tick();
        var t = _time.GetUtcNow();
        _collector.Notify(t.AddMilliseconds(100), new DisplayNotification(DisplayNotificationKind.MonitorRemoved, MainPath));
        _collector.Notify(t.AddMilliseconds(700), new DisplayNotification(DisplayNotificationKind.MonitorArrived, MainPath));

        var changes = Tick().ShouldNotBeNull().Changes;

        changes.Select(c => (c.Kind, c.Target, c.NewValue)).ShouldBe(
        [
            (DisplayEventKind.Disconnected, "PG32UCDP", "disconnected"),
            (DisplayEventKind.Connected, "PG32UCDP", "connected, 3840x2160 @ 240 Hz"),
        ]);
    }

    [Fact]
    public void Unplugging_a_monitor_is_a_disconnect_not_also_a_topology_change()
    {
        _current = Snapshot(Primary(), Side());
        Tick();
        _current = Snapshot(Primary());
        _collector.Notify(_time.GetUtcNow(), new DisplayNotification(DisplayNotificationKind.MonitorRemoved, SidePath));

        var change = Tick().ShouldNotBeNull().Changes.ShouldHaveSingleItem();

        change.Kind.ShouldBe(DisplayEventKind.Disconnected);
        change.Target.ShouldBe("VG279QM");
    }

    [Fact]
    public void A_display_change_that_changes_nothing_is_recorded_once_the_configuration_has_settled()
    {
        Tick();
        var announced = _time.GetUtcNow();
        _collector.Notify(announced, new DisplayNotification(DisplayNotificationKind.DisplayChange));

        Tick().ShouldBeNull(); // re-read right away: no difference yet, so wait for the settle re-read

        var (sample, after) = TickUntilSomething();
        var change = sample.Changes.ShouldHaveSingleItem();
        change.Kind.ShouldBe(DisplayEventKind.Reconfigured);
        change.Timestamp.ShouldBe(announced);
        after.ShouldBeGreaterThanOrEqualTo(DisplayCollector.SettleDelay);
        Tick().ShouldBeNull();
    }

    [Fact]
    public void A_slow_mode_change_is_not_reported_as_unchanged()
    {
        Tick();
        var announced = _time.GetUtcNow();
        _collector.Notify(announced, new DisplayNotification(DisplayNotificationKind.DisplayChange));
        Tick().ShouldBeNull();      // the mode set has not finished yet
        _current = Snapshot(Primary(hz: 144));

        // Found by the settle re-read, dated to the notification, and not also reported as "unchanged".
        var change = TickUntilSomething().Sample.Changes.ShouldHaveSingleItem();
        change.Kind.ShouldBe(DisplayEventKind.RefreshRateChanged);
        change.Timestamp.ShouldBe(announced);
        for (var i = 0; i < 5; i++)
        {
            Tick().ShouldBeNull();
        }
    }

    [Fact]
    public void Display_power_changes_are_recorded_after_the_initial_state()
    {
        Tick();
        _collector.Notify(_time.GetUtcNow(), new DisplayNotification(DisplayNotificationKind.DisplayPower, Value: 1)); // sent on registration
        Tick().ShouldBeNull();

        _collector.Notify(_time.GetUtcNow(), new DisplayNotification(DisplayNotificationKind.DisplayPower, Value: 0));
        var change = Tick().ShouldNotBeNull().Changes.ShouldHaveSingleItem();

        change.Kind.ShouldBe(DisplayEventKind.PowerStateChanged);
        (change.OldValue, change.NewValue).ShouldBe(("on", "off"));
    }

    [Fact]
    public void Remote_desktop_switches_are_session_changes_without_per_monitor_noise()
    {
        Tick();
        _current = new DisplaySnapshot(_time.GetUtcNow(), [Primary() with { TargetKey = @"DISPLAY\DEFAULT_MONITOR\1&31C5ECD4&0&UID256", FriendlyName = "" }], IsRemoteSession: true);
        _collector.Notify(_time.GetUtcNow(), new DisplayNotification(DisplayNotificationKind.SessionChange, Value: 3));

        var sample = Tick().ShouldNotBeNull();

        sample.Changes.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.Kind.ShouldBe(DisplayEventKind.SessionChanged),
            c => c.NewValue.ShouldBe("Remote Desktop connected"));
        sample.Snapshot.ShouldNotBeNull().IsRemoteSession.ShouldBeTrue();
    }

    [Fact]
    public void Notifications_from_while_the_recorder_was_idle_are_discarded()
    {
        Tick();
        _collector.Notify(_time.GetUtcNow(), new DisplayNotification(DisplayNotificationKind.MonitorRemoved, SidePath));
        _time.Advance(TimeSpan.FromMinutes(5)); // recording stopped and started again

        Tick().ShouldBeNull();
    }

    [Theory]
    [InlineData(5u, DisplayConnection.Hdmi)]
    [InlineData(10u, DisplayConnection.DisplayPort)]
    [InlineData(11u, DisplayConnection.Internal)]
    [InlineData(0x80000000u, DisplayConnection.Internal)]
    [InlineData(18u, DisplayConnection.UsbC)]
    [InlineData(0xFFFFFFFFu, DisplayConnection.Other)]
    public void Output_technology_maps_to_connection_types(uint technology, DisplayConnection expected) =>
        DisplayCollector.Connection(technology).ShouldBe(expected);
}

/// <summary>The collector and watcher against this machine's real display configuration.</summary>
public sealed class DisplayCollectorTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_configuration_is_described_with_its_session_type()
    {
        if (DisplayConfigTests.DesktopLocked(output))
        {
            return;
        }

        using var collector = new DisplayCollector(TimeProvider.System, NullLogger<DisplayCollector>.Instance);
        var status = await collector.InitializeAsync(CancellationToken.None);
        output.WriteLine(status.Message);

        status.Availability.ShouldBe(Availability.Available);
        var snapshot = await collector.SnapshotAsync(CancellationToken.None);
        snapshot.Displays.ShouldNotBeEmpty();
        snapshot.IsRemoteSession.ShouldBe(DisplaySession.IsRemote);
        snapshot.Displays.ShouldAllBe(d => !string.IsNullOrEmpty(d.TargetKey) && !string.IsNullOrEmpty(d.AdapterName));
        if (snapshot.IsRemoteSession)
        {
            status.Message!.ShouldContain("Remote Desktop session");
        }

        var first = await collector.CollectAsync(new SampleContext(1, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), false), CancellationToken.None);
        first.ShouldNotBeNull().Snapshot.ShouldNotBeNull();
    }

    [Fact]
    public void The_hidden_window_registers_and_receives_the_current_display_power_state()
    {
        using var received = new ManualResetEventSlim();
        var notifications = new List<DisplayNotification>();
        using var watcher = DisplayChangeWatcher.Start(n =>
        {
            lock (notifications)
            {
                notifications.Add(n);
            }

            if (n.Kind == DisplayNotificationKind.DisplayPower)
            {
                received.Set();
            }
        }, out var error);

        watcher.ShouldNotBeNull($"window creation failed with {error}");
        output.WriteLine("Registered: " + watcher.Registrations);
        watcher.Registrations.ShouldContain("monitor arrival/removal");
        watcher.Registrations.ShouldContain("display power");

        // Windows sends the current state right after RegisterPowerSettingNotification.
        received.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        lock (notifications)
        {
            notifications.First(n => n.Kind == DisplayNotificationKind.DisplayPower).Value.ShouldBeInRange(0, 2);
        }
    }
}
