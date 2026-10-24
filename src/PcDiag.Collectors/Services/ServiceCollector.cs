using Microsoft.Extensions.Logging;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Events;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Services;

namespace PcDiag.Collectors.Services;

/// <summary>
/// Windows services on the inventory tier (default 10 s). Each poll is one EnumServicesStatusEx call (a few ms), and
/// changes are found by comparing polls. Start types are re-read every 5 minutes. A full snapshot is emitted on the
/// first poll and every hour. Precise start/stop/failure timestamps come from Service Control Manager events
/// (7036, 7040, 7031, 7034, 7045...) in the event engine (Phase 7).
/// </summary>
public sealed partial class ServiceCollector : IServiceCollector
{
    /// <summary>How often start types are re-read (QueryServiceConfig per service).</summary>
    public static readonly TimeSpan StartTypeRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>How often a full snapshot is emitted for storage.</summary>
    public static readonly TimeSpan SnapshotInterval = TimeSpan.FromHours(1);

    /// <summary>A stop followed by a start within this window is reported as a restart.</summary>
    public static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(60);

    private const int StateStopped = 1;
    private const int StateRunning = 4;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ServiceCollector> _logger;
    private Dictionary<string, ServiceInfo> _previous = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _stoppedAt = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, ServiceStartConfig> _startConfigs = new Dictionary<string, ServiceStartConfig>();
    private long _lastStartTypeRefresh;
    private long _lastSnapshot;
    private bool _first = true;

    /// <summary>Creates the collector.</summary>
    public ServiceCollector(TimeProvider timeProvider, ILogger<ServiceCollector> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "services";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.Inventory;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Windows services", RequiredPrivilege.StandardUser, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        var services = ServiceControlManager.Enumerate();
        return ValueTask.FromResult(services.Count == 0
            ? new CollectorStatus(Availability.Error, "EnumServicesStatusEx returned no services")
            : new CollectorStatus(Availability.Available, $"{services.Count} services, {services.Count(s => s.State == StateRunning)} running"));
    }

    /// <inheritdoc />
    public ValueTask<ServiceSample> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var records = ServiceControlManager.Enumerate();
        if (records.Count == 0)
        {
            return ValueTask.FromResult(new ServiceSample(context.Timestamp, null, []));
        }

        var now = _timeProvider.GetTimestamp();
        var previousStartConfigs = _startConfigs;
        var refreshStartTypes = _first || _timeProvider.GetElapsedTime(_lastStartTypeRefresh, now) >= StartTypeRefreshInterval;
        if (refreshStartTypes)
        {
            _startConfigs = ServiceControlManager.QueryStartConfigs(records.Select(r => r.Name));
            _lastStartTypeRefresh = now;
        }
        else if (records.Any(r => !_startConfigs.ContainsKey(r.Name)))
        {
            // New services: read just their configuration.
            var added = ServiceControlManager.QueryStartConfigs(records.Where(r => !_startConfigs.ContainsKey(r.Name)).Select(r => r.Name));
            _startConfigs = _startConfigs.Concat(added).GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
        }

        var current = records.ToDictionary(r => r.Name, ToInfo, StringComparer.OrdinalIgnoreCase);
        var changes = _first ? [] : Diff(_previous, current, previousStartConfigs, refreshStartTypes, context.Timestamp);

        var emitSnapshot = _first || _timeProvider.GetElapsedTime(_lastSnapshot, now) >= SnapshotInterval;
        if (emitSnapshot)
        {
            _lastSnapshot = now;
        }

        foreach (var change in changes)
        {
            LogChange(_logger, change.Name, change.Kind, change.OldValue ?? string.Empty, change.NewValue ?? string.Empty);
        }

