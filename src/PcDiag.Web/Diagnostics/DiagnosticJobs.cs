using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Engine.Diagnostics;
using PcDiag.Storage.Sqlite.Reading;
using PcDiag.Web.Reports;
using PcDiag.Web.Settings;

namespace PcDiag.Web.Diagnostics;

/// <summary>Where a period diagnostic is.</summary>
public enum DiagnosticJobState
{
    /// <summary>Waiting for another diagnostic to finish (one runs at a time).</summary>
    Queued = 0,

    /// <summary>Reading what was recorded in the period.</summary>
    Reading = 1,

    /// <summary>Analyzing it.</summary>
    Analyzing = 2,

    /// <summary>Finished; the report is ready.</summary>
    Completed = 3,

    /// <summary>Failed; see the error.</summary>
    Failed = 4,

    /// <summary>Cancelled by the user, or stopped with the Web host.</summary>
    Cancelled = 5,
}

/// <summary>A period diagnostic run in the background.</summary>
/// <param name="Id">The job's ID.</param>
/// <param name="From">Period start.</param>
/// <param name="To">Period end.</param>
/// <param name="State">Where it is.</param>
/// <param name="CreatedAt">When it was asked for.</param>
/// <param name="StartedAt">When it started reading (after waiting for others).</param>
/// <param name="FinishedAt">When it completed, failed or was cancelled.</param>
/// <param name="Error">Why it failed or was cancelled.</param>
/// <param name="FindingsBySeverity">The report's finding counts, once completed.</param>
/// <param name="Incidents">The incidents it was asked for (their recorded windows make its period), when it was.</param>
public sealed record DiagnosticJob(
    string Id,
    DateTimeOffset From,
    DateTimeOffset To,
    DiagnosticJobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    IReadOnlyDictionary<string, int>? FindingsBySeverity,
    IReadOnlyList<string>? Incidents = null)
{
    /// <summary>Still queued or running.</summary>
    public bool Running => State is DiagnosticJobState.Queued or DiagnosticJobState.Reading or DiagnosticJobState.Analyzing;
}

/// <summary>A job with its report (when completed).</summary>
public sealed record DiagnosticJobDetail(DiagnosticJob Job, PeriodReport? Report);

/// <summary>
/// Period diagnostics as background jobs (Phase 17): a diagnostic runs in the Web host whatever the browser does, so
/// leaving the page neither stops nor loses it; the page finds it again by its ID or in the list of recent ones, and it
/// can be cancelled. One runs at a time (a long period reads a lot); others wait. Finished reports are kept in
/// <see cref="DataPaths.DiagnosticsDirectory"/>, the most recent <see cref="KeepReports"/>, so they survive the Web
/// host restarting; a job still running when the host stops ends as cancelled.
/// </summary>
public sealed partial class DiagnosticJobs : IDisposable
{
    /// <summary>Finished reports kept.</summary>
    public const int KeepReports = 20;

    /// <summary>Most jobs queued or running at once.</summary>
    public const int MaxRunning = 4;

    /// <summary>A finished diagnostic of the same period this recent is reused instead of running again (unless asked afresh).</summary>
    public static readonly TimeSpan ReuseFor = TimeSpan.FromMinutes(5);

    /// <summary>One analysis at a time (also the synchronous endpoint).</summary>
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly ConcurrentDictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly PeriodEvidenceReader _reader;
    private readonly SettingsStore _settings;
    private readonly DataPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<DiagnosticJobs> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();
    private bool _loaded;

