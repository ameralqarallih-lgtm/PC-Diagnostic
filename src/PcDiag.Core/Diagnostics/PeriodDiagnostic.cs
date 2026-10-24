using PcDiag.Core.Anomalies;
using PcDiag.Core.Events;

namespace PcDiag.Core.Diagnostics;

/// <summary>
/// Everything recorded in a period, gathered for the period diagnostic: every History series, the recording sessions,
/// events, anomalies, incidents, and per-process, per-service, per-sensor and per-volume figures.
/// </summary>
/// <param name="From">Period start.</param>
/// <param name="To">Period end.</param>
/// <param name="RawFrom">Where raw samples begin (process, sensor and volume details exist only from then on); null when there are none.</param>
/// <param name="Series">Every History series over the period, in buckets.</param>
/// <param name="Sessions">Recording sessions overlapping the period.</param>
/// <param name="Events">Windows, process, service, display and recorder events, oldest first.</param>
/// <param name="Anomalies">Anomalies the rules detected in the period, with their reasoning.</param>
/// <param name="Incidents">Incidents centred in the period.</param>
/// <param name="UncleanStops">Unclean stops detected in the period.</param>
/// <param name="Processes">Per-process figures over the period.</param>
/// <param name="ProcessNames">Process starts, exits and hangs by program name.</param>
/// <param name="Services">Service changes by service.</param>
/// <param name="Sensors">Hardware sensor ranges.</param>
/// <param name="Volumes">Volume free space.</param>
/// <param name="NetworkErrors">Packet errors per network adapter that had any.</param>
/// <param name="Peaks">The moments CPU, GPU and disk use peaked and available memory was lowest, with the processes using the most then.</param>
/// <param name="LogicalProcessors">Logical processors of this PC (CPU time as a share of capacity); 0 when unknown.</param>
public sealed record PeriodEvidence(
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset? RawFrom,
    IReadOnlyList<PeriodSeries> Series,
    IReadOnlyList<RecordingSpan> Sessions,
    IReadOnlyList<EvidenceEvent> Events,
    IReadOnlyList<AnomalyInfo> Anomalies,
    IReadOnlyList<PeriodIncident> Incidents,
    IReadOnlyList<PeriodUncleanStop> UncleanStops,
    IReadOnlyList<ProcessPeriodStats> Processes,
    IReadOnlyList<ProcessNameStats> ProcessNames,
    IReadOnlyList<ServicePeriodStats> Services,
    IReadOnlyList<SensorPeriodStats> Sensors,
    IReadOnlyList<VolumePeriodStats> Volumes,
    IReadOnlyList<NetworkErrorStats> NetworkErrors,
    IReadOnlyList<PeakMoment> Peaks,
    int LogicalProcessors);

/// <summary>One History series over the period.</summary>
/// <param name="Points">Buckets as [time (Unix ms), min, average, max], oldest first.</param>
public sealed record PeriodSeries(string Id, string Label, string Unit, string Group, string? Device, long BucketMs, IReadOnlyList<double[]> Points);

/// <summary>A recording session (clipped to the period).</summary>
public sealed record RecordingSpan(DateTimeOffset Start, DateTimeOffset End, bool Custom);

/// <summary>An incident centred in the period.</summary>
public sealed record PeriodIncident(string Key, DateTimeOffset CenterAt, string Title, Severity Severity, string Source);

/// <summary>An unclean stop (the recorder or Windows stopped without shutting down cleanly).</summary>
public sealed record PeriodUncleanStop(DateTimeOffset DetectedAt, DateTimeOffset LastHeartbeat, string Kind, bool WasRecording, string Summary);

/// <summary>
/// One process instance over the period, from its stored samples. CPU time used is the difference of its cumulative
/// CPU time between its first and last sample in the period.
/// </summary>
public sealed record ProcessPeriodStats(
    long ProcKey,
    string Name,
    int Pid,
    string? Path,
    string? Signature,
    string? Signer,
    DateTimeOffset FirstSample,
    DateTimeOffset LastSample,
    int Samples,
    double? CpuTimeMs,
    double? MaxCpuPercent,
    long? FirstPrivateBytes,
    long? LastPrivateBytes,
    long? MaxPrivateBytes,
    int? FirstHandles,
    int? LastHandles,
    double? MaxGpuPercent,
    double? MaxIoBytesPerSec);

/// <summary>Lifecycle of a program (all its processes) in the period.</summary>
public sealed record ProcessNameStats(string Name, int Started, int Stopped, int Hung, int Recovered);

/// <summary>Changes of a service in the period.</summary>
public sealed record ServicePeriodStats(string Name, string DisplayName, int Started, int Stopped, int Restarted, int Failed, int StartTypeChanged, DateTimeOffset? LastFailure);

