using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PcDiag.Collectors.Inventory;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Devices;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Devices;

namespace PcDiag.Collectors.Devices;

/// <summary>
/// Devices connected, disconnected, dropping out or failing, while recording. Plug and Play notifications say when
/// something changed; once the device tree has been quiet for <see cref="Quiet"/>, it is scanned and compared with the
/// previous scan (<see cref="DeviceEvents"/>), so a missed notification is caught by the next scan, at the latest
/// after <see cref="Rescan"/>. Problems that appear without a connection (a device failing with code 43) are found the
/// same way. After a connection change the inventory's device register is refreshed.
/// </summary>
public sealed partial class DeviceChangeCollector : IEventCollector
{
    /// <summary>Quiet time after the last notification before scanning (a dock connects dozens of nodes).</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);

    /// <summary>Longest wait for quiet once something changed.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    /// <summary>Scan interval without notifications.</summary>
    public static readonly TimeSpan Rescan = TimeSpan.FromSeconds(30);

    /// <summary>Scan interval when Windows gives no notifications.</summary>
    public static readonly TimeSpan RescanWithoutNotifications = TimeSpan.FromSeconds(5);

    private readonly InventoryCollector _inventory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeviceChangeCollector> _logger;

    /// <summary>Creates the collector.</summary>
    public DeviceChangeCollector(InventoryCollector inventory, TimeProvider timeProvider, ILogger<DeviceChangeCollector> logger)
    {
        _inventory = inventory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "devices";

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Device connections and problems", RequiredPrivilege.StandardUser, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        var present = DeviceTree.PresentProblems();
        var failing = present.Count(p => p.Value != 0);
        return ValueTask.FromResult(present.Count == 0
            ? new CollectorStatus(Availability.Error, "the device tree could not be read")
            : new CollectorStatus(Availability.Available, $"{present.Count} device nodes connected, {failing} with a problem"));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SystemEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var notes = Channel.CreateBounded<DeviceChangeNote>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var watcher = DeviceNotifications.Start(
            n => notes.Writer.TryWrite(new DeviceChangeNote(_timeProvider.GetUtcNow(), n.InstanceId, n.Kind != DeviceNotificationKind.Removed)),
            out var error);
        if (watcher is null)
        {
            LogNoNotifications(_logger, error, RescanWithoutNotifications.TotalSeconds);
        }
        else
        {
            LogWatching(_logger, watcher.Mode);
        }

        var describer = new DeviceEvents();
        var previous = DeviceTree.PresentProblems();
        var rescan = watcher is null ? RescanWithoutNotifications : Rescan;
        var batch = new List<DeviceChangeNote>();
        while (!cancellationToken.IsCancellationRequested)
        {
            batch.Clear();
            await WaitForChangesAsync(notes.Reader, batch, rescan, cancellationToken).ConfigureAwait(false);

            var now = _timeProvider.GetUtcNow();
            var current = DeviceTree.PresentProblems();
            if (current.Count == 0)
            {
                continue; // the tree could not be read this time; a PC always has device nodes
            }

            var changes = DeviceEvents.Compare(previous, current, batch, now);
            previous = current;
            if (changes.Count == 0)
            {
                continue;
            }

            if (changes.Any(c => c.Kind is NodeChangeKind.Connected or NodeChangeKind.Disconnected or NodeChangeKind.Reconnected or NodeChangeKind.Failed or NodeChangeKind.Recovered))
            {
                _inventory.RequestDeviceRefresh();
            }

            IReadOnlyList<SystemEvent> events;
            try
            {
                events = describer.Describe(changes, Facts, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDescribeFailed(_logger, ex, changes.Count);
                continue;
            }

            foreach (var systemEvent in events)
            {
                yield return systemEvent;
            }
        }
    }

    private static DeviceNodeFacts? Facts(string instanceId)
    {
        try
        {
            return DeviceTree.Read(instanceId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Waits for the first notification (or the rescan time), then for quiet.</summary>
    private async Task WaitForChangesAsync(ChannelReader<DeviceChangeNote> reader, List<DeviceChangeNote> batch, TimeSpan rescan, CancellationToken cancellationToken)
    {
        using (var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            first.CancelAfter(rescan);
            try
            {
                if (!await reader.WaitToReadAsync(first.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return; // time for the periodic scan
            }
        }

        var started = _timeProvider.GetTimestamp();
        while (true)
        {
            while (reader.TryRead(out var note))
            {
                batch.Add(note);
            }

            var left = MaxWait - _timeProvider.GetElapsedTime(started);
            if (left <= TimeSpan.Zero)
            {
                return;
            }

            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quiet.CancelAfter(left < Quiet ? left : Quiet);
            try
            {
                if (!await reader.WaitToReadAsync(quiet.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return; // quiet
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Watching device changes ({Mode})")]
    private static partial void LogWatching(ILogger logger, string mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device notifications are unavailable (CONFIGRET {Error}); the device tree is scanned every {Seconds} s instead")]
    private static partial void LogNoNotifications(ILogger logger, int error, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not describe {Count} device changes")]
    private static partial void LogDescribeFailed(ILogger logger, Exception exception, int count);
}
