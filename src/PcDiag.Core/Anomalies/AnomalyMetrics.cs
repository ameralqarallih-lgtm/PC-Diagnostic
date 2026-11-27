using System.Globalization;

namespace PcDiag.Core.Anomalies;

/// <summary>A value the anomaly rules can watch.</summary>
/// <param name="Id">Metric ID used in rules, e.g. "gpu.util".</param>
/// <param name="Label">Label, e.g. "GPU utilization".</param>
/// <param name="Unit">Unit ("%", "°C", "GB", "MHz", "ms" or "" for counts).</param>
/// <param name="PerDevice">Evaluated separately for each GPU, disk or network adapter.</param>
/// <param name="Min">Smallest threshold a user can set.</param>
/// <param name="Max">Largest threshold a user can set.</param>
public sealed record AnomalyMetric(string Id, string Label, string Unit, bool PerDevice, double Min, double Max)
{
    /// <summary>A value with its unit, e.g. "99 %", "0.8 GB", "2100 MHz".</summary>
    public string Format(double value) => AnomalyMetrics.Format(value, Unit);
}

/// <summary>The metrics anomaly rules can use. The engine extracts them from each sample frame.</summary>
public static class AnomalyMetrics
{
    /// <summary>CPU utilization (Task Manager style).</summary>
    public const string Cpu = "cpu";

    /// <summary>CPU package temperature.</summary>
    public const string CpuTemperature = "cpu.temp";

    /// <summary>Available physical memory in GB.</summary>
    public const string MemoryAvailable = "memory.available";

    /// <summary>Committed memory as a share of the commit limit.</summary>
    public const string MemoryCommit = "memory.commit";

    /// <summary>GPU utilization (busiest engine).</summary>
    public const string GpuUtilization = "gpu.util";

    /// <summary>GPU core temperature.</summary>
    public const string GpuTemperature = "gpu.temp";

    /// <summary>GPU hotspot temperature.</summary>
    public const string GpuHotspot = "gpu.hotspot";

    /// <summary>GPU core clock.</summary>
    public const string GpuCoreClock = "gpu.core";

    /// <summary>Dedicated GPU memory in use, as a share of its size.</summary>
    public const string GpuVram = "gpu.vram";

    /// <summary>The slower of read and write latency.</summary>
    public const string DiskLatency = "disk.latency";

    /// <summary>Disk queue length.</summary>
    public const string DiskQueue = "disk.queue";

    /// <summary>Receive and send errors since the previous sample.</summary>
    public const string NetworkErrors = "net.errors";

    /// <summary>How far (%) the game in front runs below its usual frame rate (0 at or above it).</summary>
    public const string GameDrop = "game.drop";

    /// <summary>The game's stutters in the last second (frames far longer than the frames around them).</summary>
    public const string GameStutters = "game.stutters";

    /// <summary>The busiest processor's time in drivers' DPCs or interrupts, whichever is higher (%).</summary>
    public const string DriverTime = "latency.dpc";

    /// <summary>The longest single DPC or ISR of a driver latency measurement (µs; only when one finished).</summary>
    public const string DriverLongest = "latency.driver";

    /// <summary>The share of the last 20 pings to the internet that were lost (%).</summary>
    public const string ConnectionLoss = "conn.loss";

    /// <summary>The share of the last 20 pings to the router that were lost (%).</summary>
    public const string RouterLoss = "conn.router_loss";

    /// <summary>The internet round trip (ms).</summary>
    public const string ConnectionPing = "conn.ping";

    /// <summary>The router round trip (ms).</summary>
    public const string RouterPing = "conn.router";

    /// <summary>A failed DNS lookup (1 for each).</summary>
    public const string DnsFailed = "conn.dns_failed";

    /// <summary>All metrics.</summary>
    public static IReadOnlyList<AnomalyMetric> All { get; } =
    [
        new(Cpu, "CPU utilization", "%", false, 1, 100),
        new(CpuTemperature, "CPU package temperature", "°C", false, 30, 125),
        new(MemoryAvailable, "Available memory", "GB", false, 0.1, 512),
        new(MemoryCommit, "Memory commit", "%", false, 1, 100),
        new(GpuUtilization, "GPU utilization", "%", true, 1, 100),
        new(GpuTemperature, "GPU temperature", "°C", true, 30, 125),
        new(GpuHotspot, "GPU hotspot temperature", "°C", true, 30, 125),
        new(GpuCoreClock, "GPU core clock", "MHz", true, 1, 10_000),
        new(GpuVram, "VRAM in use", "%", true, 1, 100),
        new(DiskLatency, "Disk latency", "ms", true, 1, 10_000),
        new(DiskQueue, "Disk queue length", "", true, 1, 1000),
        new(NetworkErrors, "Network packet errors", "", true, 1, 100_000),
        new(GameDrop, "Game frame rate below its usual", "%", false, 5, 100),
        new(GameStutters, "Game stutters", "", false, 1, 1000),
        new(DriverTime, "Driver time on the busiest processor (DPCs or interrupts)", "%", false, 1, 100),
        new(DriverLongest, "Longest driver DPC or ISR", "µs", false, 50, 1_000_000),
        new(ConnectionLoss, "Lost pings to the internet", "%", false, 1, 100),
        new(RouterLoss, "Lost pings to the router", "%", false, 1, 100),
        new(ConnectionPing, "Internet round trip", "ms", false, 1, 10_000),
        new(RouterPing, "Router round trip", "ms", false, 1, 10_000),
        new(DnsFailed, "Failed DNS lookups", "", false, 1, 1000),
    ];

    private static readonly Dictionary<string, AnomalyMetric> ById = All.ToDictionary(m => m.Id, StringComparer.Ordinal);

    /// <summary>The metric, or null when unknown.</summary>
    public static AnomalyMetric? Find(string? id) => id is not null && ById.TryGetValue(id, out var metric) ? metric : null;

    /// <summary>
    /// Prefix of a metric that is one of the system's readings (what the on-screen panels can show: every processor,
    /// memory, graphics, drive, network and hardware sensor value), e.g. "reading:sensor/cpu/0/temperature/3".
    /// </summary>
    public const string ReadingPrefix = "reading:";

    /// <summary>Whether a metric is one of the system's readings.</summary>
    public static bool IsReading(string? id) => id is not null && id.StartsWith(ReadingPrefix, StringComparison.Ordinal) && id.Length > ReadingPrefix.Length && id.Length <= 240
        && !id.Any(char.IsControl);

    /// <summary>The widest threshold a reading allows (its unit is whatever the reading has).</summary>
    public const double ReadingLimit = 1e12;

    /// <summary>A value with its unit, e.g. "99 %", "0.8 GB", "2100 MHz", "12".</summary>
    public static string Format(double value, string unit)
    {
        var number = unit switch
        {
            "GB" => value < 10 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("0", CultureInfo.InvariantCulture),
            "σ" => value.ToString("0.#", CultureInfo.InvariantCulture),
            _ => Math.Abs(value) < 10 && value != Math.Round(value) ? value.ToString("0.#", CultureInfo.InvariantCulture) : value.ToString("0", CultureInfo.InvariantCulture),
        };
        return unit.Length == 0 ? number : $"{number} {unit}";
    }
}