/// <summary>A hardware sensor's range in the period.</summary>
public sealed record SensorPeriodStats(string Name, string Kind, string Unit, double Min, double Average, double Max, DateTimeOffset MaxAt);

/// <summary>A volume's free space in the period.</summary>
public sealed record VolumePeriodStats(string Mount, string? Label, long? TotalBytes, long? MinFreeBytes, DateTimeOffset? MinFreeAt);

/// <summary>Packet errors (received and sent) of a network adapter in the period.</summary>
/// <param name="SeriesId">The adapter's errors series (net.{device}.errors).</param>
/// <param name="Total">Errors in the period.</param>
/// <param name="Most">Most errors in one sample.</param>
/// <param name="MostAt">When.</param>
public sealed record NetworkErrorStats(string SeriesId, long Total, long Most, DateTimeOffset MostAt);

/// <summary>The moment a system value was at its extreme in the period (raw data only), with the processes using the most of that resource then.</summary>
/// <param name="SeriesId">The series: cpu, gpu, disk (highest) or memory.available (lowest).</param>
/// <param name="At">When.</param>
/// <param name="Value">The series' value then.</param>
/// <param name="Processes">The processes using the most then, most first: CPU % for cpu, GPU % for gpu, I/O bytes/s for disk, private bytes for memory.</param>
public sealed record PeakMoment(string SeriesId, DateTimeOffset At, double Value, IReadOnlyList<PeakProcess> Processes);

/// <summary>A process at a <see cref="PeakMoment"/>.</summary>
/// <param name="Value">Its use of the resource (see <see cref="PeakMoment.Processes"/>).</param>
public sealed record PeakProcess(string Name, int Pid, double Value);

/// <summary>The period diagnostic's report.</summary>
/// <param name="From">Period start.</param>
/// <param name="To">Period end.</param>
/// <param name="GeneratedAt">When it was made.</param>
/// <param name="Summary">Coverage and the most severe findings.</param>
/// <param name="Sections">One per aspect, in reading order.</param>
public sealed record PeriodReport(DateTimeOffset From, DateTimeOffset To, DateTimeOffset GeneratedAt, PeriodSummary Summary, IReadOnlyList<PeriodSection> Sections);

/// <summary>The report at a glance.</summary>
/// <param name="PeriodMinutes">Length of the period.</param>
/// <param name="RecordedMinutes">How much of it was recorded.</param>
/// <param name="FindingsBySeverity">Finding counts by severity name (Critical, High, Medium, Low, Info), without the anomaly recap.</param>
/// <param name="Top">The most severe findings across sections.</param>
/// <param name="Note">What limits the report (e.g. process details only within the raw retention), when anything does.</param>
public sealed record PeriodSummary(double PeriodMinutes, double RecordedMinutes, IReadOnlyDictionary<string, int> FindingsBySeverity, IReadOnlyList<PeriodFinding> Top, string? Note);

/// <summary>One aspect of the report.</summary>
/// <param name="Id">Stable ID (cpu, memory, gpu, storage, network, processes, services, display, system, sensors, anomalies, coverage).</param>
/// <param name="Title">Heading.</param>
/// <param name="Stats">Summary figures.</param>
/// <param name="Findings">Issues and abnormal behaviour, most severe first; empty when nothing stood out.</param>
/// <param name="Tables">Supporting tables (e.g. top CPU-time users).</param>
public sealed record PeriodSection(string Id, string Title, IReadOnlyList<PeriodStat> Stats, IReadOnlyList<PeriodFinding> Findings, IReadOnlyList<PeriodTable> Tables);

/// <summary>A summary figure.</summary>
/// <param name="SeriesId">The History series it comes from, to open it.</param>
public sealed record PeriodStat(string Label, string Value, string? SeriesId = null);

/// <summary>A small table of the report.</summary>
public sealed record PeriodTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>An issue or abnormal behaviour in the period.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Level">Evidence level (an observation, a logged event, a hedged possible explanation).</param>
/// <param name="Title">Short heading.</param>
/// <param name="Text">What was measured or logged, with times and figures.</param>
/// <param name="From">When it started (or happened), to open History there.</param>
/// <param name="To">When it ended, when it lasted.</param>
/// <param name="SeriesIds">History series showing it.</param>
/// <param name="Occurrences">How many times it happened in the period.</param>
/// <param name="Section">The section it belongs to (set on the summary's top findings).</param>
/// <param name="IncidentKey">The incident it is, to open it.</param>
public sealed record PeriodFinding(
    Severity Severity,
    EvidenceLevel Level,
    string Title,
    string Text,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IReadOnlyList<string>? SeriesIds = null,
    int Occurrences = 1,
    string? Section = null,
    string? IncidentKey = null);
