using Microsoft.Extensions.Logging;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;

namespace PcDiag.Collectors.Sensors;

/// <summary>
/// Merges every <see cref="IHardwareSensorProvider"/> into one <see cref="SensorSample"/> per sensor tick.
/// Each provider is read on its own interval (slow providers keep their last snapshot in between). When several
/// providers report the same logical key, the highest-priority value wins; if none has a value, the most
/// informative reason is kept (DriverMissing / RequiresElevation over Unsupported).
/// </summary>
public sealed partial class SensorCollector : ISensorCollector
{
    private readonly List<IHardwareSensorProvider> _providers;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SensorCollector> _logger;
    private readonly Dictionary<string, (long ReadAt, HardwareSnapshot Snapshot)> _last = [];
    private readonly HashSet<string> _failed = [];

    /// <summary>Creates the collector.</summary>
    public SensorCollector(IEnumerable<IHardwareSensorProvider> providers, TimeProvider timeProvider, ILogger<SensorCollector> logger)
    {
        _providers = providers.OrderByDescending(p => p.Priority).ToList();
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "sensors";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.Sensors;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Hardware sensors", RequiredPrivilege.PartialWithoutElevation, CostClass.Moderate);

    /// <inheritdoc />
    public async ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<string>();
        foreach (var provider in _providers)
        {
            try
            {
                var status = await provider.InitializeAsync(cancellationToken).ConfigureAwait(false);
                statuses.Add($"{provider.ProviderId}: {status}");
                LogProviderReady(_logger, provider.ProviderId, status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _failed.Add(provider.ProviderId);
                LogProviderFailed(_logger, ex, provider.ProviderId);
            }
        }

        return new CollectorStatus(_providers.Count > _failed.Count ? Availability.Available : Availability.Error, string.Join("; ", statuses));
    }

    /// <inheritdoc />
    public async ValueTask<SensorSample> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = _timeProvider.GetTimestamp();
        var snapshots = new List<(int Priority, HardwareSnapshot Snapshot)>();

        foreach (var provider in _providers.Where(p => !_failed.Contains(p.ProviderId)))
        {
            if (!_last.TryGetValue(provider.ProviderId, out var last) || _timeProvider.GetElapsedTime(last.ReadAt, now) >= provider.RefreshInterval)
            {
                try
                {
                    last = (now, await provider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false));
                    _last[provider.ProviderId] = last;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogProviderFailed(_logger, ex, provider.ProviderId);
                    if (!_last.ContainsKey(provider.ProviderId))
                    {
                        continue;
                    }
                }
            }

            snapshots.Add((provider.Priority, last.Snapshot));
        }

        return new SensorSample(context.Timestamp, Merge(snapshots));
    }

    /// <summary>Merges readings by logical key (highest priority with a value wins).</summary>
    internal static List<SensorReading> Merge(IEnumerable<(int Priority, HardwareSnapshot Snapshot)> snapshots)
    {
        var merged = new Dictionary<string, (int Priority, SensorReading Reading)>(StringComparer.Ordinal);
        foreach (var (priority, snapshot) in snapshots.OrderByDescending(s => s.Priority))
        {
            foreach (var reading in snapshot.Readings)
            {
                var key = reading.Sensor.LogicalKey;
                if (!merged.TryGetValue(key, out var existing))
                {
                    merged[key] = (priority, reading);
                }
                else if (!existing.Reading.Value.HasValue && (reading.Value.HasValue || Rank(reading.Value.Availability) > Rank(existing.Reading.Value.Availability)))
                {
                    merged[key] = (priority, reading);
                }
            }
        }

        return merged.Values.Select(v => v.Reading).ToList();
    }

    private static int Rank(Availability availability) => availability switch
    {
        Availability.DriverMissing => 5,
        Availability.RequiresElevation => 4,
        Availability.PermissionDenied => 3,
        Availability.Error => 2,
        Availability.Pending => 1,
        _ => 0,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Sensor provider {Provider}: {Status}")]
    private static partial void LogProviderReady(ILogger logger, string provider, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sensor provider {Provider} failed")]
    private static partial void LogProviderFailed(ILogger logger, Exception exception, string provider);
}
