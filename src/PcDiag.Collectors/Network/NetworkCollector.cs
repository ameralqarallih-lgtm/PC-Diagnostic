using System.Globalization;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Network;

namespace PcDiag.Collectors.Network;

/// <summary>
/// Per-interface throughput, packets, errors, discards, link speed and utilization (GetIfEntry2 through
/// <see cref="NetworkInterface.GetIPStatistics"/>), Wi-Fi signal strength (WLAN RSSI, not location-restricted), and
/// link changes, which are recorded as events. The interface list refreshes on Windows network-change notifications
/// and every 30 s, so a change is noticed within that time.
/// Per-process network activity needs ETW (Enhanced mode, Phase 14).
/// </summary>
public sealed partial class NetworkCollector : INetworkCollector, IDisposable
{
    /// <summary>Interface list refresh interval when no change notification arrives.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private static readonly string[] SystemTunnelMarkers = ["Teredo", "ISATAP", "6to4", "IP-HTTPS"];
    private static readonly string[] SystemPlumbingMarkers = ["WAN Miniport", "RAS Async Adapter", "Kernel Debug", "Virtual Switch Extension Adapter"];
    private static readonly string[] VirtualMarkers = ["Hyper-V", "vEthernet", "VirtualBox", "VMware", "WSL", "Virtual Adapter", "Docker"];
    private static readonly string[] VpnMarkers = ["VPN", "WireGuard", "Wintun", "TAP-Windows", "OpenVPN", "Tailscale", "ZeroTier", "Cisco AnyConnect", "Fortinet", "PANGP"];

    private readonly IOptionsMonitor<PrivacyOptions> _privacy;
    private readonly ISystemEventSink _events;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NetworkCollector> _logger;
    private readonly Dictionary<string, Previous> _previous = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _everUp = new(StringComparer.OrdinalIgnoreCase);
    private List<(NetworkInterface Nic, NetworkInterfaceDescriptor Descriptor)> _interfaces = [];
    private Dictionary<string, (bool Up, long Speed)> _lastState = new(StringComparer.OrdinalIgnoreCase);
    private WlanClient? _wlan;
    private uint _wlanError;
    private long _lastRefresh;
    private volatile bool _changed = true;

    /// <summary>Creates the collector.</summary>
    public NetworkCollector(IOptionsMonitor<PrivacyOptions> privacy, ISystemEventSink events, TimeProvider timeProvider, ILogger<NetworkCollector> logger)
    {
        _privacy = privacy;
        _events = events;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "network";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.System;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Network", RequiredPrivilege.StandardUser, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Refresh();
        var summary = string.Join("; ", _interfaces.Select(i => $"{i.Descriptor.Name} ({i.Descriptor.Kind}, {(i.Nic.OperationalStatus == OperationalStatus.Up ? "up" : "down")})"));
        return ValueTask.FromResult(new CollectorStatus(_interfaces.Count == 0 ? Availability.NotPresent : Availability.Available, summary));
    }

