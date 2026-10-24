using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Processes;

namespace PcDiag.Collectors.Processes;

/// <summary>
/// Every process, every process tick (default 2 s), from one NtQuerySystemInformation call: CPU %, CPU time,
/// memory, I/O rates, threads, handles, priority. It detects lifecycle changes by comparing snapshots
/// (started/stopped; processes shorter than one tick can be missed without ETW) and hangs with IsHungAppWindow.
/// Crashes come from Windows Error Reporting events in the event engine (Phase 7).
/// </summary>
public sealed partial class ProcessCollector : IProcessCollector
{
    /// <summary>Most processes enriched (path, user, command line) per tick; the rest follow on later ticks.</summary>
    public const int MaxEnrichmentsPerTick = 64;

    private readonly IOptionsMonitor<PrivacyOptions> _privacy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProcessCollector> _logger;
    private readonly IProcessIoMonitor? _io;
    private readonly ProcessSignatures? _signatures;
    private readonly Dictionary<string, List<ProcessIdentityDetails>> _awaitingSignature = new(StringComparer.OrdinalIgnoreCase);
    private int _signatureTicks;
    private readonly NtProcesses _nt = new();
    private readonly int _processorCount = Environment.ProcessorCount;
    private readonly Dictionary<ProcessKey, Previous> _previous = [];
    private readonly Dictionary<ProcessKey, string> _names = [];
    private readonly Queue<ProcessKey> _toEnrich = new();
    private readonly Queue<ProcessKey> _toEnrichFirst = new(); // started after the baseline: most relevant to incidents
    private readonly HashSet<ProcessKey> _enriched = [];
    private HashSet<int> _hung = [];
    private long _lastTimestamp;
    private bool _baselineTaken;
    private bool _ioWasRunning;

    /// <summary>Creates the collector.</summary>
    public ProcessCollector(IOptionsMonitor<PrivacyOptions> privacy, TimeProvider timeProvider, ILogger<ProcessCollector> logger)
        : this(privacy, [], timeProvider, logger)
    {
    }

    /// <summary>Creates the collector with Enhanced mode's per-process I/O monitor (used while it runs).</summary>
    public ProcessCollector(IOptionsMonitor<PrivacyOptions> privacy, IEnumerable<IProcessIoMonitor> io, TimeProvider timeProvider, ILogger<ProcessCollector> logger)
        : this(privacy, io, null, timeProvider, logger)
    {
    }

    /// <summary>
    /// Creates the collector with the per-process I/O monitor and the signature checker: each program file's
    /// signature is checked in the background and its process identity is sent again with the result.
    /// </summary>
    public ProcessCollector(IOptionsMonitor<PrivacyOptions> privacy, IEnumerable<IProcessIoMonitor> io, ProcessSignatures? signatures, TimeProvider timeProvider,
        ILogger<ProcessCollector> logger)
    {
        ArgumentNullException.ThrowIfNull(io);
        _privacy = privacy;
        _io = io.FirstOrDefault();
        _signatures = signatures;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "processes";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.Process;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Processes", RequiredPrivilege.PartialWithoutElevation, CostClass.Moderate);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        var ok = _nt.Visit(ref count, static (ref int c, in RawProcess _) => c++);
        if (!ok)
        {
            LogSnapshotFailed(_logger);
        }

        return ValueTask.FromResult(ok
            ? new CollectorStatus(Availability.Available, $"{count} processes")
            : new CollectorStatus(Availability.Error, "NtQuerySystemInformation(SystemProcessInformation) failed"));
    }

    /// <inheritdoc />
    public ValueTask<ProcessSample> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = _timeProvider.GetTimestamp();
        var elapsed = _lastTimestamp == 0 ? TimeSpan.Zero : _timeProvider.GetElapsedTime(_lastTimestamp, now);
        _lastTimestamp = now;

        var raw = new List<Current>(400);
        var state = (List: raw, Names: _names);
        if (!_nt.Visit(ref state, static (ref (List<Current> List, Dictionary<ProcessKey, string> Names) s, in RawProcess p) =>
        {
            var key = new ProcessKey(p.Pid, p.CreateTime > 0 ? DateTime.FromFileTimeUtc(p.CreateTime).Ticks : 0);
            if (!s.Names.TryGetValue(key, out var name))
            {
                name = p.Pid == 0 ? "System Idle Process" : p.Name.ToString();
                s.Names[key] = name;
            }

            s.List.Add(new Current(key, name, p.ParentPid, p.CreateTime, p.SessionId, p.UserTime + p.KernelTime, p.WorkingSet, p.PrivateWorkingSet,
                p.Commit, p.ReadTransfer, p.WriteTransfer, p.Threads, p.Handles, p.BasePriority));
        }))
        {
            return ValueTask.FromResult(new ProcessSample(context.Timestamp, [], [], []));
        }

