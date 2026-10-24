using System.Globalization;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Kernel;
using PcDiag.Platform.Windows.Pdh;

namespace PcDiag.Collectors.Cpu;

/// <summary>
/// System-wide and per-logical-processor CPU metrics from the "Processor Information" PDH object
/// (Task Manager-consistent "% Processor Utility", effective frequency, performance limits).
/// Package temperature and power come from hardware sensor providers (Phase 6) and are unavailable here.
/// </summary>
public sealed partial class CpuCollector(ILogger<CpuCollector> logger) : ICpuCollector, IDisposable
{
    private const string Total = @"\Processor Information(_Total)\";

    private PdhQuery? _query;
    private PdhCounter? _utility;
    private PdhCounter? _user;
    private PdhCounter? _privileged;
    private PdhCounter? _frequency;
    private PdhCounter? _performance;
    private PdhCounter? _performanceLimit;
    private PdhCounter? _performanceLimitFlags;
    private PdhCounter? _interrupts;
    private PdhCounter? _queueLength;
    private PdhCounter? _contextSwitches;
    private PdhCounter? _coreUtility;
    private PdhCounter? _corePerformance;
    private PdhCounter? _coreFrequency;
    private Reading<double> _maxMhz = Reading<double>.Pending;
    private bool _utilityIsProcessorTime;

