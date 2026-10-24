using System.Globalization;
using PcDiag.Core.Overlay;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Overlay;

/// <summary>The newest sample of each collector (what the on-screen display reads).</summary>
public sealed record LiveSamples(DateTimeOffset? Timestamp, CpuSample? Cpu, MemorySample? Memory, GpuSample? Gpu, DiskSample? Disk, NetworkSample? Network, SensorSample? Sensors, ProcessSample? Processes = null);

/// <summary>
/// The values the on-screen display can show, from the newest samples: processor, memory, each graphics adapter,
/// drive and network adapter, every hardware sensor, the time and the recorder's state. Ids stay the same across
/// restarts (adapter and drive keys, sensors' logical keys).
/// </summary>
public static class OverlaySources
{
    /// <summary>The values now.</summary>
    /// <param name="samples">The newest samples.</param>
    /// <param name="localNow">The local time (for the clock).</param>
    /// <param name="recording">Whether the recorder is recording.</param>
    /// <param name="game">Frame rates, round trips and the app in front, when they are being measured.</param>
    /// <param name="missing">Expected hardware that is missing (null: no expected setup).</param>
    public static List<OverlaySource> Build(LiveSamples samples, DateTimeOffset localNow, bool recording, GameState? game = null, IReadOnlyList<Core.Devices.MissingHardware>? missing = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var sensors = samples.Sensors?.Readings.GroupBy(r => r.Sensor.LogicalKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var list = new List<OverlaySource>();

        const string System = "This PC";
        // Texts keep a fixed space (Room): the widest they can read, or a width for a free text, shortened to fit.
        list.Add(new("time.clock", "Time", System, string.Empty, null, localNow.ToString("HH:mm", CultureInfo.InvariantCulture), Room: ClockRoom));
        list.Add(new("time.seconds", "Time", System, string.Empty, null, localNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Room: SecondsRoom));
        list.Add(new("time.date", "Date", System, string.Empty, null, localNow.ToString("ddd d MMM", CultureInfo.InvariantCulture), Room: DateRoom));
        list.Add(new("recorder.state", "Recorder", System, string.Empty, null, recording ? "Recording" : "Not recording", Room: RecorderRoom));
        list.Add(new("devices.missing", "Missing hardware", System, string.Empty, missing?.Count));
        list.Add(new("devices.missing_names", "Missing", System, string.Empty, null, missing is null ? "—" : missing.Count == 0 ? "Nothing missing" : Core.Devices.HardwareCheck.Describe(missing), Room: MissingRoom));

        // Gaming: the frame rate of the app drawing frames (the one in front first), and that app's own load.
        const string Gaming = "Gaming";
        var frames = game?.Frames;
        var app = frames?.ProcessId ?? game?.ForegroundProcessId;
        var process = app is { } pid ? samples.Processes?.Processes.FirstOrDefault(p => p.Key.Pid == pid) : null;
        list.Add(new("game.fps", "FPS", Gaming, "fps", frames?.Fps));
        list.Add(new("game.frametime", "Frame time", Gaming, "ms", frames is { FrameTimeMs: > 0 } f ? f.FrameTimeMs : null));
        list.Add(new("game.low1", "1% low", Gaming, "fps", frames?.Low1Fps));
        list.Add(new("game.app", "Game", Gaming, string.Empty, null, AppName(frames?.ProcessName ?? process?.Name), Room: AppRoom));
        list.Add(new("game.cpu", "Game CPU", Gaming, "%", process is null ? null : V(process.CpuPercent)));
        // Its full scale is the PC's memory (as RAM used), so a ring or bar of it needs nothing set.
        list.Add(new("game.memory", "Game RAM", Gaming, "B", process?.WorkingSetBytes.Value is { } ws ? ws : null,
            Max: samples.Memory?.TotalBytes.Value is > 0 ? samples.Memory.TotalBytes.Value : null));

        const string Cpu = "Processor";
        if (samples.Cpu is { } cpu)
        {
            list.Add(new("cpu.usage", "CPU", Cpu, "%", V(cpu.TotalPercent)));
            list.Add(new("cpu.clock", "CPU clock", Cpu, "GHz", V(cpu.FrequencyMhz) / 1000));
            list.Add(new("cpu.temp", "CPU temp", Cpu, "°C", V(SensorKeys.Prefer(cpu.PackageTemperatureC, sensors, SensorKeys.CpuPackageTemperature))));
            list.Add(new("cpu.power", "CPU power", Cpu, "W", V(SensorKeys.Prefer(cpu.PackagePowerW, sensors, SensorKeys.CpuPackagePower))));

            // A game often runs one core flat out while the total looks low.
            var cores = cpu.Cores.Select(c => V(c.UtilizationPercent)).OfType<double>().ToList();
            var clocks = cpu.Cores.Select(c => V(c.FrequencyMhz)).OfType<double>().ToList();
            list.Add(new("cpu.max_core", "Busiest core", Cpu, "%", cores.Count > 0 ? cores.Max() : null));
            list.Add(new("cpu.max_clock", "Fastest core", Cpu, "GHz", clocks.Count > 0 ? clocks.Max() / 1000 : null));
        }

        const string Memory = "Memory";
        if (samples.Memory is { } memory)
        {
            double? total = memory.TotalBytes.Value;
            double? available = memory.AvailableBytes.Value;
            double? used = total is { } t && available is { } a ? t - a : null;
            list.Add(new("memory.usage", "RAM", Memory, "%", used is { } u && total is > 0 ? 100 * u / total.Value : null));
            list.Add(new("memory.used", "RAM used", Memory, "B", used, Max: total));
            list.Add(new("memory.available", "RAM free", Memory, "B", available, Max: total));
            list.Add(new("memory.commit", "Commit", Memory, "%", memory.CommittedBytes.Value is { } c && memory.CommitLimitBytes.Value is > 0 ? 100.0 * c / memory.CommitLimitBytes.Value!.Value : null));
        }

        foreach (var gpu in samples.Gpu?.Adapters ?? [])
        {
            var p = "gpu." + gpu.Adapter.AdapterKey + ".";
            var name = gpu.Adapter.Name;
            double? vramPct = gpu.VramUsedBytes.Value is { } vu && gpu.VramTotalBytes.Value is > 0 ? 100.0 * vu / gpu.VramTotalBytes.Value!.Value : null;
            list.Add(new(p + "usage", "GPU", name, "%", V(gpu.UtilizationPercent)));
            list.Add(new(p + "temp", "GPU temp", name, "°C", V(gpu.TemperatureC)));
            list.Add(new(p + "hotspot", "GPU hot spot", name, "°C", V(SensorKeys.Prefer(gpu.HotspotTemperatureC, sensors, SensorKeys.GpuHotspot(name)))));
            list.Add(new(p + "vram", "VRAM", name, "B", gpu.VramUsedBytes.Value, Max: gpu.VramTotalBytes.Value is > 0 ? gpu.VramTotalBytes.Value : null));
            list.Add(new(p + "vram_pct", "VRAM", name, "%", vramPct));
            list.Add(new(p + "power", "GPU power", name, "W", V(SensorKeys.Prefer(gpu.PowerW, sensors, SensorKeys.GpuPower(name)))));
            list.Add(new(p + "clock", "GPU clock", name, "MHz", V(gpu.CoreClockMhz)));
            list.Add(new(p + "mem_clock", "Memory clock", name, "MHz", V(gpu.MemoryClockMhz)));
            list.Add(new(p + "fan", "GPU fan", name, "RPM", V(gpu.FanRpm)));
        }

        foreach (var disk in samples.Disk?.Disks ?? [])
        {
            var p = "disk." + disk.Disk.DiskKey + ".";
            var name = (string.IsNullOrWhiteSpace(disk.Disk.Model) ? $"Disk {disk.Disk.Number}" : disk.Disk.Model) + (disk.Volumes.Count > 0 ? $" ({string.Join(" ", disk.Volumes)})" : string.Empty);
            var short_ = disk.Volumes.Count > 0 ? disk.Volumes[0] : $"Disk {disk.Disk.Number}";
            list.Add(new(p + "active", short_, name, "%", V(disk.ActivePercent)));
            list.Add(new(p + "read", short_ + " read", name, "B/s", V(disk.ReadBytesPerSec)));
            list.Add(new(p + "write", short_ + " write", name, "B/s", V(disk.WriteBytesPerSec)));
            list.Add(new(p + "temp", short_ + " temp", name, "°C", V(SensorKeys.Prefer(disk.TemperatureC, sensors, SensorKeys.DiskTemperature(disk.Disk.Number)))));
        }

        // Round trips (pinged all the time, once a second).
        list.Add(new("net.ping.router", "Ping router", "Network", "ms", game?.Latency?.RouterMs));
        list.Add(new("net.ping.internet", "Ping internet", "Network", "ms", game?.Latency?.InternetMs));

        foreach (var nic in (samples.Network?.Interfaces ?? []).Where(n => n.Interface.Kind is not (NetworkInterfaceKind.Virtual or NetworkInterfaceKind.Vpn) || n.IsUp))
        {
            var p = "net." + nic.Interface.InterfaceKey + ".";
            list.Add(new(p + "down", "Download", nic.Interface.Name, "B/s", V(nic.ReceiveBytesPerSec)));
            list.Add(new(p + "up", "Upload", nic.Interface.Name, "B/s", V(nic.SendBytesPerSec)));
        }

        // Every hardware sensor by its logical key (aliases repeat a sensor already listed).
        foreach (var reading in (samples.Sensors?.Readings ?? []).Where(r => r.Sensor.AliasOf is null).OrderBy(r => r.Sensor.HardwareName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Sensor.Name, StringComparer.OrdinalIgnoreCase))
        {
            var label = reading.Sensor.Name.Contains(" / ", StringComparison.Ordinal) ? reading.Sensor.Name[(reading.Sensor.Name.IndexOf(" / ", StringComparison.Ordinal) + 3)..] : reading.Sensor.Name;
            list.Add(new("sensor." + reading.Sensor.LogicalKey, label, reading.Sensor.HardwareName, Unit(reading.Sensor.Unit), V(reading.Value)));
        }

        return list;
    }

    private static readonly string[] ClockRoom = ["00:00"];
    private static readonly string[] SecondsRoom = ["00:00:00"];
    private static readonly string[] RecorderRoom = ["Recording", "Not recording"];

    /// <summary>Every day with every month ("Wed 00 May"): the date's widest, whichever it is.</summary>
    private static readonly string[] DateRoom = [.. CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames.SelectMany(d => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames.Where(m => m.Length > 0).Select(m => $"{d} 00 {m}"))];

    /// <summary>An app's name: 14 digits' width (a longer one is shortened with "…").</summary>
    private static readonly string[] AppRoom = [new string('0', 14)];

    /// <summary>Missing hardware: 24 digits' width.</summary>
    private static readonly string[] MissingRoom = [new string('0', 24)];

    /// <summary>An app's process name without its engine's build suffix (FortniteClient-Win64-Shipping reads FortniteClient).</summary>
    internal static string AppName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "—";
        }

        foreach (var suffix in new[] { "-Win64-Shipping", "-WinGDK-Shipping", "-Win32-Shipping", "-Shipping", "_x64", "-x64", "_DX12", "_DX11", "_Vulkan" })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    private static double? V(Reading<double> reading) => reading.Value is { } v && double.IsFinite(v) ? v : null;

    /// <summary>Sensor units as the display writes them ("C" from some providers is °C).</summary>
    private static string Unit(string unit) => unit switch
    {
        "C" or "°C" => "°C",
        _ => unit,
    };
}
