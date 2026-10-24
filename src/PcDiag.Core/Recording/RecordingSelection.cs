using PcDiag.Core.Events;

namespace PcDiag.Core.Recording;

/// <summary>
/// What a custom recording stores: data items (see <see cref="RecordingItems"/>) the user picked. Everything else is
/// still sampled for the live dashboard but is not stored, not kept for incidents and not seen by the automatic
/// incident rules. A recording without a selection (null) records everything.
/// </summary>
public sealed class RecordingSelection
{
    /// <summary>Most items a selection may hold (sensors are listed one by one).</summary>
    public const int MaxItems = 2000;

    /// <summary>Selected item IDs.</summary>
    public IList<string> Items { get; init; } = [];

    /// <summary>Creates a selection of <paramref name="items"/>.</summary>
    public static RecordingSelection Of(IEnumerable<string> items) => new() { Items = [.. items.Distinct(StringComparer.Ordinal)] };

    /// <summary>The items as a set for lookups.</summary>
    public IReadOnlySet<string> ToSet() => new HashSet<string>(Items, StringComparer.Ordinal);

    /// <summary>Whether two selections hold the same items (null = everything).</summary>
    public static bool SameAs(RecordingSelection? a, RecordingSelection? b) =>
        a is null || b is null ? a is null && b is null : a.ToSet().SetEquals(b.Items);

    /// <summary>The items of every selection together; null (everything) when any of them records everything.</summary>
    public static RecordingSelection? Union(IEnumerable<RecordingSelection?> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var items = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            if (selection is null)
            {
                return null;
            }

            items.UnionWith(selection.Items);
        }

        return Of(items);
    }
}

/// <summary>
/// The data items a recording can select, as stable IDs. Static sources have fixed items; GPUs, disks and network
/// adapters have the same items per device (<c>gpu:{adapter key}.thermal</c>), and hardware sensors one item each
/// (<c>sensor:{logical key}</c>).
/// </summary>
public static class RecordingItems
{
    /// <summary>CPU utilization (total, user, kernel).</summary>
    public const string CpuUsage = "cpu.usage";

    /// <summary>Per-core utilization and frequency.</summary>
    public const string CpuCores = "cpu.cores";

    /// <summary>Frequency, performance and its limits.</summary>
    public const string CpuClock = "cpu.clock";

    /// <summary>Processor queue, context switches and interrupts.</summary>
    public const string CpuScheduler = "cpu.scheduler";

    /// <summary>Package temperature and power.</summary>
    public const string CpuThermal = "cpu.thermal";

    /// <summary>Memory in use, available, cache, commit and pools.</summary>
    public const string MemoryUsage = "memory.usage";

    /// <summary>Page faults, hard faults, paging and pressure.</summary>
    public const string MemoryPaging = "memory.paging";

    /// <summary>Capacity and free space of every volume.</summary>
    public const string VolumeSpace = "volumes.space";

    /// <summary>Per-process CPU, memory, I/O and GPU figures.</summary>
    public const string ProcessMetrics = "processes.metrics";

    /// <summary>Process starts, exits, hangs and recoveries.</summary>
    public const string ProcessLifecycle = "processes.lifecycle";

    /// <summary>Display configuration and its changes.</summary>
    public const string Display = "display.config";

    /// <summary>Windows services and their changes.</summary>
    public const string Services = "services.changes";

    /// <summary>Hardware, driver and system facts.</summary>
    public const string Inventory = "inventory.hardware";

    /// <summary>GPU data items, per adapter.</summary>
    public static IReadOnlyList<(string Suffix, string Label)> GpuData { get; } =
    [
        ("usage", "Utilization (all engines)"),
        ("vram", "VRAM and shared memory"),
        ("thermal", "Temperatures and fan"),
        ("clocks", "Core and memory clocks"),
        ("power", "Power, limit and throttling"),
        ("processes", "GPU use per process"),
    ];

    /// <summary>Disk data items, per physical disk.</summary>
    public static IReadOnlyList<(string Suffix, string Label)> DiskData { get; } =
    [
        ("activity", "Throughput, IOPS, queue and active time"),
        ("latency", "Read and write latency"),
        ("temperature", "Temperature"),
    ];

    /// <summary>Network data items, per adapter.</summary>
    public static IReadOnlyList<(string Suffix, string Label)> NetworkData { get; } =
    [
        ("throughput", "Throughput, packets and utilization"),
        ("errors", "Errors and discards"),
        ("link", "Link speed and Wi-Fi signal"),
    ];

    /// <summary>A GPU adapter's item.</summary>
    public static string Gpu(string adapterKey, string suffix) => $"gpu:{adapterKey}.{suffix}";

    /// <summary>A physical disk's item.</summary>
    public static string Disk(string diskKey, string suffix) => $"disk:{diskKey}.{suffix}";

    /// <summary>A network adapter's item.</summary>
    public static string Network(string interfaceKey, string suffix) => $"net:{interfaceKey}.{suffix}";

    /// <summary>A hardware sensor's item, by its logical key.</summary>
    public static string Sensor(string logicalKey) => $"sensor:{logicalKey}";

    /// <summary>Windows events of one category.</summary>
    public static string Events(EventCategory category) => $"events.{category}";
}

/// <summary>What a recording can select on this PC now, source by source.</summary>
/// <param name="Sources">Sources in display order.</param>
public sealed record RecordingCatalog(IReadOnlyList<RecordingSource> Sources);

/// <summary>A source of data (a device, the processes, Windows events...) and its items.</summary>
/// <param name="Id">Source ID (e.g. "cpu", "gpu:{key}", "sensors:{hardware}").</param>
/// <param name="Label">Display name.</param>
/// <param name="Kind">cpu, memory, gpu, disk, volumes, network, sensors, processes, events, display, services or inventory.</param>
/// <param name="Items">Its data items.</param>
public sealed record RecordingSource(string Id, string Label, string Kind, IReadOnlyList<RecordingItem> Items);

/// <summary>One selectable data item.</summary>
/// <param name="Id">Item ID (see <see cref="RecordingItems"/>).</param>
/// <param name="Label">Display name.</param>
/// <param name="Detail">Extra description (e.g. a sensor's unit), when any.</param>
public sealed record RecordingItem(string Id, string Label, string? Detail = null);
