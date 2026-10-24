using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Incidents;

namespace PcDiag.Engine.Diagnostics;

/// <summary>
/// The diagnostic summary of an incident (PROJECT §19): runs the analyzers over the incident's evidence and orders
/// their findings as OBSERVATIONS (what was measured and what Windows logged, including what was <i>not</i> seen),
/// POSSIBLE CORRELATIONS (what happened close to the incident, and hedged explanations for what the user reported)
/// and NOT PROVEN (what the data does not establish). Every finding passes the <see cref="LanguageGuard"/>; one that
/// does not is dropped and logged rather than shown.
/// </summary>
public sealed partial class IncidentDiagnostics
{
    /// <summary>
    /// Version of the analyzers. Finished incidents analyzed by an older version (or never) are analyzed again when
    /// the Agent starts, so improvements reach existing incidents.
    /// </summary>
    public const int AnalyzerVersion = 4;

    /// <summary>Most findings in a summary.</summary>
    public const int MaxFindings = 60;

    /// <summary>The analyzers, in the order their findings appear within a section.</summary>
    public static IReadOnlyList<IDiagnosticAnalyzer<AnalysisContext>> Analyzers { get; } =
    [
        new RecordingSelectionAnalyzer(),
        new AnomalyAnalyzer(),
        new SystemEventAnalyzer(),
        new DisplayEventAnalyzer(),
        new GpuActivityAnalyzer(),
        new GpuClockAnalyzer(),
        new CpuAnalyzer(),
        new MemoryPressureAnalyzer(),
        new DiskLatencyAnalyzer(),
        new NetworkAnalyzer(),
        new ProcessLifecycleAnalyzer(),
        new ThermalAnalyzer(),
    ];

    private static readonly Dictionary<string, string> Hypotheses = new(StringComparer.Ordinal)
    {
        [Subsystems.Gpu] = "a display driver or GPU problem",
        [Subsystems.Cpu] = "the processor being fully loaded",
        [Subsystems.Memory] = "the system running short of memory",
        [Subsystems.Storage] = "slow or failing storage",
        [Subsystems.Network] = "a network adapter or connection problem",
        [Subsystems.Application] = "a problem in that application",
        [Subsystems.Thermal] = "temperature, power or thermal limits",
        [Subsystems.System] = "a hardware or driver problem that Windows recorded",
    };

    private static readonly Dictionary<string, string> Nouns = new(StringComparer.Ordinal)
    {
        [Subsystems.Gpu] = "GPU and display",
        [Subsystems.Cpu] = "CPU",
        [Subsystems.Memory] = "memory",
        [Subsystems.Storage] = "storage",
        [Subsystems.Network] = "network",
        [Subsystems.Application] = "application",
        [Subsystems.Thermal] = "thermal",
        [Subsystems.System] = "Windows and hardware",
    };

    private static readonly Dictionary<string, string> Subjects = new(StringComparer.Ordinal)
    {
        [Subsystems.Gpu] = "the GPU or the display driver",
        [Subsystems.Cpu] = "the CPU load",
        [Subsystems.Memory] = "the memory activity",
        [Subsystems.Storage] = "the storage activity",
        [Subsystems.Network] = "the network problems",
        [Subsystems.Application] = "the application crash or hang",
        [Subsystems.Thermal] = "the temperatures or performance limits",
        [Subsystems.System] = "the events Windows logged",
    };

    private readonly IIncidentEvidenceSource _evidence;
    private readonly IIncidentStore _store;
    private readonly IOptionsMonitor<TriggerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IncidentDiagnostics> _logger;

    /// <summary>Creates the service.</summary>
    public IncidentDiagnostics(IIncidentEvidenceSource evidence, IIncidentStore store, IOptionsMonitor<TriggerOptions> options, TimeProvider timeProvider, ILogger<IncidentDiagnostics> logger)
    {
        _evidence = evidence;
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The findings for an incident, in summary order. <paramref name="rejected"/> receives findings the language
    /// guard refused (none, unless an analyzer is wrong).
    /// </summary>
    public static IReadOnlyList<Finding> Analyze(IncidentEvidence evidence, IReadOnlyList<AnomalyRule> rules, TimeZoneInfo zone, List<(Finding Finding, string Problem)>? rejected = null)
    {
        var context = new AnalysisContext(evidence, rules, zone);
        var observations = new List<(Finding Finding, int Order)>();
        foreach (var analyzer in Analyzers)
        {
            foreach (var finding in analyzer.Analyze(context))
            {
                observations.Add((finding, observations.Count));
            }
        }

        // Positive observations in time order, then what was not seen (no evidence, or only whole-window ranges).
        var ordered = observations
            .OrderBy(o => IsNegative(o.Finding) ? 1 : 0)
            .ThenBy(o => IsNegative(o.Finding) ? DateTimeOffset.MinValue : o.Finding.EvidenceRefs.Min(r => r.Timestamp))
            .ThenBy(o => o.Order)
            .Select(o => o.Finding)
            .ToList();

        var (correlations, correlated) = Correlate(context);
        // Identical statements (identical adapters with identical values) are said once.
        var all = ordered.Concat(correlations).Concat(NotProven(context, correlated)).DistinctBy(f => (f.Level, f.Text));

        var result = new List<Finding>();
        foreach (var finding in all)
        {
            if (LanguageGuard.Violation(finding) is { } problem)
            {
                rejected?.Add((finding, problem));
                continue;
            }

            if (result.Count < MaxFindings || finding.Level == EvidenceLevel.NotProven)
            {
                result.Add(finding);
            }
        }

        return result;
    }

    /// <summary>Analyzes one incident and stores its findings. False when it has no evidence to analyze.</summary>
    public async Task<bool> AnalyzeAsync(IncidentKey key, CancellationToken cancellationToken)
    {
        var evidence = await _evidence.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        if (evidence is null)
        {
            return false;
        }

        IReadOnlyList<AnomalyRule> rules;
        try
        {
            rules = AnomalyRuleCatalog.Default.Apply(_options.CurrentValue);
        }
        catch (OptionsValidationException)
        {
            rules = AnomalyRuleCatalog.Default.Rules;
        }

        var rejected = new List<(Finding Finding, string Problem)>();
        var findings = Analyze(evidence, rules, _timeProvider.LocalTimeZone, rejected);
        foreach (var (finding, problem) in rejected)
        {
            LogRejected(_logger, key.ToString(), problem, finding.Text);
        }

        var saved = await _store.SaveFindingsAsync(key, findings, AnalyzerVersion, cancellationToken).ConfigureAwait(false);
        if (saved)
        {
            LogAnalyzed(_logger, key.ToString(), findings.Count);
        }

        return saved;
    }

    /// <summary>Analyzes finished incidents that an older analyzer version (or none) analyzed, newest first.</summary>
    public async Task<int> AnalyzePendingAsync(int limit, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var key in await _store.ListUnanalyzedAsync(AnalyzerVersion, limit, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await AnalyzeAsync(key, cancellationToken).ConfigureAwait(false))
                {
                    count++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                LogFailed(_logger, ex, key.ToString());
            }
        }

        return count;
    }

