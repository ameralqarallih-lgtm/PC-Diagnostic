using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Events;
using PcDiag.Core.Stability;
using PcDiag.Engine.Events;
using PcDiag.Platform.Windows.EventLog;

namespace PcDiag.Collectors.Stability;

/// <summary>
/// The Stability tab's facts, from Windows itself: every time it went down uncleanly in the period (Kernel-Power 41 at the next
/// start, with the bug check and dump from WER 1001 and the moment it went down from EventLog 6008), what each dump points at,
/// how many dumps Windows keeps, and Windows Memory Diagnostic's results. Windows logged all 12 crashes of the dev PC's last
/// 30 days (Nov 2026) while the recorder caught 5. Run by the elevated recorder: the dumps and the dump setting need it.
/// </summary>
public sealed partial class StabilityService(ISystemEventSink events, TimeProvider time, ILogger<StabilityService> logger) : IStability
{
    private const string CrashControl = @"SYSTEM\CurrentControlSet\Control\CrashControl";
    private const string KernelPower = "Microsoft-Windows-Kernel-Power";
    private const string ErrorReporting = "Microsoft-Windows-WER-SystemErrorReporting";
    private const string MemoryResults = "Microsoft-Windows-MemoryDiagnostics-Results";

    /// <inheritdoc />
    public Task<StabilityReport> ReportAsync(int days, CancellationToken cancellationToken) => Task.Run(() => Report(Math.Clamp(days, 1, 365), cancellationToken), cancellationToken);

    /// <inheritdoc />
    public string? KeepDumps(int count)
    {
        if (count is < BugChecks.WindowsDefault or > BugChecks.MaxDumpsKept)
        {
            return $"Choose between {BugChecks.WindowsDefault} and {BugChecks.MaxDumpsKept} dumps.";
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CrashControl, writable: true);
            if (key is null)
            {
                return "Windows' crash settings were not found.";
            }

            key.SetValue("MinidumpsCount", count, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return "Changing it needs the recorder to run as administrator.";
        }

        Audit("You changed how many crash dumps Windows keeps", $"Windows now keeps the last {count} small crash dumps (MinidumpsCount).", "dumps");
        return null;
    }

    /// <inheritdoc />
    public string? OpenMemoryTest()
    {
        var program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "MdSched.exe");
        if (!File.Exists(program))
        {
            return "Windows Memory Diagnostic is not on this PC.";
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(program) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            return $"Windows Memory Diagnostic could not be opened: {ex.Message}";
        }

