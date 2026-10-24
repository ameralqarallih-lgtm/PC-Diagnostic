using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Diagnostics;

namespace PcDiag.Agent;

/// <summary>
/// Writes what the recorder cost in CPU to its log every minute: its share of the whole PC, and how that splits over
/// its parts (each collector, the sensors, the panels, motion, the USB screens, the characters; "other" is the rest:
/// storage, the pipe, the runtime). The parts are counted in CPU cycles (<see cref="CostMeter"/>) and turned into
/// milliseconds with the process's own cycles-to-time ratio over the same minute.
/// </summary>
internal sealed partial class CostReportService(ILogger<CostReportService> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var cycles = CostMeter.ProcessCycles();
        var clock = Stopwatch.StartNew();
        _ = CostMeter.Take();
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            process.Refresh();
            var cpuNow = process.TotalProcessorTime;
            var cyclesNow = CostMeter.ProcessCycles();
            var parts = CostMeter.Take();
            var used = (cpuNow - cpu).TotalMilliseconds;
            var spent = cyclesNow - cycles;
            var seconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            (cpu, cycles) = (cpuNow, cyclesNow);
            if (spent <= 0 || seconds <= 0)
            {
                continue;
            }

            var msPerCycle = used / spent;
            var share = used / (seconds * 1000 * Environment.ProcessorCount) * 100;
            var counted = parts.Values.Sum() * msPerCycle;
            var split = string.Join(", ", parts.OrderByDescending(p => p.Value).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key} {p.Value * msPerCycle:0} ms")));
            LogCost(logger, used, seconds, share, split, Math.Max(0, used - counted));
            CostMeter.Latest = new RecorderCost(
                DateTimeOffset.UtcNow,
                seconds,
                used,
                share,
                [.. parts.OrderByDescending(p => p.Value).Select(p => new RecorderCostPart(p.Key, Math.Round(p.Value * msPerCycle, 1))), new RecorderCostPart("other", Math.Round(Math.Max(0, used - counted), 1))]);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorder CPU: {Used:0} ms in {Seconds:0} s ({Share:0.00} % of the PC): {Split}, other {Other:0} ms")]
    private static partial void LogCost(ILogger logger, double used, double seconds, double share, string split, double other);
}