    /// <inheritdoc />
    public string Id => "cpu";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.System;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "CPU", RequiredPrivilege.StandardUser, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            _query = PdhQuery.Open();
        }
        catch (PdhException ex)
        {
            LogPdhUnavailable(logger, ex);
            return ValueTask.FromResult(new CollectorStatus(Availability.Error, ex.Message));
        }

        _utility = Add(Total + "% Processor Utility");
        if (_utility is null)
        {
            // Older systems without the utility counter: fall back to time-based utilization.
            _utility = Add(Total + "% Processor Time");
            _utilityIsProcessorTime = _utility is not null;
        }

        _user = Add(Total + "% User Time");
        _privileged = Add(Total + "% Privileged Time");
        _frequency = Add(Total + "Processor Frequency");
        _performance = Add(Total + "% Processor Performance");
        _performanceLimit = Add(Total + "% Performance Limit");
        _performanceLimitFlags = Add(Total + "Performance Limit Flags");
        _interrupts = Add(Total + "Interrupts/sec");
        _queueLength = Add(@"\System\Processor Queue Length");
        _contextSwitches = Add(@"\System\Context Switches/sec");
        _coreUtility = Add(_utilityIsProcessorTime ? @"\Processor Information(*)\% Processor Time" : @"\Processor Information(*)\% Processor Utility");
        _corePerformance = Add(@"\Processor Information(*)\% Processor Performance");
        _coreFrequency = Add(@"\Processor Information(*)\Processor Frequency");

        _maxMhz = SystemInfo.TryGetMaxProcessorMhz() is { } max ? Reading<double>.Of(max) : Reading<double>.Unavailable(Availability.Unsupported);

        // Prime rate counters so the first real tick has two samples to compute from.
        _query.Collect();

        // Missing individual counters are reported per metric as Unsupported; the collector itself stays available.
        const int expectedCounters = 13;
        var detail = _query.Counters.Count < expectedCounters
            ? $"{expectedCounters - _query.Counters.Count} of {expectedCounters} counters unavailable on this system"
            : $"{Environment.ProcessorCount} logical processors";
        return ValueTask.FromResult(new CollectorStatus(_utility is null ? Availability.Unsupported : Availability.Available, detail));
    }

    /// <inheritdoc />
    public ValueTask<CpuSample> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_query is null || _query.Collect() != 0)
        {
            return ValueTask.FromResult(Unavailable(context.Timestamp, _query is null ? Availability.Unsupported : Availability.Error));
        }

        var frequency = Read(_frequency);
        var performance = Read(_performance, noCap100: true);
        var effectiveMhz = frequency.HasValue && performance.HasValue
            ? Reading<double>.Of(frequency.Value!.Value * performance.Value!.Value / 100.0)
            : frequency;

        var sample = new CpuSample(
            context.Timestamp,
            TotalPercent: ClampPercent(Read(_utility, noCap100: true)),
            UserPercent: ClampPercent(Read(_user)),
            KernelPercent: ClampPercent(Read(_privileged)),
            FrequencyMhz: effectiveMhz,
            MaxFrequencyMhz: _maxMhz,
            PerformancePercent: performance,
            PerformanceLimitPercent: Read(_performanceLimit),
            PerformanceLimitFlags: Read(_performanceLimitFlags).Map(v => (long)v),
            ProcessorQueueLength: Read(_queueLength),
            ContextSwitchesPerSec: Read(_contextSwitches),
            InterruptsPerSec: Read(_interrupts),
            PackageTemperatureC: Reading<double>.Unavailable(Availability.Unsupported),
            PackagePowerW: Reading<double>.Unavailable(Availability.Unsupported),
            Cores: ReadCores());

        return ValueTask.FromResult(sample);
    }

    /// <inheritdoc />
    public void Dispose() => _query?.Dispose();

    /// <summary>
    /// Parses a "Processor Information" instance name ("group,number"). Returns false for totals ("_Total", "0,_Total").
    /// </summary>
    internal static bool TryParseInstance(string instance, out int group, out int number)
    {
        group = 0;
        number = 0;
        var comma = instance.IndexOf(',', StringComparison.Ordinal);
        return comma > 0
               && int.TryParse(instance.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out group)
               && int.TryParse(instance.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private List<CpuCoreSample> ReadCores()
    {
        if (_coreUtility is null)
        {
            return [];
        }

        var performance = ToLookup(_corePerformance?.GetArray(noCap100: true));
        var frequency = ToLookup(_coreFrequency?.GetArray());

        var cores = new List<(int Group, int Number, string Instance, Reading<double> Utility)>();
        foreach (var item in _coreUtility.GetArray(noCap100: true))
        {
            if (TryParseInstance(item.Instance, out var group, out var number))
            {
                cores.Add((group, number, item.Instance, item.Value));
            }
        }

        cores.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Number.CompareTo(b.Number));

        var result = new List<CpuCoreSample>(cores.Count);
        for (var i = 0; i < cores.Count; i++)
        {
            var (_, _, instance, utility) = cores[i];
            var mhz = frequency.TryGetValue(instance, out var f) && performance.TryGetValue(instance, out var p) && f.HasValue && p.HasValue
                ? Reading<double>.Of(f.Value!.Value * p.Value!.Value / 100.0)
                : Reading<double>.Unavailable(Availability.Unsupported);
            result.Add(new CpuCoreSample(i, ClampPercent(utility), mhz));
        }

        return result;
    }

    private static Dictionary<string, Reading<double>> ToLookup(IReadOnlyList<PdhInstanceValue>? values) =>
        values?.ToDictionary(v => v.Instance, v => v.Value, StringComparer.Ordinal) ?? [];

    // Utility can exceed 100 % when cores run above base frequency; utilization is reported like Task Manager (capped).
    private static Reading<double> ClampPercent(Reading<double> reading) => reading.Map(v => Math.Clamp(v, 0, 100));

    private static Reading<double> Read(PdhCounter? counter, bool noCap100 = false) =>
        counter?.GetDouble(noCap100) ?? Reading<double>.Unavailable(Availability.Unsupported);

    private PdhCounter? Add(string path)
    {
        var counter = _query!.TryAddCounter(path, out var status);
        if (counter is null)
        {
            LogCounterMissing(logger, path, status);
        }

        return counter;
    }

    private static CpuSample Unavailable(DateTimeOffset timestamp, Availability reason)
    {
        var none = Reading<double>.Unavailable(reason);
        return new CpuSample(timestamp, none, none, none, none, none, none, none, Reading<long>.Unavailable(reason), none, none, none, none, none, []);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PDH counter {Path} is not available on this system (status 0x{Status:X8}); the metric will be reported as unavailable")]
    private static partial void LogCounterMissing(ILogger logger, string path, uint status);

    [LoggerMessage(Level = LogLevel.Error, Message = "Performance counters (PDH) are unavailable; CPU metrics cannot be collected")]
    private static partial void LogPdhUnavailable(ILogger logger, Exception exception);
}
