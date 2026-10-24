using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Inventory;

namespace PcDiag.Collectors.Inventory;

/// <summary>
/// Hardware, OS, driver and device inventory. The WMI read takes seconds, so it runs in the background (at startup
/// and every <see cref="RefreshInterval"/>) and the finished result is handed to the next inventory tick. When a
/// device is connected or removed, only the device register is read again (fast, from the device tree), at most every
/// <see cref="DeviceRefreshInterval"/>. Storage keeps only changed snapshots.
/// </summary>
public sealed partial class InventoryCollector : IPolledCollector<InventorySample?>
{
    /// <summary>Periodic refresh interval.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    /// <summary>Shortest time between two device-register refreshes after device changes.</summary>
    public static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IOptionsMonitor<PrivacyOptions> _privacy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InventoryCollector> _logger;
    private Task<InventoryResult>? _pending;
    private Task<DeviceRegister>? _pendingDevices;
    private InventoryResult? _last;
    private long _lastStarted;
    private long _lastDevicesStarted;
    private volatile bool _devicesDue;

    /// <summary>Creates the collector.</summary>
    public InventoryCollector(IOptionsMonitor<PrivacyOptions> privacy, TimeProvider timeProvider, ILogger<InventoryCollector> logger)
    {
        _privacy = privacy;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "inventory";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.Inventory;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Hardware, driver and device inventory", RequiredPrivilege.StandardUser, CostClass.Expensive);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        Start();
        return ValueTask.FromResult(new CollectorStatus(Availability.Available, "inventory read started in the background"));
    }

    /// <summary>Requests a full refresh on the next tick.</summary>
    public void RequestRefresh() => _lastStarted = 0;

    /// <summary>Requests a refresh of the device register (a device was connected or removed).</summary>
    public void RequestDeviceRefresh() => _devicesDue = true;

    /// <inheritdoc />
    public ValueTask<InventorySample?> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        InventorySample? sample = null;
        if (_pending is { IsCompleted: true } finished)
        {
            _pending = null;
            if (finished.IsCompletedSuccessfully)
            {
                var r = finished.Result;
                _last = r;
                sample = new InventorySample(context.Timestamp, r.System, r.HardwareJson, r.Drivers, r.IoDevices);
                LogInventoryRead(_logger, r.System.Count, r.Drivers.Count, r.IoDevices.Count);
            }
            else
            {
                LogInventoryFailed(_logger, finished.Exception?.GetBaseException());
            }
        }
        else if (_pendingDevices is { IsCompleted: true } devices)
        {
            _pendingDevices = null;
            if (devices.IsCompletedSuccessfully && _last is { } last)
            {
                var system = new SortedDictionary<string, string>(last.System.ToDictionary(), StringComparer.Ordinal);
                SystemInventory.PutDeviceFacts(system, devices.Result);
                _last = last with { System = system, IoDevices = devices.Result.Devices };
                sample = new InventorySample(context.Timestamp, system, last.HardwareJson, last.Drivers, devices.Result.Devices);
            }
            else if (!devices.IsCompletedSuccessfully)
            {
                LogInventoryFailed(_logger, devices.Exception?.GetBaseException());
            }
        }

        if (_pending is null && (_lastStarted == 0 || _timeProvider.GetElapsedTime(_lastStarted) >= RefreshInterval))
        {
            _devicesDue = false; // the full read includes the devices
            Start();
        }
        else if (_devicesDue && _pending is null && _pendingDevices is null && _last is not null
                 && (_lastDevicesStarted == 0 || _timeProvider.GetElapsedTime(_lastDevicesStarted) >= DeviceRefreshInterval))
        {
            _devicesDue = false;
            _lastDevicesStarted = _timeProvider.GetTimestamp();
            _pendingDevices = Task.Run(SystemInventory.ReadDevices);
        }

        // An empty-drivers sample would look like "all drivers removed"; frames only carry real results.
        return ValueTask.FromResult(sample);
    }

    private void Start()
    {
        _lastStarted = _timeProvider.GetTimestamp();
        var includeComputerName = _privacy.CurrentValue.CaptureUserNames;
        _pending = Task.Run(() => SystemInventory.Read(includeComputerName));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Inventory read: {Facts} system facts, {Drivers} drivers, {Devices} registered devices")]
    private static partial void LogInventoryRead(ILogger logger, int facts, int drivers, int devices);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inventory read failed")]
    private static partial void LogInventoryFailed(ILogger logger, Exception? exception);
}
