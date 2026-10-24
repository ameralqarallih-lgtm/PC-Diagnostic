using PcDiag.Core.Events;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Display;

namespace PcDiag.Engine.Tests.Display;

public class DisplayDiffTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    internal static DisplayTarget Target(string key = @"DISPLAY\AUS32F4\5&12862C70&0&UID4353", int width = 3840, int height = 2160, double hz = 240, bool primary = true,
        int x = 0, bool? hdr = false, int rotation = 0, string adapter = "gpu-10de-2684-00000000", string name = "PG32UCDP", string? colorMode = null) =>
        new(key, name, "AUS", "32F4", width, height, hz, DisplayConnection.DisplayPort,
            hdr is { } h ? Reading<bool>.Of(h) : Reading<bool>.Unavailable(Availability.Unsupported),
            Reading<bool>.Of(true), Reading<bool>.Of(true), rotation, primary, x, 0, adapter, "NVIDIA GeForce RTX 4090", @"\\.\DISPLAY1", colorMode);

    private static DisplaySnapshot Snap(bool remote = false, params DisplayTarget[] displays) => new(At, displays, remote);

    [Fact]
    public void Refresh_resolution_hdr_and_orientation_changes_are_reported_with_old_and_new_values()
    {
        var before = Snap(false, Target());
        var after = Snap(false, Target(width: 2560, height: 1440, hz: 60, hdr: true, rotation: 90, colorMode: "HDR"));

        var changes = DisplayDiff.Compare(before, after, At);

        changes.Select(c => (c.Kind, c.OldValue, c.NewValue)).ShouldBe(
        [
            (DisplayEventKind.ResolutionChanged, "3840x2160", "2560x1440"),
            (DisplayEventKind.RefreshRateChanged, "240 Hz", "60 Hz"),
            (DisplayEventKind.HdrChanged, "HDR off", "HDR on (HDR)"),
            (DisplayEventKind.OrientationChanged, "0°", "90°"),
        ]);
        changes.ShouldAllBe(c => c.Target == "PG32UCDP" && c.Timestamp == At && c.TargetKey == @"DISPLAY\AUS32F4\5&12862C70&0&UID4353");
    }

    [Theory]
    [InlineData(60.0, 59.94, true)]   // NTSC-style rate change is real
    [InlineData(144.0, 143.997, false)] // rounding noise
    [InlineData(240.0, 240.0, false)]
    public void Refresh_changes_respect_a_small_tolerance(double before, double after, bool reported)
    {
        var changes = DisplayDiff.Compare(Snap(false, Target(hz: before)), Snap(false, Target(hz: after)), At);
        changes.Any(c => c.Kind == DisplayEventKind.RefreshRateChanged).ShouldBe(reported);
    }

    [Fact]
    public void Displays_becoming_active_or_inactive_are_topology_changes_unless_a_monitor_notification_explains_them()
    {
        var main = Target();
        var side = Target(key: @"DISPLAY\AUS2721\5&12862C70&0&UID4352", width: 2560, height: 1440, hz: 280, primary: false, x: 3840, name: "VG279QM");

        var off = DisplayDiff.Compare(Snap(false, main, side), Snap(false, main), At);
        off.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.Kind.ShouldBe(DisplayEventKind.TopologyChanged),
            c => c.Target.ShouldBe("VG279QM"),
            c => c.OldValue.ShouldBe("active, 2560x1440 @ 280 Hz"),
            c => c.NewValue.ShouldBe("not active"));

        var unplugged = DisplayDiff.Compare(Snap(false, main, side), Snap(false, main), At, new HashSet<string> { side.TargetKey.ToLowerInvariant() });
        unplugged.ShouldBeEmpty();

        DisplayDiff.Compare(Snap(false, main), Snap(false, main, side), At).ShouldHaveSingleItem().NewValue.ShouldBe("active, 2560x1440 @ 280 Hz");
    }

    [Fact]
    public void Primary_and_position_changes_are_topology_changes()
    {
        var change = DisplayDiff.Compare(Snap(false, Target()), Snap(false, Target(primary: false, x: -2560)), At).ShouldHaveSingleItem();

        change.Kind.ShouldBe(DisplayEventKind.TopologyChanged);
        change.OldValue.ShouldBe("primary at (0,0) on NVIDIA GeForce RTX 4090");
        change.NewValue.ShouldBe("secondary at (-2560,0) on NVIDIA GeForce RTX 4090");
    }

    [Fact]
    public void Switching_between_console_and_remote_desktop_is_not_a_hardware_change()
    {
        var console = Snap(false, Target());
        var remote = Snap(true, Target(key: @"DISPLAY\DEFAULT_MONITOR\1&31C5ECD4&0&UID256", width: 2560, height: 1600, hz: 32, name: ""));

        DisplayDiff.Compare(console, remote, At).ShouldBeEmpty();
        DisplayDiff.Compare(null, console, At).ShouldBeEmpty();
    }

    [Fact]
    public void Unknown_hdr_state_is_not_reported_as_a_change()
    {
        DisplayDiff.Compare(Snap(false, Target(hdr: null)), Snap(false, Target(hdr: true)), At).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#AUS32F4#5&12862c70&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", @"DISPLAY\AUS32F4\5&12862C70&0&UID4353", "AUS32F4")]
    [InlineData(@"\\?\DISPLAY#Default_Monitor#1&31c5ecd4&0&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", @"DISPLAY\DEFAULT_MONITOR\1&31C5ECD4&0&UID256", "DEFAULT_MONITOR")]
    [InlineData(null, null, null)]
    public void Monitor_keys_come_from_device_paths(string? path, string? key, string? id)
    {
        DisplayDiff.TargetKeyFromDevicePath(path).ShouldBe(key);
        DisplayDiff.MonitorIdFromDevicePath(path).ShouldBe(id);
    }

    [Fact]
    public void Names_fall_back_to_edid_id_then_gdi_name()
    {
        DisplayDiff.Name(Target()).ShouldBe("PG32UCDP");
        DisplayDiff.Name(Target(name: "")).ShouldBe("AUS32F4");
        DisplayDiff.Name(Target(name: "") with { Manufacturer = null }).ShouldBe(@"\\.\DISPLAY1");
    }
}
