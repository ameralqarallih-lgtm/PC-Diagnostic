using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Agent.Screens;
using PcDiag.Core.Overlay;
using PcDiag.Platform.Windows.Processes;
using PcDiag.Screens;

namespace PcDiag.Agent.Tests;

public sealed class DeviceScreenTests
{
    private static readonly ScreenModel Universal = new("universal-88", "Lian Li Universal Screen 8.8″", 0x1CBE, 0xA088, 480, 1920, FrameFormat.Jpeg, 512_000, 30, 90, Tested: true);

    [Fact]
    public void A_bar_mounted_screen_is_laid_out_landscape_and_its_pictures_turned_onto_the_portrait_panel()
    {
        // Its usual mounting (the model's 90°) unless set; laid out 1920×480.
        ScreenFrames.RotationOf(Universal, -1).ShouldBe(90);
        ScreenFrames.RotationOf(Universal, 270).ShouldBe(270);
        ScreenFrames.LayoutSize(Universal, 90).ShouldBe(new Size(1920, 480));
        ScreenFrames.LayoutSize(Universal, 180).ShouldBe(new Size(480, 1920));

        using var layout = new Bitmap(1920, 480);
        using (var g = Graphics.FromImage(layout))
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 1920, 480), Color.Navy, Color.Teal, 0f);
            g.FillRectangle(brush, 0, 0, 1920, 480);
            g.FillRectangle(Brushes.OrangeRed, 0, 0, 1920, 24);
        }

        var frame = ScreenFrames.Make(layout, Universal, 90)!;
        frame.Length.ShouldBeLessThanOrEqualTo(512_000);
        using var decoded = new Bitmap(new MemoryStream(frame));
        decoded.Size.ShouldBe(new Size(480, 1920));

        // Turned clockwise: the layout's top edge (red) is the panel's right edge.
        var right = decoded.GetPixel(470, 960);
        (right.R > 180 && right.G < 120).ShouldBeTrue($"right edge {right}");

        // A layout of the wrong size for the panel is not sent.
        ScreenFrames.Make(layout, Universal, 0).ShouldBeNull();
        ScreenFrames.Encode(new Bitmap(4, 2), FrameFormat.Rgb565, 0).Length.ShouldBe(16);
    }

    [Fact]
    public void The_program_holding_a_screen_is_paused_written_down_first_and_started_again_in_the_opposite_order()
    {
        var file = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"), "screen-holders.json");
        var services = new FakeServices("LConnectServiceWatcher", "LConnectService");
        ScreenHolder[] holders = [new("LConnectServiceWatcher", "L-Connect"), new("LConnectService", "L-Connect"), new("NotRunning", "Other")];
        try
        {
            var first = new ScreenHolders(file, services, NullLogger.Instance);
            first.Take(holders).ShouldBeNull();
            services.Log.ShouldBe(["stop LConnectServiceWatcher", "stop LConnectService"]);
            first.Paused.Select(p => p.Service).ShouldBe(["LConnectServiceWatcher", "LConnectService"]);
            File.Exists(file).ShouldBeTrue();

            // Taken again: nothing more to pause.
            first.Take(holders).ShouldBeNull();
            services.Log.Count.ShouldBe(2);

            // The recorder stopped before starting them again: the next one does, the last paused first.
            var next = new ScreenHolders(file, services, NullLogger.Instance);
            next.Paused.Count.ShouldBe(2);
            next.Release();
            services.Log.Skip(2).ShouldBe(["start LConnectService", "start LConnectServiceWatcher"]);
            next.Paused.ShouldBeEmpty();
            File.Exists(file).ShouldBeFalse();

            // A program that cannot be paused: the screen is not used, and nothing is left written down for it.
            services.Stubborn = "LConnectService";
            var third = new ScreenHolders(file, services, NullLogger.Instance);
            third.Take(holders)!.ShouldContain("L-Connect could not be paused");
            third.Paused.Select(p => p.Service).ShouldBe(["LConnectServiceWatcher"]);
            third.Release();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public async Task A_screen_in_use_shows_the_newest_picture_sets_its_backlight_and_says_when_its_session_fails()
    {
        // The live failure: the pump died at its first wake (Interlocked on an int?), the next picture took the host down.
        var session = new FakeSession();
        var statuses = new List<ScreenHostProtocol.Status>();
        var stopped = new TaskCompletionSource<Exception>();
        using var screen = new ScreenHostMode.Screen("usb:test", session, s =>
        {
            lock (statuses)
            {
                statuses.Add(s);
            }

            return Task.CompletedTask;
        }, (_, ex) => stopped.TrySetResult(ex));

        screen.SetBrightness(40);
        screen.Show([1, 2, 3]);
        await WaitUntil(() => session.Shown.Count == 1);
        session.Brightness.ShouldBe([40]);
        session.Shown[0].ShouldBe(new byte[] { 1, 2, 3 });
        screen.Pictures.ShouldBe(1);

        // A session that fails: the screen says it stopped (and is closed); pictures and closing after are harmless.
        session.Fail = true;
        screen.Show([4]);
        (await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message.ShouldBe("unplugged");
        await screen.Ended.WaitAsync(TimeSpan.FromSeconds(5));
        session.Disposed.ShouldBeTrue();
        screen.Show([5]);
        screen.SetBrightness(10);
        screen.Dispose();
        screen.Dispose();
    }

    [Fact]
    public void Programs_paused_for_a_screen_are_started_again_only_when_no_screen_is_to_be_used_or_it_has_been_gone_a_while()
    {
        var now = new DateTime(2026, 10, 9, 9, 41, 12, DateTimeKind.Utc);

        // Nothing to be used: at once.
        DeviceScreens.ReleaseHolders(0, 0, null, now).ShouldBeTrue();

        // The screen to be used is missing for a moment (the screen host starting again lists none yet): kept paused.
        DeviceScreens.ReleaseHolders(1, 0, now, now).ShouldBeFalse();
        DeviceScreens.ReleaseHolders(1, 0, now - TimeSpan.FromSeconds(10), now).ShouldBeFalse();

        // Gone a while (unplugged): started again.
        DeviceScreens.ReleaseHolders(1, 0, now - DeviceScreens.MissingGrace, now).ShouldBeTrue();
        DeviceScreens.ReleaseHolders(1, 1, null, now).ShouldBeFalse();
    }

    [Fact]
    public void A_program_written_down_as_paused_that_runs_again_is_paused_again()
    {
        var file = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"), "screen-holders.json");
        var services = new FakeServices("LConnectService");
        ScreenHolder[] holders = [new("LConnectService", "L-Connect")];
        try
        {
            new ScreenHolders(file, services, NullLogger.Instance).Take(holders).ShouldBeNull();

            // Windows started it again at sign-in, and the list still says paused.
            services.Start("LConnectService");
            var next = new ScreenHolders(file, services, NullLogger.Instance);
            next.Paused.Count.ShouldBe(1);
            next.Take(holders).ShouldBeNull();
            services.IsRunning("LConnectService").ShouldBeFalse();
            next.Paused.Count.ShouldBe(1);
            next.Release();
            services.IsRunning("LConnectService").ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void A_usb_screens_settings_are_checked()
    {
        var config = new OverlayConfig(Devices: [new OverlayDevice("usb:1cbe:a088:34314314969ec304", Use: true, Rotation: 90, Zoom: 150)]);
        OverlayStyles.Validate(config).ShouldBeNull();
        config.DeviceOf("usb:1cbe:a088:34314314969ec304").Zoom.ShouldBe(150);
        config.DeviceOf("usb:other").ShouldBe(new OverlayDevice("usb:other"));
        OverlayStyles.Validate(config with { Devices = [new OverlayDevice("usb:x", Rotation: 45)] }).ShouldNotBeNull();
        OverlayStyles.Validate(config with { Devices = [new OverlayDevice("usb:x", Background: "black")] }).ShouldNotBeNull();
        OverlayStyles.Validate(config with { Devices = [new OverlayDevice("usb:x"), new OverlayDevice("usb:x")] }).ShouldNotBeNull();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }

    private sealed class FakeSession : IScreenSession
    {
        public string? Firmware => "test";

        public List<byte[]> Shown { get; } = [];

        public List<int> Brightness { get; } = [];

        public bool Fail { get; set; }

        public bool Disposed { get; private set; }

        public bool Present(ReadOnlySpan<byte> frame)
        {
            if (Fail)
            {
                throw new IOException("unplugged");
            }

            Shown.Add(frame.ToArray());
            return true;
        }

        public void SetBrightness(int percent) => Brightness.Add(percent);

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeServices(params string[] running) : IServiceSwitch
    {
        private readonly HashSet<string> _running = [.. running];

        public List<string> Log { get; } = [];

        public string? Stubborn { get; set; }

        public bool IsRunning(string name) => _running.Contains(name);

        public ControlOutcome Stop(string name)
        {
            Log.Add("stop " + name);
            if (name == Stubborn)
            {
                return ControlOutcome.Failed("access denied");
            }

            _running.Remove(name);
            return ControlOutcome.Done;
        }

        public ControlOutcome Start(string name)
        {
            Log.Add("start " + name);
            _running.Add(name);
            return ControlOutcome.Done;
        }
    }
}
