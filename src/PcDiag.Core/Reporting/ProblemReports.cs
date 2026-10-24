using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PcDiag.Core.Reporting;

/// <summary>
/// A problem report for the makers of PC Diagnostic: what went wrong in the product (not on the user's PC), with
/// its cause and enough context to fix it. Reports are written locally first; they leave the PC only when the user
/// sends one, or with the user's consent to automatic reports.
/// </summary>
/// <param name="Id">Report ID (automatic reports: the fingerprint, so repeats update one report).</param>
/// <param name="Kind">"manual" (the user wrote it) or "automatic" (an error the product caught).</param>
/// <param name="At">When it was written, or when the problem was last seen.</param>
/// <param name="Component">Where: "agent", "web", "ui", or a part such as "agent/collector:gpu".</param>
/// <param name="Summary">One line: the user's title, or the error in short.</param>
/// <param name="Description">What the user wrote (manual reports).</param>
/// <param name="Contact">An address to answer to, if the user gave one.</param>
/// <param name="Cause">The error and its chain of causes, with stacks.</param>
/// <param name="Product">Which build.</param>
/// <param name="Environment">Which Windows and hardware.</param>
/// <param name="Log">The log lines that led up to it.</param>
/// <param name="Context">Further facts (the page, the operation, the settings that matter).</param>
/// <param name="Fingerprint">Same problem, same fingerprint (type, message pattern and code location).</param>
/// <param name="Occurrences">How often it happened since the report was first written.</param>
/// <param name="FirstSeen">When it first happened.</param>
public sealed record ProblemReport(
    string Id,
    string Kind,
    DateTimeOffset At,
    string Component,
    string Summary,
    string? Description,
    string? Contact,
    ProblemCause? Cause,
    ProductFacts Product,
    EnvironmentFacts Environment,
    IReadOnlyList<ProblemLogLine> Log,
    IReadOnlyDictionary<string, string> Context,
    string Fingerprint,
    int Occurrences,
    DateTimeOffset FirstSeen);

/// <summary>An error and what caused it.</summary>
public sealed record ProblemCause(string Type, string Message, string? Stack, int? HResult, ProblemCause? Inner);

/// <summary>The build that had the problem.</summary>
/// <param name="InstallId">A random ID of this installation (not derived from the PC), to tell repeats from many PCs apart.</param>
public sealed record ProductFacts(string Name, string Version, string App, bool Elevated, string InstallId);

/// <summary>The PC, without anything that identifies it or its user.</summary>
public sealed record EnvironmentFacts(string Os, string Architecture, int Processors, long MemoryBytes, string Runtime, string Language, IReadOnlyList<string> Hardware);

/// <summary>A log line.</summary>
public sealed record ProblemLogLine(DateTimeOffset At, string Level, string Source, string Message);

/// <summary>A report as listed: where it is and whether it was sent.</summary>
/// <param name="Status">"pending" (waiting to be sent), "sent", "failed" (it will be tried again) or "local" (kept here; automatic reports are off).</param>
/// <param name="Reference">The report service's reference (for example a work item), once sent.</param>
public sealed record ProblemReportEntry(string Id, string Kind, string Component, string Summary, DateTimeOffset At, int Occurrences, string Status, DateTimeOffset? SentAt, string? Reference, string? LastError);

