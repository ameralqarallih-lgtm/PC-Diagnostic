using System.Globalization;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Devices;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Platform.Windows.Inventory;

namespace PcDiag.Agent.Devices;

/// <summary>
/// Checks the hardware against the setup the user expects (expected-hardware.json): every
/// <see cref="Interval"/>, and at once when asked (the setup was just remembered, or the user checks). Expected
/// devices, drives, monitors, network adapters and graphics cards that are not there, and memory or processor
/// threads clearly below what was there, are missing, each since the check that first found it so. When the user
/// chose so, hardware going missing starts an incident (the evidence of what happened around it).
/// </summary>
internal sealed partial class HardwareWatch(
    DataPaths paths,
    IDisplayCollector displays,
    IIncidentCapture capture,
    TimeProvider time,
    ILogger<HardwareWatch> logger) : BackgroundService, IHardwareWatch
{
    /// <summary>How often the hardware is checked.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    /// <summary>Delay before the first check, so startup stays light.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile HardwareStatus? _status;

    /// <inheritdoc />
    public HardwareStatus? Status => _status;

    /// <inheritdoc />
    public async Task<HardwareStatus> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            var current = await Task.Run(HardwareInventory.Read, cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await displays.SnapshotAsync(cancellationToken).ConfigureAwait(false);
                foreach (var monitor in snapshot?.Displays ?? [])
                {
                    current.Add(new HardwareItem("monitor:" + monitor.TargetKey, "monitor", string.IsNullOrWhiteSpace(monitor.FriendlyName) ? "Monitor" : monitor.FriendlyName,
                        string.Create(CultureInfo.InvariantCulture, $"{monitor.WidthPixels} × {monitor.HeightPixels}")));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDisplaysFailed(logger, ex);
            }

            var expected = HardwareCheck.Read(paths.ExpectedHardwareFile);
            var before = _status;
            var (missing, added) = HardwareCheck.Compare(expected, current, before?.Missing ?? [], now);
            var status = new HardwareStatus(now, expected, current.GroupBy(i => i.Key, StringComparer.Ordinal).Select(g => g.First()).ToList(), missing, added);
            _status = status;

            // Newly missing (not after the setup was just remembered with it missing, nor on the first check).
            var known = (before?.Missing ?? []).Select(m => m.Item.Key).ToHashSet(StringComparer.Ordinal);
            var gone = before is null ? [] : missing.Where(m => !known.Contains(m.Item.Key)).ToList();
            if (gone.Count > 0)
            {
                LogMissing(logger, HardwareCheck.Describe(gone));
                if (expected?.IncidentOnMissing == true)
                {
                    await CaptureAsync(gone, now, cancellationToken).ConfigureAwait(false);
                }
            }

            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    LogCheckFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private async Task CaptureAsync(List<MissingHardware> gone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var detail = "Expected hardware missing: " + HardwareCheck.Describe(gone);
            await capture.CaptureAsync(new IncidentTrigger(now, IncidentSource.Event, RuleId: "expected-hardware", Detail: detail, Note: detail, Severity: Severity.High), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // The license or the drive does not allow an incident now; the notice still shows.
            LogCaptureFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Expected hardware missing: {Missing}")]
    private static partial void LogMissing(ILogger logger, string missing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The hardware check failed; it is tried again")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The monitors could not be listed for the hardware check")]
    private static partial void LogDisplaysFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No incident was started for the missing hardware")]
    private static partial void LogCaptureFailed(ILogger logger, Exception exception);
}
