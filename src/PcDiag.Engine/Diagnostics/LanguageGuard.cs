using System.Text.RegularExpressions;
using PcDiag.Core.Diagnostics;

namespace PcDiag.Engine.Diagnostics;

/// <summary>
/// Wording discipline for diagnostic findings (ARCHITECTURE §10): telemetry shows what happened together, never
/// why, so causal wording is rejected everywhere except in quoted text (what Windows logged, quoted verbatim) and in
/// NOT PROVEN statements, which exist to say what the data does not establish. Correlations must say "coincided with" or
/// "before"/"after"; possible explanations must say "may" or "could".
/// </summary>
public static partial class LanguageGuard
{
    /// <summary>Why a finding's wording is not allowed, or null when it is.</summary>
    public static string? Violation(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var text = finding.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return "empty text";
        }

        if (finding.Level == EvidenceLevel.NotProven)
        {
            return Negation().IsMatch(text) ? null : "a NOT PROVEN statement must be a negation (\"does not establish\")";
        }

        var checkedText = Quoted().Replace(text, "\"\"");
        if (Causal().Match(checkedText) is { Success: true } causal)
        {
            return $"causal wording \"{causal.Value}\" in a {finding.Level} statement";
        }

        return finding.Level switch
        {
            EvidenceLevel.Correlation when !Temporal().IsMatch(checkedText) => "a correlation must say \"coincided with\" or \"before\"/\"after\"",
            EvidenceLevel.PossibleExplanation when !Hedge().IsMatch(checkedText) => "a possible explanation must say \"may\" or \"could\"",
            _ => null,
        };
    }

    /// <summary>Throws when a finding's wording is not allowed (used by tests and analyzers in debug builds).</summary>
    public static Finding Check(Finding finding) =>
        Violation(finding) is { } problem ? throw new InvalidOperationException($"{problem}: {finding.Text}") : finding;

    [GeneratedRegex(@"\b(caus(e|ed|es|ing)|because|due to|led to|leads? to|result(ed|s)? in|responsible for|as a result of|triggered by|root cause|prov(e|es|ed|en|ing)|confirm(s|ed|ing)?|definitely|certainly|clearly)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Causal();

    [GeneratedRegex(@"\b(coincided with|before|after)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Temporal();

    [GeneratedRegex(@"\b(may|could)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Hedge();

    [GeneratedRegex(@"\b(does not establish|do not establish|not proven|cannot (establish|show)|does not show)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Negation();

    [GeneratedRegex("\"[^\"]*\"|“[^”]*”")]
    private static partial Regex Quoted();
}