        var hung = SafeFindHung();
        var trueIo = TakeTrueIo(elapsed);
        var events = new List<ProcessLifecycleEvent>();
        var identities = new List<ProcessIdentityDetails>();
        var metrics = new List<ProcessMetrics>(raw.Count);
        var currentKeys = new HashSet<ProcessKey>();

        foreach (var p in raw)
        {
            currentKeys.Add(p.Key);
            var isNew = !_previous.TryGetValue(p.Key, out var prev);
            if (isNew)
            {
                identities.Add(Identity(p, enrich: false));
                (_baselineTaken ? _toEnrichFirst : _toEnrich).Enqueue(p.Key);
                if (_baselineTaken)
                {
                    events.Add(new ProcessLifecycleEvent(context.Timestamp, p.Key, p.Name, ProcessEventKind.Started));
                }
            }

            var seconds = elapsed.TotalSeconds;
            Reading<double> Rate(long current, long before) =>
                isNew || seconds <= 0 ? Reading<double>.Pending : Reading<double>.Of(Math.Max(0, current - before) / seconds);

            var cpu = isNew || elapsed <= TimeSpan.Zero
                ? Reading<double>.Pending
                : Reading<double>.Of(Math.Clamp(Math.Max(0, p.CpuTime - prev.CpuTime) / (double)elapsed.Ticks / _processorCount * 100, 0, 100));

            var state2 = hung.Contains(p.Key.Pid) ? ProcessState.NotResponding : ProcessState.Running;
            if (_baselineTaken && hung.Contains(p.Key.Pid) != _hung.Contains(p.Key.Pid) && !isNew)
            {
                events.Add(new ProcessLifecycleEvent(context.Timestamp, p.Key, p.Name, state2 == ProcessState.NotResponding ? ProcessEventKind.Hung : ProcessEventKind.Recovered));
            }

            var current = new ProcessMetrics(
                p.Key,
                p.Name,
                cpu,
                Reading<long>.Of(p.CpuTime / TimeSpan.TicksPerMillisecond),
                Reading<long>.Of(p.WorkingSet),
                Reading<long>.Of(p.PrivateWorkingSet),
                Reading<long>.Of(p.Commit),
                Rate(p.ReadTransfer, prev.ReadTransfer),
                Rate(p.WriteTransfer, prev.WriteTransfer),
                Reading<int>.Of(p.Threads),
                Reading<int>.Of(p.Handles),
                Reading<int>.Of(p.BasePriority),
                state2,
                trueIo is null ? null : trueIo.TryGetValue(p.Key.Pid, out var io) ? io : ProcessTrueIo.None);

            // An idle process usually reads exactly as before: hand back the previous object, so the ring buffer's
            // frames share it rather than each holding its own copy (about 270 bytes per process every tick).
            if (current.Equals(prev.Metrics))
            {
                current = prev.Metrics;
            }

            metrics.Add(current);
            _previous[p.Key] = new Previous(p.CpuTime, p.ReadTransfer, p.WriteTransfer, current);
        }

        foreach (var gone in _previous.Keys.Where(k => !currentKeys.Contains(k)).ToList())
        {
            events.Add(new ProcessLifecycleEvent(context.Timestamp, gone, _names.GetValueOrDefault(gone, "?"), ProcessEventKind.Stopped));
            _previous.Remove(gone);
            _names.Remove(gone);
            _enriched.Remove(gone);
        }

