namespace PcDiag.Core.Diagnostics;

/// <summary>
/// How strong a statement in a diagnostic report is. The diagnostic engine never presents speculation as fact.
/// </summary>
public enum EvidenceLevel
{
    /// <summary>A measured fact about telemetry, e.g. "GPU utilization reached 99 %".</summary>
    Observation = 0,

    /// <summary>Two things happened close together in time. Not a claim of cause.</summary>
    Correlation = 1,

    /// <summary>A hypothesis consistent with the evidence ("may", "could be consistent with").</summary>
    PossibleExplanation = 2,

    /// <summary>Something Windows itself recorded, e.g. "Event 4101: display driver stopped responding and recovered".</summary>
    ConfirmedEvent = 3,

    /// <summary>
    /// What the data does not establish, e.g. "The collected telemetry does not establish that the GPU caused the
    /// problem." Always phrased as a negation; every summary ends with at least one.
    /// </summary>
    NotProven = 4,
}

/// <summary>One statement in a diagnostic summary, linked to the data behind it.</summary>
/// <param name="Level">Evidence level.</param>
/// <param name="Text">The statement.</param>
/// <param name="EvidenceRefs">The data points supporting the statement.</param>
public sealed record Finding(EvidenceLevel Level, string Text, IReadOnlyList<EvidenceRef> EvidenceRefs);

/// <summary>A pointer to a piece of evidence.</summary>
/// <param name="Kind">Evidence kind, e.g. "metric", "event", "anomaly", "process".</param>
/// <param name="Timestamp">When the evidence occurred.</param>
/// <param name="SourceId">Identifier within its kind (series name, event ID, anomaly ID), when applicable.</param>
public sealed record EvidenceRef(string Kind, DateTimeOffset Timestamp, string? SourceId = null);