/// <summary>Removes what identifies the user or the PC from text bound for a report.</summary>
public static partial class ReportRedactor
{
    /// <summary>Redacts a text: user folders, the user and PC names, e-mail and network addresses.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = UserFolder().Replace(text, m => m.Groups[1].Value + "<user>");
        result = Email().Replace(result, "<email>");
        result = IPv4().Replace(result, m => m.Value.StartsWith("127.", StringComparison.Ordinal) ? m.Value : "<ip>");
        result = IPv6().Replace(result, "<ip>");
        foreach (var name in Names())
        {
            result = Regex.Replace(result, $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])", name == Environment.MachineName ? "<pc>" : "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        return result;
    }

    /// <summary>The PC's and user's names (at least 3 characters, so short ones do not blank common words).</summary>
    private static IEnumerable<string> Names() =>
        new[] { Environment.MachineName, Environment.UserName, Environment.UserDomainName }.Where(n => n is { Length: >= 3 } && !string.Equals(n, "WORKGROUP", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>An error's fingerprint: the same problem from the same place gives the same value on every PC.</summary>
    public static string Fingerprint(string component, string? type, string? message, string? stack)
    {
        // Numbers, IDs and quoted values vary between occurrences; the shape of the message and the frames do not.
        var shape = Variable().Replace(message ?? string.Empty, "#");
        var frames = string.Join('|', (stack ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at ", StringComparison.Ordinal))
            .Select(l => l.Split(" in ")[0]).Take(6));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{component}\n{type}\n{shape}\n{frames}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>An exception as a cause chain, redacted (at most 5 levels).</summary>
    public static ProblemCause? Cause(Exception? exception, int depth = 0) => exception is null || depth >= 5
        ? null
        : new ProblemCause(exception.GetType().FullName ?? exception.GetType().Name, Redact(exception.Message), Redact(exception.StackTrace), exception.HResult,
            Cause(exception is AggregateException { InnerExceptions.Count: > 0 } aggregate ? aggregate.InnerExceptions[0] : exception.InnerException, depth + 1));

    /// <summary>A short summary of an error.</summary>
    public static string Summarize(string component, Exception? exception, string message)
    {
        var text = exception is null ? message : $"{exception.GetType().Name}: {exception.Message}";
        text = Redact(text).ReplaceLineEndings(" ");
        var summary = $"{component}: {text}";
        return summary.Length <= 200 ? summary : summary[..197] + "...";
    }

    [GeneratedRegex(@"([A-Za-z]:\\Users\\)[^\\/""'\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserFolder();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IPv4();

    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{1,4}:){3,7}[0-9a-fA-F]{1,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6();

    [GeneratedRegex(@"""[^""]*""|'[^']*'|\b0x[0-9A-Fa-f]+\b|\b\d+(?:[.,]\d+)*\b|\b[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();
}

/// <summary>
/// The local store of problem reports: <c>{data}\reports\outbox</c> (not sent yet) and <c>…\sent</c>. The recorder and
/// the Web host write here; only the Web host sends (the recorder never connects out).
/// </summary>
public sealed class ProblemSpool(string root)
{
    /// <summary>Most reports kept, sent ones first to go.</summary>
    public const int MaxReports = 200;

    /// <summary>Largest report kept (larger logs are cut).</summary>
    public const int MaxReportBytes = 256 * 1024;

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    /// <summary>The folder of reports not sent yet.</summary>
    public string Outbox => Path.Combine(root, "outbox");

    /// <summary>The folder of sent reports.</summary>
    public string Sent => Path.Combine(root, "sent");

    /// <summary>Writes a new report, or for an automatic one already waiting adds an occurrence.</summary>
    public ProblemReport Write(ProblemReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            Directory.CreateDirectory(Outbox);
            var path = PathOf(Outbox, report.Id);
            if (report.Kind == "automatic" && Read(path) is { } existing)
            {
                report = report with { Occurrences = existing.Report.Occurrences + 1, FirstSeen = existing.Report.FirstSeen };
            }

            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, Json);
            if (json.Length > MaxReportBytes)
            {
                report = report with { Log = report.Log.TakeLast(20).ToList() };
                json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, Json);
            }

            var temp = path + ".tmp";
            File.WriteAllBytes(temp, json);
            File.Move(temp, path, overwrite: true);
            Trim();
            return report;
        }
    }

    /// <summary>Every report with its status, newest first.</summary>
    public IReadOnlyList<(ProblemReport Report, ProblemDelivery Delivery)> List()
    {
        lock (_gate)
        {
            return Files(Outbox).Concat(Files(Sent)).Select(Read).OfType<(ProblemReport, ProblemDelivery)>().OrderByDescending(r => r.Item1.At).ToList();
        }
    }

    /// <summary>One report, or null.</summary>
    public (ProblemReport Report, ProblemDelivery Delivery)? Get(string id)
    {
        if (!IsId(id))
        {
            return null;
        }

        lock (_gate)
        {
            return Read(PathOf(Outbox, id)) ?? Read(PathOf(Sent, id));
        }
    }

    /// <summary>Records a delivery attempt: sent (moved to the sent folder with the service's reference) or failed.</summary>
    public void MarkDelivery(string id, ProblemDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        lock (_gate)
        {
            var path = PathOf(Outbox, id);
            if (Read(path) is not { } found)
            {
                return;
            }

            var target = delivery.SentAt is null ? path : PathOf(Sent, id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target + ".tmp", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(found.Report, Json));
            File.Move(target + ".tmp", target, overwrite: true);
            File.WriteAllBytes(target + ".delivery", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(delivery, Json));
            if (target != path)
            {
                File.Delete(path);
                File.Delete(path + ".delivery");
            }
        }
    }

    /// <summary>Removes a report.</summary>
    public bool Delete(string id)
    {
        if (!IsId(id))
        {
            return false;
        }

        lock (_gate)
        {
            var deleted = false;
            foreach (var folder in new[] { Outbox, Sent })
            {
                var path = PathOf(folder, id);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    File.Delete(path + ".delivery");
                    deleted = true;
                }
            }

            return deleted;
        }
    }

    /// <summary>Report IDs: hex fingerprints or GUIDs, never a path.</summary>
    public static bool IsId(string? id) => id is { Length: >= 8 and <= 40 } && id.All(c => char.IsAsciiHexDigit(c) || c == '-');

    private static string PathOf(string folder, string id) => IsId(id) ? Path.Combine(folder, id + ".json") : throw new ArgumentException("Not a report ID.", nameof(id));

    private static IEnumerable<string> Files(string folder) => Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json") : [];

    private static (ProblemReport Report, ProblemDelivery Delivery)? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var report = System.Text.Json.JsonSerializer.Deserialize<ProblemReport>(File.ReadAllBytes(path), Json);
            var delivery = File.Exists(path + ".delivery") ? System.Text.Json.JsonSerializer.Deserialize<ProblemDelivery>(File.ReadAllBytes(path + ".delivery"), Json) : null;
            return report is null ? null : (report, delivery ?? new ProblemDelivery(null, null, 0, null, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void Trim()
    {
        var all = Files(Sent).Select(f => (f, sent: true)).Concat(Files(Outbox).Select(f => (f, sent: false)))
            .OrderBy(x => x.sent ? 0 : 1).ThenBy(x => File.GetLastWriteTimeUtc(x.f)).ToList();
        foreach (var (file, _) in all.Take(Math.Max(0, all.Count - MaxReports)))
        {
            File.Delete(file);
            File.Delete(file + ".delivery");
        }
    }
}

/// <summary>Where a report is in its delivery.</summary>
/// <param name="SentAt">When the report service accepted it.</param>
/// <param name="Reference">The service's reference for it.</param>
/// <param name="Attempts">Tries so far.</param>
/// <param name="LastError">Why the last try failed.</param>
/// <param name="NextAttempt">When to try again.</param>
public sealed record ProblemDelivery(DateTimeOffset? SentAt, string? Reference, int Attempts, string? LastError, DateTimeOffset? NextAttempt);

/// <summary>Formatting helpers shared by the writers.</summary>
public static class ProblemFacts
{
    /// <summary>The installation's random ID, created on first use (<c>{data}\reports\install-id</c>).</summary>
    public static string InstallId(string reportsRoot)
    {
        var path = Path.Combine(reportsRoot, "install-id");
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: 32 } existing)
            {
                return existing;
            }

            Directory.CreateDirectory(reportsRoot);
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    /// <summary>This Windows and machine, without names.</summary>
    public static EnvironmentFacts Environment(IReadOnlyList<string>? hardware = null) => new(
        System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        System.Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        CultureInfo.CurrentUICulture.Name,
        hardware ?? []);
}