    /// <summary>Creates the job runner.</summary>
    public DiagnosticJobs(PeriodEvidenceReader reader, SettingsStore settings, DataPaths paths, TimeProvider time, IHostApplicationLifetime lifetime, ILogger<DiagnosticJobs> logger)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _reader = reader;
        _settings = settings;
        _paths = paths;
        _time = time;
        _logger = logger;
        lifetime.ApplicationStopping.Register(_stopping.Cancel);
    }

    /// <summary>Starts a diagnostic of a period (validated by the caller), or returns the one already running or just finished for it.</summary>
    /// <returns>The job, or null when too many are already queued or running.</returns>
    public DiagnosticJob? Start(DateTimeOffset from, DateTimeOffset to, bool fresh, IReadOnlyList<string>? incidents = null)
    {
        var asked = incidents is { Count: > 0 } ? incidents.Order(StringComparer.Ordinal).ToList() : null;
        EnsureLoaded();
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var same = _jobs.Values.Select(e => e.Job)
                .Where(j => j.From == from && j.To == to && (j.Incidents ?? []).SequenceEqual(asked ?? [], StringComparer.Ordinal))
                .OrderByDescending(j => j.CreatedAt).FirstOrDefault();
            if (same is not null && (same.Running || (!fresh && same.State == DiagnosticJobState.Completed && now - same.FinishedAt < ReuseFor)))
            {
                return same;
            }

            if (_jobs.Values.Count(e => e.Job.Running) >= MaxRunning)
            {
                return null;
            }

            var id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
            var entry = new Entry(new DiagnosticJob(id, from, to, DiagnosticJobState.Queued, now, null, null, null, null, asked), CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
            _jobs[id] = entry;
            LogStarted(_logger, id, from, to);
            entry.Task = Task.Run(() => RunAsync(entry));
            return entry.Job;
        }
    }

    /// <summary>Recent jobs, newest first.</summary>
    public IReadOnlyList<DiagnosticJob> List()
    {
        EnsureLoaded();
        return _jobs.Values.Select(e => e.Job).OrderByDescending(j => j.CreatedAt).ToList();
    }

    /// <summary>A job with its report; null when unknown.</summary>
    public DiagnosticJobDetail? Get(string id)
    {
        EnsureLoaded();
        if (!_jobs.TryGetValue(id, out var entry))
        {
            return null;
        }

        var job = entry.Job;
        return new DiagnosticJobDetail(job, job.State == DiagnosticJobState.Completed ? entry.Report ?? LoadReport(id) : null);
    }

    /// <summary>Cancels a job still running, or forgets a finished one (and its saved report). False when unknown.</summary>
    public bool CancelOrRemove(string id)
    {
        EnsureLoaded();
        if (!_jobs.TryGetValue(id, out var entry))
        {
            return false;
        }

        if (entry.Job.Running)
        {
            entry.Cancellation.Cancel();
            return true;
        }

        _jobs.TryRemove(id, out _);
        TryDelete(FileFor(id));
        return true;
    }

    /// <summary>Waits for a job to finish (tests).</summary>
    internal Task WaitAsync(string id) => _jobs.TryGetValue(id, out var entry) && entry.Task is { } task ? task : Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Cancel();
        foreach (var entry in _jobs.Values)
        {
            entry.Cancellation.Dispose();
        }

        _stopping.Dispose();
    }

    private async Task RunAsync(Entry entry)
    {
        var token = entry.Cancellation.Token;
        var acquired = false;
        try
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            Update(entry, j => j with { State = DiagnosticJobState.Reading, StartedAt = _time.GetUtcNow() });
            var evidence = await _reader.LoadAsync(entry.Job.From, entry.Job.To, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Update(entry, j => j with { State = DiagnosticJobState.Analyzing });
            var rules = AnomalyRuleCatalog.Default.Apply(_settings.Load().Triggers ?? new TriggerOptions());
            var report = PeriodDiagnostics.Analyze(evidence, rules, _time.LocalTimeZone, _time.GetUtcNow());
            entry.Report = report;
            Update(entry, j => j with { State = DiagnosticJobState.Completed, FinishedAt = _time.GetUtcNow(), FindingsBySeverity = report.Summary.FindingsBySeverity });
            Save(entry);
            LogFinished(_logger, entry.Job.Id, "completed");
        }
        catch (OperationCanceledException)
        {
            var why = _stopping.IsCancellationRequested ? "The dashboard host stopped before the diagnostic finished." : "Cancelled.";
            Update(entry, j => j with { State = DiagnosticJobState.Cancelled, FinishedAt = _time.GetUtcNow(), Error = why });
            LogFinished(_logger, entry.Job.Id, "cancelled");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(_logger, ex, entry.Job.Id);
            Update(entry, j => j with { State = DiagnosticJobState.Failed, FinishedAt = _time.GetUtcNow(), Error = "The diagnostic failed: " + ex.Message });
        }
        finally
        {
            if (acquired)
            {
                Gate.Release();
            }
        }
    }

    private static void Update(Entry entry, Func<DiagnosticJob, DiagnosticJob> change) => entry.Job = change(entry.Job);

    private string FileFor(string id) => Path.Combine(_paths.DiagnosticsDirectory, id + ".json");

    /// <summary>Saves a finished report and keeps only the most recent ones.</summary>
    private void Save(Entry entry)
    {
        try
        {
            Directory.CreateDirectory(_paths.DiagnosticsDirectory);
            var path = FileFor(entry.Job.Id);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(new DiagnosticJobDetail(entry.Job, entry.Report), IncidentReport.Json));
            File.Move(path + ".tmp", path, overwrite: true);
            foreach (var old in _jobs.Values.Where(e => e.Job.State == DiagnosticJobState.Completed).OrderByDescending(e => e.Job.CreatedAt).Skip(KeepReports).ToList())
            {
                _jobs.TryRemove(old.Job.Id, out _);
                TryDelete(FileFor(old.Job.Id));
            }

            // The report stays on disk; memory holds only the latest few.
            foreach (var kept in _jobs.Values.Where(e => e.Report is not null).OrderByDescending(e => e.Job.CreatedAt).Skip(3))
            {
                kept.Report = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, ex, entry.Job.Id);
        }
    }

    private PeriodReport? LoadReport(string id) => Read(FileFor(id))?.Report;

    private static DiagnosticJobDetail? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DiagnosticJobDetail>(File.ReadAllBytes(path), IncidentReport.Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The finished diagnostics saved by earlier runs of the Web host.</summary>
    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            if (!Directory.Exists(_paths.DiagnosticsDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_paths.DiagnosticsDirectory, "*.json"))
            {
                if (Read(file) is { Job: { State: DiagnosticJobState.Completed } job } && Path.GetFileNameWithoutExtension(file) == job.Id)
                {
                    _jobs.TryAdd(job.Id, new Entry(job, new CancellationTokenSource()));
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Period diagnostic {Id} started for {From:O} – {To:O}")]
    private static partial void LogStarted(ILogger logger, string id, DateTimeOffset from, DateTimeOffset to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Period diagnostic {Id} {Outcome}")]
    private static partial void LogFinished(ILogger logger, string id, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Period diagnostic {Id} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Period diagnostic {Id}: the report could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string id);

    private sealed class Entry(DiagnosticJob job, CancellationTokenSource cancellation)
    {
        private DiagnosticJob _job = job;

        public DiagnosticJob Job
        {
            get => Volatile.Read(ref _job);
            set => Volatile.Write(ref _job, value);
        }

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public PeriodReport? Report { get; set; }

        public Task? Task { get; set; }
    }
}