        identities.AddRange(Enrich(raw.ToDictionary(r => r.Key)));
        identities.AddRange(SignaturesChecked(currentKeys));
        _hung = hung;
        _baselineTaken = true;
        return ValueTask.FromResult(new ProcessSample(context.Timestamp, metrics, identities, events));
    }

    /// <summary>
    /// Per-process rates from the ETW monitor since the previous tick, or null while it is not running. The first
    /// read after it starts is discarded, since it covers less than a whole tick.
    /// </summary>
    private Dictionary<int, ProcessTrueIo>? TakeTrueIo(TimeSpan elapsed)
    {
        var running = _io is { Running: true };
        var first = running && !_ioWasRunning;
        _ioWasRunning = running;
        if (!running || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var totals = _io!.Take();
        if (first)
        {
            return null;
        }

        var seconds = elapsed.TotalSeconds;
        return totals.ToDictionary(
            t => t.Key,
            t => new ProcessTrueIo(t.Value.DiskReadBytes / seconds, t.Value.DiskWriteBytes / seconds, t.Value.NetReceiveBytes / seconds, t.Value.NetSendBytes / seconds));
    }

    private IEnumerable<ProcessIdentityDetails> Enrich(Dictionary<ProcessKey, Current> current)
    {
        var done = 0;
        while (done < MaxEnrichmentsPerTick && (_toEnrichFirst.TryDequeue(out var key) || _toEnrich.TryDequeue(out key)))
        {
            if (!current.TryGetValue(key, out var p) || !_enriched.Add(key))
            {
                continue; // exited before we got to it, or already enriched
            }

            done++;
            yield return Identity(p, enrich: true);
        }
    }

    private ProcessIdentityDetails Identity(Current p, bool enrich)
    {
        string? path = null, commandLine = null, user = null;
        if (enrich)
        {
            var privacy = _privacy.CurrentValue;
            path = ProcessDetails.TryGetImagePath(p.Key.Pid);
            if (privacy.CaptureCommandLines && ProcessDetails.TryGetCommandLine(p.Key.Pid) is { } line)
            {
                commandLine = CommandLineRedactor.Redact(line);
            }

            if (privacy.CaptureUserNames)
            {
                user = ProcessDetails.TryGetUser(p.Key.Pid);
            }
        }

        var started = p.CreateTime > 0 ? new DateTimeOffset(DateTime.FromFileTimeUtc(p.CreateTime)) : DateTimeOffset.UnixEpoch;
        var identity = new ProcessIdentityDetails(p.Key, p.ParentPid, p.Name, started, p.SessionId, path, commandLine, user);
        if (path is null || _signatures is null)
        {
            return identity;
        }

        if (_signatures.TryGet(path, out var signature))
        {
            return identity with { Signer = signature.Signer, Signature = signature.Status };
        }

        _signatures.Request(path);
        if (!_awaitingSignature.TryGetValue(path, out var waiting))
        {
            _awaitingSignature[path] = waiting = [];
        }

        waiting.Add(identity);
        return identity;
    }

    /// <summary>Identities sent again with their signature, for checks that finished since the previous tick.</summary>
    private IEnumerable<ProcessIdentityDetails> SignaturesChecked(HashSet<ProcessKey> running)
    {
        if (_signatures is null)
        {
            yield break;
        }

        // Now and then forget processes that exited while waiting (or whose check was dropped by a full queue).
        if (++_signatureTicks % 64 == 0)
        {
            foreach (var path in _awaitingSignature.Keys.ToList())
            {
                _awaitingSignature[path].RemoveAll(i => !running.Contains(i.Key));
                if (_awaitingSignature[path].Count == 0)
                {
                    _awaitingSignature.Remove(path);
                }
            }
        }

        foreach (var (path, signature) in _signatures.TakeCompleted())
        {
            if (!_awaitingSignature.Remove(path, out var waiting))
            {
                continue;
            }

            foreach (var identity in waiting.Where(i => running.Contains(i.Key)))
            {
                yield return identity with { Signer = signature.Signer, Signature = signature.Status };
            }
        }
    }

    private HashSet<int> SafeFindHung()
    {
        try
        {
            return HungWindows.FindHungProcesses();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogHangCheckFailed(_logger, ex);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read the process list (NtQuerySystemInformation)")]
    private static partial void LogSnapshotFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hung-window check failed")]
    private static partial void LogHangCheckFailed(ILogger logger, Exception exception);

    private readonly record struct Previous(long CpuTime, long ReadTransfer, long WriteTransfer, ProcessMetrics? Metrics);

    private sealed record Current(
        ProcessKey Key,
        string Name,
        int ParentPid,
        long CreateTime,
        int SessionId,
        long CpuTime,
        long WorkingSet,
        long PrivateWorkingSet,
        long Commit,
        long ReadTransfer,
        long WriteTransfer,
        int Threads,
        int Handles,
        int BasePriority);
}
