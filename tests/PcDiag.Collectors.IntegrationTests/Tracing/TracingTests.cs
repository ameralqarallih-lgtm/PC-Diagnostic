using PcDiag.Core.Abstractions;
using PcDiag.Platform.Windows.Tracing;

namespace PcDiag.Collectors.IntegrationTests.Tracing;

public sealed class TracingTests
{
    [Fact]
    public void Native_event_structures_have_the_windows_x64_layout()
    {
        var (record, properties, header, userData) = EtwProcessIo.Layout();

        header.ShouldBe(80);
        record.ShouldBe(112);
        userData.ShouldBe(96);
        properties.ShouldBe(120);
    }

    [Fact]
    public void Disk_events_count_for_the_process_in_the_event_header()
    {
        // Kernel-Disk write as decoded from a real trace: DiskNumber 1, IrpFlags, TransferSize 4096, Reserved,
        // ByteOffset, FileObject, IORequestPacket, HighResResponseTime; 48 bytes and no thread field.
        var payload = new byte[48];
        BitConverter.TryWriteBytes(payload.AsSpan(0), 1u);
        BitConverter.TryWriteBytes(payload.AsSpan(4), 0x60A01u);
        BitConverter.TryWriteBytes(payload.AsSpan(8), 4096u);
        BitConverter.TryWriteBytes(payload.AsSpan(16), 190_967_238_656L);
        BitConverter.TryWriteBytes(payload.AsSpan(40), 758L);

        EtwProcessIo.Disk(11, payload, 46968).ShouldBe(new EtwProcessIo.Transfer(46968, EtwProcessIo.Kind.DiskWrite, 4096));
        EtwProcessIo.Disk(10, payload, 4).ShouldBe(new EtwProcessIo.Transfer(4, EtwProcessIo.Kind.DiskRead, 4096));
        EtwProcessIo.Disk(14, payload, 4).ShouldBeNull(); // buffers flushed: not a transfer
        EtwProcessIo.Disk(11, payload.AsSpan(0, 8), 4).ShouldBeNull();
    }

    [Fact]
    public void Network_events_carry_their_process_and_size()
    {
        var payload = new byte[24];
        BitConverter.TryWriteBytes(payload.AsSpan(0), 39904u);
        BitConverter.TryWriteBytes(payload.AsSpan(4), 1460u);

        EtwProcessIo.Network(11, payload).ShouldBe(new EtwProcessIo.Transfer(39904, EtwProcessIo.Kind.NetReceive, 1460)); // TCP receive, IPv4
        EtwProcessIo.Network(58, payload).ShouldBe(new EtwProcessIo.Transfer(39904, EtwProcessIo.Kind.NetSend, 1460));    // UDP send, IPv6
        EtwProcessIo.Network(12, payload).ShouldBeNull();
    }

    [Fact]
    public async Task Without_administrator_rights_nothing_starts_and_the_reason_is_given()
    {
        using var io = new EtwProcessIo(new ElevationInfo(false));
        io.StartCollecting().ShouldBeFalse();
        io.Problem.ShouldBe("Needs the recorder to run as administrator.");
        io.Take().ShouldBeEmpty();

        using var wpr = new WprFlightRecorder(new ElevationInfo(false));
        (await wpr.StartAsync(CancellationToken.None)).ShouldBeFalse();
        wpr.Problem.ShouldBe("Needs the recorder to run as administrator.");
    }

    [Fact]
    public void The_flight_recorder_uses_its_own_wpr_instance_profile_and_compressed_saves()
    {
        WprFlightRecorder.WprPath.ShouldEndWith(@"System32\wpr.exe", Case.Insensitive);
        File.Exists(WprFlightRecorder.WprPath).ShouldBeTrue();
        WprFlightRecorder.StartArguments(@"C:\Program Files\PC Diagnostic\agent\PcDiagnostic.wprp").ShouldBe(
            ["-start", @"C:\Program Files\PC Diagnostic\agent\PcDiagnostic.wprp!PcDiagnostic.Light", "-instancename", "PcDiagnostic"]);
        WprFlightRecorder.SaveArguments(@"D:\i\incident.etl", "PC Diagnostic incident INC-1").ShouldBe(
            ["-stop", @"D:\i\incident.etl", "PC Diagnostic incident INC-1", "-skipPdbGen", "-compress", "-instancename", "PcDiagnostic"]);
    }

    [Fact]
    public async Task The_shipped_profile_is_accepted_by_wpr_and_caps_its_memory()
    {
        // The profile is copied next to the program; wpr.exe reads profiles without administrator rights.
        File.Exists(WprFlightRecorder.ProfilePath).ShouldBeTrue();
        var details = await WprAsync("-profiledetails", $"{WprFlightRecorder.ProfilePath}!{WprFlightRecorder.ProfileName}");

        details.ShouldContain("PcDiagnostic.Light.Memory");
        var buffers = System.Text.RegularExpressions.Regex.Matches(details, @"Number of Buffers\s*:\s*(\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        buffers.Sum().ShouldBe(WprFlightRecorder.BufferMegabytes); // 1 MB buffers: a fixed cap, never a share of RAM
        details.ShouldContain("Microsoft-Windows-DxgKrnl");
        details.ShouldContain("Microsoft-Windows-Dwm-Core");
    }

    private static async Task<string> WprAsync(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(WprFlightRecorder.WprPath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, output);
        return output;
    }

    [Fact]
    public void Only_DirectX_present_calls_count_as_frames()
    {
        EtwFrameMonitor.IsPresent(EtwFrameMonitor.Dxgi, 42).ShouldBeTrue();
        EtwFrameMonitor.IsPresent(EtwFrameMonitor.Dxgi, 55).ShouldBeTrue();
        EtwFrameMonitor.IsPresent(EtwFrameMonitor.Dxgi, 43).ShouldBeFalse(); // Present_Stop
        EtwFrameMonitor.IsPresent(EtwFrameMonitor.D3D9, 1).ShouldBeTrue();
        EtwFrameMonitor.IsPresent(EtwFrameMonitor.D3D9, 42).ShouldBeFalse();
        EtwFrameMonitor.IsPresent(Guid.NewGuid(), 42).ShouldBeFalse();
    }
}
