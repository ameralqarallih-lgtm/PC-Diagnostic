using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PcDiag.Collectors.Gpu;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Events;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Display;
using PcDiag.Platform.Windows.Display;
using PcDiag.Platform.Windows.Gpu;

namespace PcDiag.Collectors.Display;

/// <summary>
/// Display configuration and changes. Windows notifications (display changes, monitor arrival/removal, display
/// power, console/Remote Desktop switches) arrive on a watcher thread and keep their own timestamps; each tick
/// turns them into changes and, after a change, re-reads the configuration (<c>QueryDisplayConfig</c>) and compares
/// it with the previous one. The configuration is also re-read every 30 s in case a notification was missed.
/// A connect followed by a disconnect within one tick is still recorded, because notifications are not merged.
/// </summary>
public sealed partial class DisplayCollector : IDisplayCollector, IDisposable
{
    /// <summary>Safety re-read interval when no notification arrives.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>A second re-read this long after a notification, once the configuration has settled.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    /// <summary>Notifications older than this when a tick finally runs (the recorder was idle) are discarded.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    private const int MaxPending = 1000;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DisplayCollector> _logger;
    private readonly ConcurrentQueue<(DateTimeOffset At, DisplayNotification Notification)> _pending = new();
    private readonly Lock _gate = new();
    private DisplayChangeWatcher? _watcher;
    private DisplaySnapshot? _last;
    private string? _lastHash;
    private bool _snapshotDue = true;
    private DateTimeOffset _nextCheck;
    private DateTimeOffset? _settleCheck;
    private DateTimeOffset? _lastCollect;
    private DateTimeOffset? _unexplainedChangeAt;
    private int? _power;
    private Dictionary<AdapterLuid, (string Key, string Name)> _adapters = [];
    private int _lastError;

    /// <summary>Creates the collector.</summary>
    public DisplayCollector(TimeProvider timeProvider, ILogger<DisplayCollector> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "display";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.System;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Displays", RequiredPrivilege.StandardUser, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        if (_watcher is null)
        {
            _watcher = DisplayChangeWatcher.Start(OnNotification, out var error);
            if (_watcher is null)
            {
                LogWatcherFailed(_logger, error);
            }
        }

        var snapshot = Read();
        lock (_gate)
        {
            _last = snapshot;
            _nextCheck = _timeProvider.GetUtcNow() + RecheckInterval;
        }

        if (snapshot is null)
        {
            return ValueTask.FromResult(new CollectorStatus(_lastError == DisplayConfig.AccessDenied ? Availability.PermissionDenied : Availability.Error,
                $"QueryDisplayConfig failed with error {_lastError} (it needs the interactive session)"));
        }

        var displays = string.Join("; ", snapshot.Displays.Select(d => $"{DisplayDiff.Name(d)} {DisplayDiff.Mode(d)} {d.Connection}"));
        var session = snapshot.IsRemoteSession ? " [Remote Desktop session: physical monitors are not visible]" : string.Empty;
        var notifications = _watcher is null ? "no notifications (re-read every 30 s)" : "notifications: " + _watcher.Registrations;
        return ValueTask.FromResult(new CollectorStatus(Availability.Available, $"{displays}{session}; {notifications}"));
    }