    /// <inheritdoc />
    public ValueTask<NetworkSample> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_changed || _timeProvider.GetElapsedTime(_lastRefresh) >= RefreshInterval)
        {
            Refresh();
        }

        var now = _timeProvider.GetTimestamp();
        var samples = new List<NetworkInterfaceSample>(_interfaces.Count);
        foreach (var (nic, descriptor) in _interfaces)
        {
            samples.Add(Sample(nic, descriptor, now));
        }

        return ValueTask.FromResult(new NetworkSample(context.Timestamp, samples));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _wlan?.Dispose();
    }

    /// <summary>Classifies an interface from its type and description.</summary>
    internal static NetworkInterfaceKind Classify(NetworkInterfaceType type, string description)
    {
        bool Has(string[] markers) => markers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));

        if (type == NetworkInterfaceType.Wireless80211)
        {
            return NetworkInterfaceKind.Wifi;
        }

        if (type is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2)
        {
            return NetworkInterfaceKind.Cellular;
        }

        if (description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkInterfaceKind.Bluetooth;
        }

        if (Has(VpnMarkers) || type == NetworkInterfaceType.Ppp)
        {
            return NetworkInterfaceKind.Vpn;
        }

        if (Has(VirtualMarkers))
        {
            return NetworkInterfaceKind.Virtual;
        }

        return type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit
            ? NetworkInterfaceKind.Ethernet
            : NetworkInterfaceKind.Other;
    }

    /// <summary>
    /// Whether an interface is worth recording: not an NDIS filter layer (same traffic as the adapter below it), not
    /// loopback, not a built-in IPv6 transition tunnel, and not non-hardware system plumbing. See
    /// docs/spikes/2026-09-29-network-interfaces.md.
    /// </summary>
    internal static bool IsRelevant(NetworkInterfaceType type, string description, InterfaceFlags? flags)
    {
        if (flags is { IsFilter: true } || type == NetworkInterfaceType.Loopback)
        {
            return false;
        }

        if (SystemTunnelMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Without flags (GetIfTable2 failed), fall back to the naming pattern of filter layers ("…Filter-0000").
        if (flags is null && description.Length > 5 && description[^5] == '-' && description[^4..].All(char.IsAsciiDigit))
        {
            return false;
        }

        return flags is { IsHardware: true } || !SystemPlumbingMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A counter delta, or null when the counter went backwards (interface reset).</summary>
    internal static long? Delta(long current, long previous) => current >= previous ? current - previous : null;

    private NetworkInterfaceSample Sample(NetworkInterface nic, NetworkInterfaceDescriptor descriptor, long now)
    {
        var up = nic.OperationalStatus == OperationalStatus.Up;
        Stats? stats = null;
        try
        {
            var s = nic.GetIPStatistics();
            stats = new Stats(
                s.BytesReceived,
                s.BytesSent,
                s.UnicastPacketsReceived + s.NonUnicastPacketsReceived,
                s.UnicastPacketsSent + s.NonUnicastPacketsSent,
                s.IncomingPacketsWithErrors,
                s.OutgoingPacketsWithErrors,
                s.IncomingPacketsDiscarded,
                s.OutgoingPacketsDiscarded);
        }
        catch (NetworkInformationException)
        {
            // The interface went away between refreshes.
        }

        var speed = nic.Speed > 0 ? Reading<long>.Of(nic.Speed) : Reading<long>.Unavailable(Availability.Unsupported);
        if (stats is null)
        {
            var error = Reading<double>.Unavailable(Availability.Error);
            var errorLong = Reading<long>.Unavailable(Availability.Error);
            return new NetworkInterfaceSample(descriptor, error, error, error, error, errorLong, errorLong, errorLong, errorLong, speed, error, up,
                Reading<double>.Unavailable(Availability.NotPresent), Reading<double>.Unavailable(Availability.NotPresent));
        }

        Reading<double> rxBps = Reading<double>.Pending, txBps = Reading<double>.Pending, rxPps = Reading<double>.Pending, txPps = Reading<double>.Pending;
        Reading<long> rxErr = Reading<long>.Pending, txErr = Reading<long>.Pending, rxDisc = Reading<long>.Pending, txDisc = Reading<long>.Pending;
        if (_previous.TryGetValue(descriptor.InterfaceKey, out var prev))
        {
            var seconds = _timeProvider.GetElapsedTime(prev.Timestamp, now).TotalSeconds;
            if (seconds > 0)
            {
                Reading<double> Rate(long cur, long old) => Delta(cur, old) is { } d ? Reading<double>.Of(d / seconds) : Reading<double>.Pending;
                Reading<long> Count(long cur, long old) => Delta(cur, old) is { } d ? Reading<long>.Of(d) : Reading<long>.Pending;
                rxBps = Rate(stats.RxBytes, prev.Stats.RxBytes);
                txBps = Rate(stats.TxBytes, prev.Stats.TxBytes);
                rxPps = Rate(stats.RxPackets, prev.Stats.RxPackets);
                txPps = Rate(stats.TxPackets, prev.Stats.TxPackets);
                rxErr = Count(stats.RxErrors, prev.Stats.RxErrors);
                txErr = Count(stats.TxErrors, prev.Stats.TxErrors);
                rxDisc = Count(stats.RxDiscards, prev.Stats.RxDiscards);
                txDisc = Count(stats.TxDiscards, prev.Stats.TxDiscards);
            }
        }

        _previous[descriptor.InterfaceKey] = new Previous(stats, now);

        var utilization = speed.HasValue && rxBps.HasValue && txBps.HasValue
            ? Reading<double>.Of(Math.Clamp(Math.Max(rxBps.Value!.Value, txBps.Value!.Value) * 8 / speed.Value!.Value * 100, 0, 100))
            : Reading<double>.Unavailable(speed.HasValue ? Availability.Pending : Availability.Unsupported);

        var (quality, rssi) = descriptor.Kind == NetworkInterfaceKind.Wifi
            ? WifiSignal(nic, up)
            : (Reading<double>.Unavailable(Availability.NotPresent), Reading<double>.Unavailable(Availability.NotPresent));

        return new NetworkInterfaceSample(descriptor, rxBps, txBps, rxPps, txPps, rxErr, txErr, rxDisc, txDisc, speed, utilization, up, quality, rssi);
    }

    private (Reading<double> Quality, Reading<double> Rssi) WifiSignal(NetworkInterface nic, bool up)
    {
        if (!up)
        {
            var disconnected = Reading<double>.Unavailable(Availability.NotPresent);
            return (disconnected, disconnected);
        }

        if (_wlan is null || !Guid.TryParse(nic.Id, out var guid))
        {
            var reason = Reading<double>.Unavailable(_wlanError == 0 ? Availability.Unsupported : Availability.Error);
            return (reason, reason);
        }

        var result = _wlan.QueryRssi(guid);
        if (result.Value is { } dbm)
        {
            return (Reading<double>.Of(WlanClient.SignalQualityFromRssi(dbm)), Reading<double>.Of(dbm));
        }

        var unavailable = Reading<double>.Unavailable(result.AccessDenied ? Availability.PermissionDenied : Availability.Error);
        return (unavailable, unavailable);
    }

    private void Refresh()
    {
        _changed = false;
        _lastRefresh = _timeProvider.GetTimestamp();
        var privacy = _privacy.CurrentValue;

        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            LogEnumerationFailed(_logger, ex);
            return;
        }

        var flags = InterfaceFlagsReader.Read();
        var relevant = all.Where(n => IsRelevant(
            n.NetworkInterfaceType,
            n.Description,
            Guid.TryParse(n.Id, out var id) && flags.TryGetValue(id, out var f) ? f : null)).ToList();
        foreach (var nic in relevant.Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            _everUp.Add(nic.Id);
        }

        // Record interfaces that are up now or were up earlier this session (so a disconnect stays visible).
        var chosen = relevant.Where(n => _everUp.Contains(n.Id)).ToList();

        if (_wlan is null && chosen.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
        {
            _wlan = WlanClient.TryOpen(out _wlanError);
        }

        var index = new Dictionary<NetworkInterfaceKind, int>();
        _interfaces = chosen.Select(nic =>
        {
            var kind = Classify(nic.NetworkInterfaceType, nic.Description);
            var n = index[kind] = index.GetValueOrDefault(kind) + 1;
            var name = privacy.CollectNetworkDetails ? nic.Name : $"{kind} {n.ToString(CultureInfo.InvariantCulture)}";
            var description = privacy.CollectNetworkDetails ? nic.Description : string.Empty;
            string? ssid = null;
            if (kind == NetworkInterfaceKind.Wifi && privacy.CaptureWifiNetworkNames && _wlan is not null && Guid.TryParse(nic.Id, out var guid))
            {
                // Opt-in only: this call is location-restricted and may make Windows show a location prompt.
                ssid = _wlan.QueryConnectedSsid(guid, out _);
            }

            return (nic, new NetworkInterfaceDescriptor(nic.Id, name, description, kind, ssid));
        }).ToList();

        ReportChanges();
    }

    private void ReportChanges()
    {
        var state = _interfaces.ToDictionary(i => i.Descriptor.InterfaceKey, i => (i.Nic.OperationalStatus == OperationalStatus.Up, i.Nic.Speed), StringComparer.OrdinalIgnoreCase);
        var now = _timeProvider.GetUtcNow();
        foreach (var (key, (up, speed)) in state)
        {
            var descriptor = _interfaces.First(i => i.Descriptor.InterfaceKey == key).Descriptor;
            if (_lastState.TryGetValue(key, out var before))
            {
                if (before.Up != up)
                {
                    LogLinkChanged(_logger, descriptor.Name, up ? "up" : "down");
                    _events.Publish(NetworkEvents.LinkChanged(now, descriptor, up, speed));
                }
                else if (up && before.Speed != speed)
                {
                    LogSpeedChanged(_logger, descriptor.Name, before.Speed / 1_000_000, speed / 1_000_000);
                    _events.Publish(NetworkEvents.SpeedChanged(now, descriptor, before.Speed, speed));
                }
            }
        }

        _lastState = state;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _changed = true;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Network interface {Name} went {State}")]
    private static partial void LogLinkChanged(ILogger logger, string name, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Network interface {Name} link speed changed from {Before} Mbps to {After} Mbps")]
    private static partial void LogSpeedChanged(ILogger logger, string name, long before, long after);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not enumerate network interfaces")]
    private static partial void LogEnumerationFailed(ILogger logger, Exception exception);

    private sealed record Stats(long RxBytes, long TxBytes, long RxPackets, long TxPackets, long RxErrors, long TxErrors, long RxDiscards, long TxDiscards);

    private sealed record Previous(Stats Stats, long Timestamp);
}

/// <summary>Network link changes as events (the recorder's own observations; not Windows Event Log entries).</summary>
public static class NetworkEvents
{
    /// <summary>Channel of network link events.</summary>
    public const string Channel = "pcdiag.network";

    /// <summary>Provider name of the recorder's own events.</summary>
    public const string Provider = "PcDiagnostic";

    /// <summary>Event ID: link went down.</summary>
    public const int LinkDown = 1;

    /// <summary>Event ID: link came up.</summary>
    public const int LinkUp = 2;

    /// <summary>Event ID: link speed changed.</summary>
    public const int SpeedChange = 3;

    /// <summary>A link went up or down.</summary>
    public static SystemEvent LinkChanged(DateTimeOffset at, NetworkInterfaceDescriptor nic, bool up, long speedBitsPerSecond)
    {
        ArgumentNullException.ThrowIfNull(nic);
        var state = up ? "up" : "down";
        var data = new Dictionary<string, string> { ["kind"] = nic.Kind.ToString(), ["state"] = state };
        if (up && speedBitsPerSecond > 0)
        {
            data["speedMbps"] = (speedBitsPerSecond / 1_000_000).ToString(CultureInfo.InvariantCulture);
        }

        return new SystemEvent(
            at,
            Channel,
            null,
            Provider,
            up ? LinkUp : LinkDown,
            up ? EventLevel.Information : EventLevel.Warning,
            EventCategory.Network,
            up ? Severity.Low : Severity.Medium,
            $"Network link {state}: {nic.Name}",
            $"{Describe(nic)} went {state}.",
            RelatedDevice: nic.Name,
            RuleId: up ? "pcdiag.network.link-up" : "pcdiag.network.link-down",
            Data: data);
    }

    /// <summary>The negotiated link speed changed while the link stayed up.</summary>
    public static SystemEvent SpeedChanged(DateTimeOffset at, NetworkInterfaceDescriptor nic, long beforeBitsPerSecond, long afterBitsPerSecond)
    {
        ArgumentNullException.ThrowIfNull(nic);
        var before = (beforeBitsPerSecond / 1_000_000).ToString(CultureInfo.InvariantCulture);
        var after = (afterBitsPerSecond / 1_000_000).ToString(CultureInfo.InvariantCulture);
        return new SystemEvent(
            at,
            Channel,
            null,
            Provider,
            SpeedChange,
            EventLevel.Information,
            EventCategory.Network,
            Severity.Low,
            $"Network link speed changed: {nic.Name} {before} to {after} Mbps",
            $"{Describe(nic)} changed its link speed from {before} Mbps to {after} Mbps.",
            RelatedDevice: nic.Name,
            RuleId: "pcdiag.network.link-speed",
            Data: new Dictionary<string, string> { ["kind"] = nic.Kind.ToString(), ["beforeMbps"] = before, ["afterMbps"] = after });
    }

    private static string Describe(NetworkInterfaceDescriptor nic) =>
        string.IsNullOrEmpty(nic.Description) ? $"{nic.Name} ({nic.Kind})" : $"{nic.Name} ({nic.Description})";
}