    private static bool IsNegative(Finding finding) => finding.EvidenceRefs.Count == 0 || finding.EvidenceRefs.All(r => r.Kind == "range");

    private static (List<Finding> Findings, List<string> Subsystems) Correlate(AnalysisContext context)
    {
        var findings = new List<Finding>();
        var groups = context.Notables
            .Where(n => context.IsAround(n.At))
            .GroupBy(n => n.Subsystem)
            .OrderBy(g => g.Min(n => (n.At - context.Center).Duration()))
            .ToList();

        foreach (var group in groups)
        {
            var items = group
                .OrderBy(n => (n.At - context.Center).Duration())
                .DistinctBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .Select(n => $"{n.Label} ({context.Offset(n.At)})");
            findings.Add(new Finding(EvidenceLevel.Correlation, $"{group.Key} activity coincided with {context.Incident}: {string.Join("; ", items)}.",
                group.Select(n => n.Evidence).Take(6).ToList()));
        }

        var subsystems = groups.Select(g => g.Key).ToList();
        foreach (var subsystem in Relevant(context.Evidence.UserType).Where(subsystems.Contains))
        {
            findings.Add(new Finding(EvidenceLevel.PossibleExplanation,
                $"The {Nouns[subsystem]} activity may be related to {context.Incident}; it could be consistent with {Hypotheses[subsystem]}.",
                groups.First(g => g.Key == subsystem).Select(n => n.Evidence).Take(3).ToList()));
        }

        return (findings, subsystems);
    }

    /// <summary>Subsystems that could plausibly relate to what the user reported, most likely first.</summary>
    private static IEnumerable<string> Relevant(UserIncidentType? type) => type switch
    {
        UserIncidentType.ScreenFlicker or UserIncidentType.ScreenArtifact => [Subsystems.Gpu, Subsystems.Thermal, Subsystems.System],
        UserIncidentType.SystemFreeze => [Subsystems.Memory, Subsystems.Storage, Subsystems.Gpu, Subsystems.Thermal, Subsystems.System],
        UserIncidentType.Slowdown => [Subsystems.Cpu, Subsystems.Memory, Subsystems.Storage, Subsystems.Thermal],
        UserIncidentType.ApplicationCrash or UserIncidentType.GameCrash => [Subsystems.Application, Subsystems.Gpu, Subsystems.Memory, Subsystems.System],
        UserIncidentType.HighCpu => [Subsystems.Cpu, Subsystems.Thermal],
        UserIncidentType.HighRam => [Subsystems.Memory],
        UserIncidentType.HighGpu => [Subsystems.Gpu, Subsystems.Thermal],
        UserIncidentType.DiskProblem => [Subsystems.Storage],
        UserIncidentType.NetworkProblem => [Subsystems.Network],
        _ => [],
    };

    private static IEnumerable<Finding> NotProven(AnalysisContext context, List<string> correlated)
    {
        if (correlated.Count == 0)
        {
            yield return new Finding(EvidenceLevel.NotProven,
                $"The collected telemetry does not establish what caused {context.Incident}; no measured activity stood out around that moment.", []);
            yield break;
        }

        foreach (var subsystem in correlated.Take(3))
        {
            yield return new Finding(EvidenceLevel.NotProven, $"The collected telemetry does not establish that {Subjects[subsystem]} caused {context.Incident}.", []);
        }

        yield return new Finding(EvidenceLevel.NotProven, "Timing alone does not establish a connection: events that occur close together can be unrelated.", []);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Incident {Key} analyzed: {Count} findings")]
    private static partial void LogAnalyzed(ILogger logger, string key, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident {Key}: a finding was dropped by the language guard ({Problem}): {Text}")]
    private static partial void LogRejected(ILogger logger, string key, string problem, string text);

    [LoggerMessage(Level = LogLevel.Error, Message = "Incident {Key} could not be analyzed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string key);
}