    /// <inheritdoc />
    public ValueTask<DisplaySample?> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            return ValueTask.FromResult(Collect(_timeProvider.GetUtcNow()));
        }
    }

    /// <inheritdoc />
    public Task<DisplaySnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read() ?? new DisplaySnapshot(_timeProvider.GetUtcNow(), [], DisplaySession.IsRemote), cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _watcher?.Dispose();

    /// <summary>Builds a snapshot from the raw configuration. Exposed for tests.</summary>
    internal static DisplaySnapshot ToSnapshot(DateTimeOffset at, IReadOnlyList<DisplayPathData> paths, bool remote, bool? variableRefresh, Func<AdapterLuid, string?, (string Key, string Name)> adapter)
    {
        var displays = paths.Select(p =>
        {
            var (adapterKey, adapterName) = adapter(p.Adapter, p.AdapterDevicePath);
            var targetKey = DisplayDiff.TargetKeyFromDevicePath(p.MonitorDevicePath) ?? $"{adapterKey}/target-{p.TargetId}";
            return new DisplayTarget(
                targetKey,
                p.FriendlyName ?? string.Empty,
                p.Manufacturer,
                p.ProductCode,
                p.Width,
                p.Height,
                p.RefreshRateHz ?? 0,
                Connection(p.OutputTechnology),
                p.Color is null ? Reading<bool>.Unavailable(Availability.Unsupported) : Reading<bool>.Of(p.Color.HdrActive),
                DisplayReadings.Optional(p.Color?.HdrSupported),
                DisplayReadings.Optional(variableRefresh, Availability.NotPresent),
                p.Rotation switch { 2 => 90, 3 => 180, 4 => 270, _ => 0 },
                p.IsPrimary,
                p.PositionX,
                p.PositionY,
                adapterKey,
                adapterName,
                p.GdiDeviceName,
                p.Color?.Mode,
                p.Color is { BitsPerColorChannel: > 0 } color ? color.BitsPerColorChannel : null,
                p.MonitorDevicePath);
        }).ToList();

        return new DisplaySnapshot(at, displays, remote);
    }

    /// <summary>Maps DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY (wingdi.h) to a connection type.</summary>
    internal static DisplayConnection Connection(uint outputTechnology) => outputTechnology switch
    {
        0 => DisplayConnection.Vga,
        4 => DisplayConnection.Dvi,
        5 => DisplayConnection.Hdmi,
        6 or 11 or 0x80000000 => DisplayConnection.Internal,
        10 => DisplayConnection.DisplayPort,
        15 => DisplayConnection.Wireless,
        16 or 17 => DisplayConnection.Indirect,
        18 => DisplayConnection.UsbC,
        _ => DisplayConnection.Other,
    };

    /// <summary>
    /// Turns the notifications received since the previous tick into changes, and re-reads and compares the
    /// configuration when something happened or a check is due. Exposed for tests (call with <c>_gate</c> held).
    /// </summary>
    internal DisplaySample? Collect(DateTimeOffset now, Func<DisplaySnapshot?>? read = null)
    {
        read ??= Read;
        var changes = new List<DisplayChange>();
        var notified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var arrivals = new List<(DateTimeOffset At, string Path)>();
        DateTimeOffset? displayChangeAt = null;
        var reread = now >= _nextCheck || (_settleCheck is { } settle && now >= settle);

        // After an idle period (recorder stopped), old notifications describe time that was not being recorded.
        var stale = _lastCollect is null || now - _lastCollect > StaleAfter ? now - StaleAfter : DateTimeOffset.MinValue;
        _lastCollect = now;

        while (_pending.TryDequeue(out var item))
        {
            var (at, n) = item;
            if (n.Kind == DisplayNotificationKind.DisplayPower)
            {
                // The first report after registering is the current state, not a change.
                if (_power is { } previous && previous != n.Value && at >= stale)
                {
                    changes.Add(new DisplayChange(at, DisplayEventKind.PowerStateChanged, "all displays", PowerState(previous), PowerState(n.Value)));
                }

                _power = n.Value;
                continue;
            }

            if (at < stale)
            {
                reread = true;
                continue;
            }

            switch (n.Kind)
            {
                case DisplayNotificationKind.DisplayChange:
                    displayChangeAt ??= at;
                    _unexplainedChangeAt ??= at;
                    reread = true;
                    _settleCheck = now + SettleDelay;
                    break;
                case DisplayNotificationKind.SettingChange:
                    reread = true;
                    break;
                case DisplayNotificationKind.MonitorArrived when n.DevicePath is not null:
                    arrivals.Add((at, n.DevicePath));
                    notified.Add(DisplayDiff.TargetKeyFromDevicePath(n.DevicePath)!);
                    reread = true;
                    _settleCheck = now + SettleDelay;
                    break;
                case DisplayNotificationKind.MonitorRemoved when n.DevicePath is not null:
                    var key = DisplayDiff.TargetKeyFromDevicePath(n.DevicePath)!;
                    notified.Add(key);
                    changes.Add(new DisplayChange(at, DisplayEventKind.Disconnected, NameFor(key, n.DevicePath, _last), null, "disconnected", key));
                    reread = true;
                    _settleCheck = now + SettleDelay;
                    break;
                case DisplayNotificationKind.SessionChange when SessionText(n.Value) is { } session:
                    changes.Add(new DisplayChange(at, DisplayEventKind.SessionChanged, "session", null, session));
                    reread = true;
                    _settleCheck = now + SettleDelay;
                    break;
            }
        }

        // The settle re-read: no new notification since, so the configuration had time to settle.
        var settled = _settleCheck is { } due && now >= due && displayChangeAt is null && arrivals.Count == 0 && notified.Count == 0;
        if (settled)
        {
            _settleCheck = null;
        }

        DisplaySnapshot? snapshot = null;
        if (reread || _snapshotDue)
        {
            _nextCheck = now + RecheckInterval;
            snapshot = read();
        }

        if (snapshot is not null)
        {
            foreach (var (at, path) in arrivals)
            {
                var key = DisplayDiff.TargetKeyFromDevicePath(path)!;
                changes.Add(new DisplayChange(at, DisplayEventKind.Connected, NameFor(key, path, snapshot), null, Describe(key, snapshot), key));
            }

            var diff = DisplayDiff.Compare(_last, snapshot, displayChangeAt ?? _unexplainedChangeAt ?? now, notified);
            changes.AddRange(diff);
            if (diff.Count > 0 || notified.Count > 0 || _last?.IsRemoteSession != snapshot.IsRemoteSession)
            {
                _unexplainedChangeAt = null; // the display change notification is accounted for
            }
            else if (settled && _unexplainedChangeAt is { } changedAt)
            {
                // Windows reported a display change, and seconds later nothing differs: record that it happened.
                changes.Add(new DisplayChange(changedAt, DisplayEventKind.Reconfigured, "all displays", null, "display change reported; configuration unchanged"));
                _unexplainedChangeAt = null;
            }

            var hash = Hash(snapshot);
            var snapshotChanged = _snapshotDue || hash != _lastHash;
            _snapshotDue = false;
            _lastHash = hash;
            _last = snapshot;
            if (snapshotChanged || changes.Count > 0)
            {
                return new DisplaySample(now, snapshotChanged ? snapshot : null, changes.OrderBy(c => c.Timestamp).ToList());
            }

            return null;
        }

        foreach (var (at, path) in arrivals)
        {
            var key = DisplayDiff.TargetKeyFromDevicePath(path)!;
            changes.Add(new DisplayChange(at, DisplayEventKind.Connected, NameFor(key, path, null), null, "connected", key));
        }

        return changes.Count == 0 ? null : new DisplaySample(now, null, changes.OrderBy(c => c.Timestamp).ToList());
    }

    /// <summary>Queues a notification as if it came from Windows. Exposed for tests.</summary>
    internal void Notify(DateTimeOffset at, DisplayNotification notification)
    {
        _pending.Enqueue((at, notification));
        while (_pending.Count > MaxPending && _pending.TryDequeue(out _))
        {
        }
    }

    internal static string Hash(DisplaySnapshot snapshot) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { snapshot.IsRemoteSession, snapshot.Displays })));

    private void OnNotification(DisplayNotification notification) => Notify(_timeProvider.GetUtcNow(), notification);

    private DisplaySnapshot? Read()
    {
        var result = DisplayConfig.Query();
        if (result.Error != DisplayConfig.Success)
        {
            if (result.Error != _lastError)
            {
                LogQueryFailed(_logger, result.Error);
            }

            _lastError = result.Error;
            return null;
        }

        _lastError = 0;
        if (result.Paths.Any(p => !_adapters.ContainsKey(p.Adapter)))
        {
            using var kmt = D3dkmtAdapters.Open();
            _adapters = GpuCollector.AssignKeys(GpuCollector.WithoutVirtualTwins(Dxgi.EnumerateAdapters(), kmt.TryGetPciAddress))
                .ToDictionary(a => a.Adapter.Luid, a => (a.Key, a.Adapter.Description));
        }

        return ToSnapshot(_timeProvider.GetUtcNow(), result.Paths, DisplaySession.IsRemote, DisplayConfig.ReadVariableRefreshSetting(), (luid, devicePath) =>
            _adapters.TryGetValue(luid, out var known) ? known : ($"adapter-{luid}", AdapterNameFromPath(devicePath)));
    }

    /// <summary>A name for adapters DXGI does not list (e.g. the Remote Desktop display adapter), from its device path.</summary>
    internal static string AdapterNameFromPath(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
        {
            return "unknown adapter";
        }

        var key = DisplayDiff.TargetKeyFromDevicePath(devicePath)!;
        var parts = key.Split('\\');
        return parts.Length >= 2 ? $"{parts[0]}\\{parts[1]}" : key;
    }

    private static string NameFor(string key, string devicePath, DisplaySnapshot? snapshot) =>
        snapshot?.Displays.FirstOrDefault(d => d.TargetKey.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } d
            ? DisplayDiff.Name(d)
            : DisplayDiff.MonitorIdFromDevicePath(devicePath) ?? key;

    private static string Describe(string key, DisplaySnapshot snapshot) =>
        snapshot.Displays.FirstOrDefault(d => d.TargetKey.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } d
            ? "connected, " + DisplayDiff.Mode(d)
            : "connected (not active)";

    private static string PowerState(int value) => value switch { 0 => "off", 1 => "on", 2 => "dimmed", _ => "unknown" };

    private static string? SessionText(int value) => value switch
    {
        1 => "console connected",
        2 => "console disconnected",
        3 => "Remote Desktop connected",
        4 => "Remote Desktop disconnected",
        _ => null,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Display change notifications are unavailable (error {Error}); the configuration is re-read every 30 s instead")]
    private static partial void LogWatcherFailed(ILogger logger, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "QueryDisplayConfig failed with error {Error}")]
    private static partial void LogQueryFailed(ILogger logger, int error);
}
