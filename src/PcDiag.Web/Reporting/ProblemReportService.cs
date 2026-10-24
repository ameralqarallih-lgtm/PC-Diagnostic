using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using PcDiag.Core.Configuration;
using PcDiag.Core.Reporting;
using PcDiag.Hosting;
using PcDiag.Storage.Sqlite.Reading;
using PcDiag.Web.Settings;

namespace PcDiag.Web.Reporting;

/// <summary>The user's choice about automatic problem reports (settings.json, "Reporting").</summary>
/// <param name="Automatic">Send the problems PC Diagnostic catches by itself, without asking each time.</param>
/// <param name="AskedAt">When the user was asked (the question is asked once).</param>
/// <param name="Contact">An address the makers may answer to, if the user wants an answer.</param>
public sealed record ReportingSettings(bool Automatic, DateTimeOffset? AskedAt, string? Contact);

/// <summary>A report the user writes.</summary>
/// <param name="Summary">What went wrong, in a line.</param>
/// <param name="Description">What happened and what they were doing.</param>
/// <param name="Contact">Where to answer, if they want an answer.</param>
/// <param name="IncludeLog">Add the recent log lines of the recorder and the dashboard (redacted).</param>
/// <param name="IncludeSystem">Add the hardware summary (processor, graphics, board, memory, drives; no serial numbers).</param>
/// <param name="Page">The page they were on.</param>
public sealed record ManualReportRequest(string Summary, string? Description, string? Contact, bool IncludeLog, bool IncludeSystem, string? Page);

/// <summary>An error the dashboard caught in the browser.</summary>
public sealed record ClientErrorRequest(string Message, string? Stack, string? Page, string? Component);

/// <summary>Reports and the user's choices, for the Settings page.</summary>
/// <param name="ServiceConfigured">This build knows where to send reports.</param>
public sealed record ReportsOverview(bool ServiceConfigured, bool Automatic, DateTimeOffset? AskedAt, string? Contact, IReadOnlyList<ProblemReportEntry> Items);

/// <summary>What happened to a report on the way.</summary>
/// <param name="Sent">The report service accepted it.</param>
/// <param name="Reference">Its reference there (for example a work item number).</param>
/// <param name="Problem">Why it was not sent (it stays in the outbox).</param>
public sealed record SendOutcome(bool Sent, string? Reference, string? Problem);

