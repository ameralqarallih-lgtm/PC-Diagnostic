using PcDiag.Core.Diagnostics;
using PcDiag.Core.Incidents;

namespace PcDiag.Core.Abstractions;

/// <summary>A single-purpose diagnostic analyzer that turns incident evidence into findings.</summary>
/// <typeparam name="TEvidence">The evidence bundle type (defined by the engine).</typeparam>
public interface IDiagnosticAnalyzer<in TEvidence>
{
    /// <summary>Analyzer identifier.</summary>
    string Id { get; }

    /// <summary>Produces findings. Must use wording that matches each finding's <see cref="EvidenceLevel"/>.</summary>
    IEnumerable<Finding> Analyze(TEvidence evidence);
}