        _previous = current;
        _first = false;
        return ValueTask.FromResult(new ServiceSample(context.Timestamp, emitSnapshot ? current.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() : null, changes));
    }

    /// <summary>A full, current snapshot (used by incident capture). Start types come from the last refresh.</summary>
    public Task<ServiceSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var records = ServiceControlManager.Enumerate();
        if (records.Any(r => !_startConfigs.ContainsKey(r.Name)))
        {
            _startConfigs = ServiceControlManager.QueryStartConfigs(records.Select(r => r.Name));
        }

        return Task.FromResult(new ServiceSnapshot(_timeProvider.GetUtcNow(), records.Select(ToInfo).ToList()));
    }

    /// <summary>Maps SCM start type and delayed flag to <see cref="ServiceStartType"/>.</summary>
    internal static ServiceStartType MapStartType(ServiceStartConfig? config) => config switch
    {
        { StartType: 2, DelayedAutoStart: true } => ServiceStartType.AutomaticDelayed,
        { StartType: var t } when t is >= 0 and <= 4 => (ServiceStartType)t,
        _ => ServiceStartType.Manual,
    };

    /// <summary>Compares two polls.</summary>
    internal List<ServiceChange> Diff(
        IReadOnlyDictionary<string, ServiceInfo> before,
        IReadOnlyDictionary<string, ServiceInfo> after,
        IReadOnlyDictionary<string, ServiceStartConfig> startConfigsBefore,
        bool startTypesRefreshed,
        DateTimeOffset at)
    {
        var changes = new List<ServiceChange>();
        foreach (var (name, now) in after)
        {
            if (!before.TryGetValue(name, out var old))
            {
                changes.Add(new ServiceChange(at, name, now.DisplayName, ServiceEventKind.Installed, null, now.Status.ToString(), now.ProcessId));
                continue;
            }

            if (old.Status != now.Status)
            {
                if (now.Status == ServiceStatus.Stopped)
                {
                    _stoppedAt[name] = at;
                    changes.Add(new ServiceChange(at, name, now.DisplayName, ServiceEventKind.Stopped, old.Status.ToString(), now.Status.ToString()));
                }
                else if (now.Status == ServiceStatus.Running)
                {
                    var restarted = _stoppedAt.Remove(name, out var stoppedAt) && at - stoppedAt <= RestartWindow;
                    changes.Add(new ServiceChange(at, name, now.DisplayName, restarted ? ServiceEventKind.Restarted : ServiceEventKind.Started, old.Status.ToString(), now.Status.ToString(),
                        now.ProcessId));
                }

                // Pending states (start/stop pending) are transient; the settled state is reported on a later poll.
            }
            else if (now.Status == ServiceStatus.Running && old.ProcessId is { } previousPid && now.ProcessId is { } pid && previousPid != pid)
            {
                // Stopped and started again between two polls: running both times, but in another process.
                changes.Add(new ServiceChange(at, name, now.DisplayName, ServiceEventKind.Restarted, old.Status.ToString(), now.Status.ToString(), pid));
            }

            if (startTypesRefreshed && startConfigsBefore.ContainsKey(name) && old.StartType != now.StartType)
            {
                changes.Add(new ServiceChange(at, name, now.DisplayName, ServiceEventKind.StartTypeChanged, old.StartType.ToString(), now.StartType.ToString()));
            }
        }

        foreach (var (name, old) in before)
        {
            if (!after.ContainsKey(name))
            {
                changes.Add(new ServiceChange(at, name, old.DisplayName, ServiceEventKind.Removed, old.Status.ToString(), null));
            }
        }

        return changes;
    }

    private ServiceInfo ToInfo(ServiceStatusRecord record) => new(
        record.Name,
        record.DisplayName,
        record.State is >= StateStopped and <= 7 ? (ServiceStatus)record.State : ServiceStatus.Unknown,
        MapStartType(_startConfigs.TryGetValue(record.Name, out var config) ? config : null),
        record.ProcessId > 0 ? record.ProcessId : null,
        record.ServiceType,
        null);

    [LoggerMessage(Level = LogLevel.Information, Message = "Service {Name}: {Kind} ({Old} -> {New})")]
    private static partial void LogChange(ILogger logger, string name, ServiceEventKind kind, string old, string @new);
}