        Audit("You opened Windows Memory Diagnostic", "Windows Memory Diagnostic was opened to test the memory at the next start.", "memory-test");
        return null;
    }

    /// <summary>Pairs Windows' events into crashes, newest first. Exposed for tests (with <paramref name="dumps"/> standing in for the files).</summary>
    internal static List<CrashRecord> Crashes(IReadOnlyList<EventRecordData> records, Func<string, Minidump.Facts?> dumps, Func<string, bool> exists, int dumpsKept)
    {
        var power = records.Where(r => r.Provider == KernelPower && r.EventId == 41).OrderBy(r => r.Timestamp).ToList();
        var reports = records.Where(r => r.Provider == ErrorReporting && r.EventId == 1001).OrderBy(r => r.Timestamp).ToList();
        var shutdowns = records.Where(r => r.EventId == 6008 && r.Provider.Equals("EventLog", StringComparison.OrdinalIgnoreCase)).ToList();
        var used = new HashSet<EventRecordData>();
        var crashes = new List<CrashRecord>();
        foreach (var start in power)
        {
            // Windows logs these at the next start; the bug check report can come some minutes later.
            var report = reports.FirstOrDefault(r => !used.Contains(r) && r.Timestamp >= start.Timestamp.AddMinutes(-2) && r.Timestamp <= start.Timestamp.AddMinutes(30));
            if (report is not null)
            {
                used.Add(report);
            }

            var shutdown = shutdowns.FirstOrDefault(s => (s.Timestamp - start.Timestamp).Duration() <= TimeSpan.FromMinutes(5));
            var code = BugChecks.Parse(start.Get("BugcheckCode")) is { } c and not 0 ? c : Code(report);
            var pressed = start.Get("PowerButtonTimestamp") is { } stamp && stamp != "0";
            crashes.Add(Crash(start.Timestamp, WentDown(shutdown?.BinaryHex), code, report, pressed, dumps, exists, dumpsKept,
                code is not null && report is null ? Enumerable.Range(1, 4).Select(i => start.Get($"BugcheckParameter{i}") ?? "0x0").ToList() : null));
        }

        // A bug check report without its Kernel-Power event (the event log was cleared in between, say) is a crash too.
        foreach (var report in reports.Where(r => !used.Contains(r)))
        {
            crashes.Add(Crash(report.Timestamp, null, Code(report), report, false, dumps, exists, dumpsKept, null));
        }

        return [.. crashes.OrderByDescending(c => c.Restarted)];
    }

    /// <summary>The moment Windows went down, from EventLog 6008's data: two SYSTEMTIMEs, local and then UTC. Exposed for tests.</summary>
    internal static DateTimeOffset? WentDown(string? binaryHex)
    {
        if (binaryHex is not { Length: >= 64 })
        {
            return null;
        }

        try
        {
            // SYSTEMTIME: year, month, day of the week, day, hour, minute, second, milliseconds (16-bit each); the UTC one second.
            var bytes = Convert.FromHexString(binaryHex.AsSpan(0, 64));
            int Utc(int field) => bytes[16 + (2 * field)] | (bytes[16 + (2 * field) + 1] << 8);
            return new DateTimeOffset(Utc(0), Utc(1), Utc(3), Utc(4), Utc(5), Utc(6), Utc(7), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static uint? Code(EventRecordData? report) => report?.At(0)?.Split(' ', 2)[0] is { } text ? BugChecks.Parse(text) : null;

    private static CrashRecord Crash(DateTimeOffset restarted, DateTimeOffset? wentDown, uint? code, EventRecordData? report, bool pressed,
        Func<string, Minidump.Facts?> dumps, Func<string, bool> exists, int dumpsKept, IReadOnlyList<string>? fromPower)
    {
        if (code is not { } bugCheck)
        {
            return pressed
                ? new CrashRecord(restarted, wentDown, CrashKind.PowerButton, null, null, null, null, false, null,
                    "No blue screen: the PC stopped responding and was turned off with the power button. A freeze like this is most often hardware (memory, power, heat) or a driver that hung the whole system; there is no dump to say which.")
                : new CrashRecord(restarted, wentDown, CrashKind.Unexpected, null, null, null, null, false, null,
                    "No blue screen and no power button: the power went, the PC was reset, or it froze and ended another way. There is no dump.");
        }

        var parameters = fromPower ?? Parameters(report?.At(0));
        var dump = report?.At(1) is { Length: > 0 } file ? file : null;
        var kept = dump is not null && exists(dump);
        var facts = kept ? dumps(dump!) : null;
        string? module = null;
        if (facts is not null && BugChecks.AddressParameter(bugCheck) is { } index && index <= facts.Parameters.Count)
        {
            module = facts.ModuleAt(facts.Parameters[index - 1]);
        }

        var described = BugChecks.Describe(bugCheck);
        var explanation = new List<string> { described?.Meaning ?? "A bug check this page does not describe; search for its code." };
        if (module is not null)
        {
            explanation.Add(module.Equals("ntoskrnl.exe", StringComparison.OrdinalIgnoreCase)
                ? "It happened inside Windows' own kernel (ntoskrnl.exe). That rarely means Windows is at fault: a driver or faulty memory can damage what the kernel uses earlier, so the dump cannot name the cause. Kernel crashes again and again, with different codes, point to hardware: test the memory and check the BIOS and its memory settings."
                : $"It happened inside {module}: look there first (update or roll back what it belongs to).");
        }
        else if (dump is not null && !kept)
        {
            explanation.Add($"Its dump is gone: Windows keeps only the last {dumpsKept}.");
        }

        return new CrashRecord(restarted, wentDown, CrashKind.BlueScreen, BugChecks.Format(bugCheck), described?.Name, parameters, dump, kept, module, string.Join(" ", explanation));
    }

    /// <summary>"0x0000007e (0xffffffffc0000420, 0xfffff804b6a6291c, …)": the parameters in the brackets.</summary>
    private static List<string>? Parameters(string? text)
    {
        var open = text?.IndexOf('(', StringComparison.Ordinal) ?? -1;
        var close = text?.LastIndexOf(')') ?? -1;
        return open >= 0 && close > open ? [.. text![(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)] : null;
    }

    private StabilityReport Report(int days, CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow() - TimeSpan.FromDays(days);
        var records = new List<EventRecordData>();
        string? problem = null;
        try
        {
            EventLogQueries.Read("System", Query(TimeSpan.FromDays(days)), view =>
            {
                if (EventXml.Parse(view.ToXml()) is { } record)
                {
                    records.Add(record);
                }
            }, cancellationToken);
        }
        catch (EventChannelException ex)
        {
            problem = $"Windows' System log could not be read ({ex.Problem}).";
            LogReadFailed(logger, ex);
        }

        var dumps = Dumps();
        var crashes = Crashes(records, Minidump.Read, File.Exists, dumps?.Kept ?? BugChecks.WindowsDefault);
        var tests = records.Where(r => r.Provider == MemoryResults)
            .Select(r => new MemoryTestResult(r.Timestamp, r.Level is 1 or 2 or 3 || r.EventId is 1102 or 1202,
                r.Level is 1 or 2 or 3 || r.EventId is 1102 or 1202 ? "Windows Memory Diagnostic found errors in the memory." : "Windows Memory Diagnostic found no errors."))
            .OrderByDescending(t => t.At).ToList();
        var logStarts = Oldest();
        return new StabilityReport(since, logStarts > since ? logStarts : null, crashes, dumps, tests, problem);
    }

    private static string Query(TimeSpan within) => string.Create(CultureInfo.InvariantCulture,
        $"""
        <QueryList><Query Id="0" Path="System"><Select Path="System">*[System[((Provider[@Name='{KernelPower}'] and EventID=41) or (Provider[@Name='{ErrorReporting}'] and EventID=1001) or (Provider[@Name='EventLog'] and EventID=6008) or Provider[@Name='{MemoryResults}']) and TimeCreated[timediff(@SystemTime) &lt;= {(long)within.TotalMilliseconds}]]]</Select></Query></QueryList>
        """);

    /// <summary>When the System log starts (it is overwritten when full, so it may not reach back as far as asked).</summary>
    private static DateTimeOffset? Oldest()
    {
        try
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName));
            using var first = reader.ReadEvent();
            return first?.TimeCreated is { } created ? new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is System.Diagnostics.Eventing.Reader.EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DumpSettings? Dumps()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CrashControl);
            var count = key?.GetValue("MinidumpsCount") is int n ? n : (int?)null;
            var folder = Environment.ExpandEnvironmentVariables(key?.GetValue("MinidumpDir") as string ?? @"%SystemRoot%\Minidump");
            var files = Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles("*.dmp") : [];
            return new DumpSettings(count ?? BugChecks.WindowsDefault, count is not null, folder, files.Length, files.Sum(f => f.Length));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return null;
        }
    }

    private void Audit(string title, string message, string rule)
    {
        LogChanged(logger, title);
        events.Publish(new SystemEvent(time.GetUtcNow(), "pcdiag.control", null, "PcDiagnostic", 1, EventLevel.Information, EventCategory.Other, Severity.Low, title, message,
            RuleId: $"pcdiag.control.{rule}"));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stability: {Change}")]
    private static partial void LogChanged(ILogger logger, string change);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The System log could not be read for the Stability tab")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
}