/// <summary>
/// Problem reports in the Web host: manual reports the user writes (with a preview of exactly what is sent), the
/// automatic ones the recorder and the Web host wrote to the outbox, and sending them to the makers' report service
/// (<c>Reporting:Endpoint</c>, HTTPS). Automatic reports leave the PC only with the user's consent; a manual report
/// only when the user sends it. Nothing recorded about the PC (telemetry, processes, incidents) is ever included.
/// </summary>
public sealed partial class ProblemReportService(
    DataPaths paths,
    SettingsStore settings,
    IHttpClientFactory httpClients,
    IConfiguration configuration,
    ProblemCapture capture,
    SystemReader systemReader,
    TimeProvider time,
    ILogger<ProblemReportService> logger) : IDisposable
{
    /// <summary>The HTTP client name.</summary>
    public const string HttpClientName = "problem-reports";

    /// <summary>Most log lines added to a manual report.</summary>
    public const int ManualLogLines = 80;

    private readonly ProblemSpool _spool = new(paths.ReportsDirectory);
    private readonly SemaphoreSlim _sending = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _sending.Dispose();

    /// <summary>Where reports go, or null when this build has no report service.</summary>
    public Uri? Endpoint
    {
        get
        {
            var value = configuration["Reporting:Endpoint"];
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return null;
            }

            // HTTPS only (plain HTTP just for a service on this PC, as in development and tests).
            return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
        }
    }

    /// <summary>The user's choices.</summary>
    public ReportingSettings Settings => settings.LoadReporting();

    /// <summary>Everything for the Settings page.</summary>
    public ReportsOverview Overview()
    {
        var choice = Settings;
        var items = _spool.List().Select(r => Entry(r.Report, r.Delivery, choice.Automatic)).ToList();
        return new ReportsOverview(Endpoint is not null, choice.Automatic, choice.AskedAt, choice.Contact, items);
    }

    /// <summary>One report as it would be (or was) sent.</summary>
    public ProblemReport? Get(string id) => _spool.Get(id)?.Report;

    /// <summary>Removes a report.</summary>
    public bool Delete(string id) => _spool.Delete(id);

    /// <summary>Saves the user's choice about automatic reports (the question then counts as asked).</summary>
    public ReportsOverview SetConsent(bool automatic, string? contact)
    {
        settings.SaveReporting(new ReportingSettings(automatic, time.GetUtcNow(), string.IsNullOrWhiteSpace(contact) ? null : contact.Trim()));
        LogConsent(logger, automatic);
        return Overview();
    }

    /// <summary>The report a request would make, without saving or sending it (the preview).</summary>
    public async Task<ProblemReport> ComposeAsync(ManualReportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = time.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        var hardware = request.IncludeSystem ? await HardwareAsync(cancellationToken).ConfigureAwait(false) : [];
        var context = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(request.Page))
        {
            context["page"] = ReportRedactor.Redact(request.Page.Trim());
        }

        return new ProblemReport(
            id,
            "manual",
            now,
            "user",
            request.Summary.Trim(),
            string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            string.IsNullOrWhiteSpace(request.Contact) ? null : request.Contact.Trim(),
            null,
            new ProductFacts(ProductInfo.Name, ProductInfo.Version, "web", false, ProblemFacts.InstallId(paths.ReportsDirectory)),
            ProblemFacts.Environment(hardware),
            request.IncludeLog ? LogTail.Read(paths.LogsDirectory, ManualLogLines) : [],
            context,
            id,
            1,
            now);
    }

    /// <summary>Saves a manual report and sends it when the report service is known.</summary>
    public async Task<(ProblemReportEntry Entry, SendOutcome Outcome)> SubmitAsync(ManualReportRequest request, CancellationToken cancellationToken)
    {
        var report = _spool.Write(await ComposeAsync(request, cancellationToken).ConfigureAwait(false));
        var outcome = await SendAsync(report.Id, cancellationToken).ConfigureAwait(false);
        var stored = _spool.Get(report.Id);
        return (Entry(stored?.Report ?? report, stored?.Delivery ?? new ProblemDelivery(null, null, 0, null, null), Settings.Automatic), outcome);
    }

    /// <summary>An error the dashboard caught in the browser, written like any other automatic report.</summary>
    public void CaptureClientError(ClientErrorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var component = $"ui/{(string.IsNullOrWhiteSpace(request.Component) ? "page" : request.Component.Trim())}";
        capture.Capture(component, new ClientError(request.Message, request.Stack), request.Message,
            new Dictionary<string, string> { ["page"] = ReportRedactor.Redact(request.Page ?? string.Empty) });
    }

    /// <summary>Sends one report now (the user pressed Send, or the automatic delivery is due).</summary>
    public async Task<SendOutcome> SendAsync(string id, CancellationToken cancellationToken)
    {
        if (Endpoint is not { } endpoint)
        {
            return new SendOutcome(false, null, "This build has no report service configured. Download the report and send it to support instead.");
        }

        if (_spool.Get(id) is not { } found)
        {
            return new SendOutcome(false, null, "There is no such report.");
        }

        if (found.Delivery.SentAt is not null)
        {
            return new SendOutcome(true, found.Delivery.Reference, null);
        }

        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(new Uri(endpoint, "v1/reports"), found.Report, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed(found, $"The report service answered {(int)response.StatusCode}.");
            }

            var accepted = await response.Content.ReadFromJsonAsync<Accepted>(cancellationToken).ConfigureAwait(false);
            _spool.MarkDelivery(id, new ProblemDelivery(time.GetUtcNow(), accepted?.Reference ?? accepted?.Id, found.Delivery.Attempts + 1, null, null));
            LogSent(logger, found.Report.Kind, found.Report.Summary, accepted?.Reference);
            return new SendOutcome(true, accepted?.Reference ?? accepted?.Id, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Failed(found, ex is TaskCanceledException ? "The report service did not answer in time." : "The report service could not be reached.");
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Sends the automatic reports that are due (only with the user's consent).</summary>
    public async Task<int> SendDueAsync(CancellationToken cancellationToken)
    {
        if (Endpoint is null || !Settings.Automatic)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var sent = 0;
        foreach (var (report, delivery) in _spool.List().Where(r => r.Report.Kind == "automatic" && r.Delivery.SentAt is null && (r.Delivery.NextAttempt ?? now) <= now).Take(20))
        {
            if ((await SendAsync(report.Id, cancellationToken).ConfigureAwait(false)).Sent)
            {
                sent++;
            }
        }

        return sent;
    }

    private SendOutcome Failed((ProblemReport Report, ProblemDelivery Delivery) found, string problem)
    {
        var attempts = found.Delivery.Attempts + 1;
        // Tries again after 5 min, 20 min, 45 min … at most 6 hours apart.
        var wait = TimeSpan.FromMinutes(Math.Min(360, 5 * attempts * attempts));
        _spool.MarkDelivery(found.Report.Id, new ProblemDelivery(null, null, attempts, problem, time.GetUtcNow() + wait));
        LogNotSent(logger, found.Report.Summary, problem);
        return new SendOutcome(false, null, problem);
    }

    private static ProblemReportEntry Entry(ProblemReport report, ProblemDelivery delivery, bool automatic) => new(
        report.Id,
        report.Kind,
        report.Component,
        report.Summary,
        report.At,
        report.Occurrences,
        delivery.SentAt is not null ? "sent" : delivery.LastError is not null ? "failed" : report.Kind == "automatic" && !automatic ? "local" : "pending",
        delivery.SentAt,
        delivery.Reference,
        delivery.LastError);

    /// <summary>The hardware summary: processor, graphics with driver, board and BIOS, memory, drives (no serial numbers or names).</summary>
    private async Task<IReadOnlyList<string>> HardwareAsync(CancellationToken cancellationToken)
    {
        try
        {
            var overview = await systemReader.OverviewAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
            string? Info(string prefix) => overview.Information.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.EndsWith(".name", StringComparison.Ordinal)).Value
                ?? overview.Information.GetValueOrDefault(prefix);
            var lines = new List<string>();
            if (Info("cpu") is { } cpu)
            {
                lines.Add($"Processor: {cpu}");
            }

            if (overview.Information.GetValueOrDefault("memory.total_bytes") is { } memory && long.TryParse(memory, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
            {
                lines.Add($"Memory: {bytes / (1024 * 1024 * 1024)} GB");
            }

            var newest = overview.Devices.Count > 0 ? overview.Devices.Max(d => d.LastSeen) : 0;
            foreach (var gpu in overview.Devices.Where(d => d.Kind == "gpu" && newest - d.LastSeen < 86_400_000))
            {
                var driver = overview.Drivers.FirstOrDefault(d => string.Equals(d.DeviceName, gpu.Name, StringComparison.OrdinalIgnoreCase))?.Version;
                lines.Add($"Graphics: {gpu.Name}{(driver is null ? string.Empty : $" (driver {driver})")}");
            }

            foreach (var disk in overview.Devices.Where(d => d.Kind == "disk" && newest - d.LastSeen < 86_400_000))
            {
                lines.Add($"Drive: {disk.Name}");
            }

            var board = string.Join(' ', BoardKeys.Select(k => overview.Information.GetValueOrDefault(k)).Where(v => !string.IsNullOrEmpty(v)));
            if (board.Length > 0)
            {
                lines.Add($"Motherboard: {board}{(overview.Information.GetValueOrDefault("bios.version") is { } bios ? $", BIOS {bios}" : string.Empty)}");
            }

            if (overview.Information.GetValueOrDefault("os.name") is { } os)
            {
                lines.Add($"Windows: {os} {overview.Information.GetValueOrDefault("os.display_version")} (build {overview.Information.GetValueOrDefault("os.build")}.{overview.Information.GetValueOrDefault("os.ubr")})");
            }

            return lines;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return [];
        }
    }

    private static readonly string[] BoardKeys = ["board.manufacturer", "board.product"];

    private sealed record Accepted(string? Id, string? Reference);

    /// <summary>A browser error carried as an exception, so it is reported like the others.</summary>
    private sealed class ClientError(string message, string? stack) : Exception(message)
    {
        public override string? StackTrace => stack;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatic problem reports turned {State}")]
    private static partial void LogConsentCore(ILogger logger, string state);

    private static void LogConsent(ILogger logger, bool automatic) => LogConsentCore(logger, automatic ? "on" : "off");

    [LoggerMessage(Level = LogLevel.Information, Message = "Problem report sent ({Kind}): {Summary} ({Reference})")]
    private static partial void LogSent(ILogger logger, string kind, string summary, string? reference);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Problem report not sent: {Summary}: {Problem}")]
    private static partial void LogNotSent(ILogger logger, string summary, string problem);
}

/// <summary>Sends the automatic reports that are due, every few minutes (only with the user's consent).</summary>
public sealed class ProblemReportSender(ProblemReportService reports, TimeProvider time) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2), time);
            do
            {
                await reports.SendDueAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}

/// <summary>The last lines of the recorder's and the dashboard's logs (compact JSON), redacted, oldest first.</summary>
public static class LogTail
{
    /// <summary>Reads the newest lines of today's (or the latest) agent and web logs.</summary>
    public static IReadOnlyList<ProblemLogLine> Read(string logsDirectory, int count)
    {
        if (!Directory.Exists(logsDirectory))
        {
            return [];
        }

        var lines = new List<ProblemLogLine>();
        foreach (var app in new[] { "agent", "web" })
        {
            var file = new DirectoryInfo(logsDirectory).EnumerateFiles($"{app}-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file is not null)
            {
                lines.AddRange(Tail(file.FullName, count).Select(l => Parse(l, app)).OfType<ProblemLogLine>());
            }
        }

        return lines.OrderBy(l => l.At).TakeLast(count).ToList();
    }

    private static List<string> Tail(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - (256 * 1024));
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return [.. all.Skip(start > 0 ? 1 : 0).TakeLast(count)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A compact JSON log line: its time, level, source and message with the values filled in.</summary>
    internal static ProblemLogLine? Parse(string line, string app)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var at = root.TryGetProperty("@t", out var t) && t.TryGetDateTimeOffset(out var time) ? time : DateTimeOffset.MinValue;
            var level = root.TryGetProperty("@l", out var l) ? l.GetString() ?? "Information" : "Information";
            var template = root.TryGetProperty("@m", out var m) ? m.GetString() : root.TryGetProperty("@mt", out var mt) ? mt.GetString() : null;
            var source = root.TryGetProperty("SourceContext", out var s) ? s.GetString() ?? app : app;
            var message = System.Text.RegularExpressions.Regex.Replace(template ?? string.Empty, @"\{(@?)([A-Za-z0-9_]+)(:[^}]*)?\}", match =>
                root.TryGetProperty(match.Groups[2].Value, out var value) ? (value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText()) : match.Value,
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            if (root.TryGetProperty("@x", out var x) && x.GetString() is { } exception)
            {
                message += " | " + exception.Split('\n')[0];
            }

            var dot = source.LastIndexOf('.');
            return new ProblemLogLine(at, level, $"{app}/{(dot >= 0 ? source[(dot + 1)..] : source)}", ReportRedactor.Redact(message));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
