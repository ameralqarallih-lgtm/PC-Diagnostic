using System.Collections.Concurrent;
using System.Security.Principal;
using PcDiag.Core.Reporting;
using Serilog.Core;
using Serilog.Events;

namespace PcDiag.Hosting;

/// <summary>
/// Catches the product's own problems in this process (the recorder or the Web host): every error logged, and
/// crashes. Each becomes a problem report in the local outbox, with its cause, the log lines before it and the
/// environment, all redacted. Repeats of the same problem add to one report; at most
/// <see cref="MaxReportsPerHour"/> new reports an hour. Nothing is sent from here.
/// </summary>
public sealed class ProblemCapture : ILogEventSink
{
    /// <summary>Most new reports per hour (repeats only count up).</summary>
    public const int MaxReportsPerHour = 20;

    /// <summary>Log lines kept for context.</summary>
    public const int ContextLines = 60;

    private readonly ProblemSpool _spool;
    private readonly string _app;
    private readonly string _installId;
    private readonly bool _elevated;
    private readonly ConcurrentQueue<ProblemLogLine> _recent = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _written = new();
    private readonly Lock _rate = new();
    private readonly Queue<DateTimeOffset> _hour = new();

    /// <summary>Creates the capture for an application ("agent" or "web").</summary>
    public ProblemCapture(string reportsRoot, string app)
    {
        _spool = new ProblemSpool(reportsRoot);
        _app = app;
        _installId = ProblemFacts.InstallId(reportsRoot);
        _elevated = OperatingSystem.IsWindows() && IsElevated();
    }


    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var source = logEvent.Properties.TryGetValue("SourceContext", out var value) ? value.ToString().Trim('"') : _app;
        var message = logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);
        _recent.Enqueue(new ProblemLogLine(logEvent.Timestamp, logEvent.Level.ToString(), Short(source), ReportRedactor.Redact(message)));
        while (_recent.Count > ContextLines && _recent.TryDequeue(out _))
        {
        }

        if (logEvent.Level >= LogEventLevel.Error)
        {
            Capture(Component(source), logEvent.Exception, message, new Dictionary<string, string> { ["source"] = source, ["level"] = logEvent.Level.ToString() });
        }
    }

    /// <summary>Writes (or counts again) a report for a problem. Never throws.</summary>
    public void Capture(string component, Exception? exception, string message, IReadOnlyDictionary<string, string>? context = null)
    {
        try
        {
            var fingerprint = ReportRedactor.Fingerprint(component, exception?.GetType().FullName, exception?.Message ?? message, exception?.StackTrace);
            var now = DateTimeOffset.UtcNow;
            // A new problem counts against the hourly limit; a repeat within the hour is only counted up once a minute.
            if (_written.TryGetValue(fingerprint, out var last) && now - last < TimeSpan.FromMinutes(1))
            {
                return;
            }

            if (!_written.ContainsKey(fingerprint) && !Allowed(now))
            {
                return;
            }

            _written[fingerprint] = now;
            _spool.Write(new ProblemReport(
                fingerprint,
                "automatic",
                now,
                component,
                ReportRedactor.Summarize(component, exception, message),
                null,
                null,
                ReportRedactor.Cause(exception) ?? new ProblemCause("Error", ReportRedactor.Redact(message), null, null, null),
                new ProductFacts(ProductInfo.Name, ProductInfo.Version, _app, _elevated, _installId),
                ProblemFacts.Environment(),
                [.. _recent],
                context ?? new Dictionary<string, string>(),
                fingerprint,
                1,
                now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            // Reporting must never become a problem itself.
        }
    }

    /// <summary>Hooks crashes and unobserved task errors of this process.</summary>
    public void HookCrashes()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Capture($"{_app}/crash", e.ExceptionObject as Exception, "The process stopped because of an unhandled error.",
            new Dictionary<string, string> { ["terminating"] = e.IsTerminating.ToString() });
        TaskScheduler.UnobservedTaskException += (_, e) => Capture($"{_app}/task", e.Exception, "A background task failed without being observed.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private bool Allowed(DateTimeOffset now)
    {
        lock (_rate)
        {
            while (_hour.Count > 0 && now - _hour.Peek() > TimeSpan.FromHours(1))
            {
                _hour.Dequeue();
            }

            if (_hour.Count >= MaxReportsPerHour)
            {
                return false;
            }

            _hour.Enqueue(now);
            return true;
        }
    }

    /// <summary>"PcDiag.Collectors.Gpu.GpuCollector" → "agent/GpuCollector".</summary>
    private string Component(string source) => $"{_app}/{Short(source)}";

    private static string Short(string source)
    {
        var dot = source.LastIndexOf('.');
        return dot >= 0 && dot < source.Length - 1 ? source[(dot + 1)..] : source;
    }
}
