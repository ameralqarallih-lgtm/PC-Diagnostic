using System.Globalization;

namespace PcDiag.Core.Stability;

/// <summary>How Windows went down.</summary>
public enum CrashKind
{
    /// <summary>A bug check (blue screen).</summary>
    BlueScreen,

    /// <summary>No bug check, and the power button was pressed: the PC most likely froze and was turned off.</summary>
    PowerButton,

    /// <summary>No bug check and no power button: a power loss, a reset, or a freeze that ended another way.</summary>
    Unexpected,
}

/// <summary>One time Windows went down without shutting down cleanly, from its own log.</summary>
/// <param name="Restarted">When Windows started again (it logs the crash then).</param>
/// <param name="WentDown">When it went down, when Windows recorded it (EventLog 6008).</param>
/// <param name="BugCheck">The bug check code ("0x0000001E"), for a blue screen.</param>
/// <param name="BugCheckName">Its name ("KMODE_EXCEPTION_NOT_HANDLED").</param>
/// <param name="Parameters">The bug check's four parameters.</param>
/// <param name="DumpFile">The dump Windows wrote, as it named it.</param>
/// <param name="DumpKept">The dump is still there (Windows deletes the oldest beyond its limit).</param>
/// <param name="Module">The module the bug check points at (from the dump), e.g. "ntoskrnl.exe" or "nvlddmkm.sys".</param>
/// <param name="Explanation">What it means, in plain words, and what the dump can and cannot say.</param>
/// <param name="IncidentKey">PC Diagnostic's incident for it, when there is one.</param>
public sealed record CrashRecord(
    DateTimeOffset Restarted,
    DateTimeOffset? WentDown,
    CrashKind Kind,
    string? BugCheck,
    string? BugCheckName,
    IReadOnlyList<string>? Parameters,
    string? DumpFile,
    bool DumpKept,
    string? Module,
    string Explanation,
    string? IncidentKey = null);

/// <summary>How many crash dumps Windows keeps, and what is there now.</summary>
/// <param name="Kept">How many small dumps Windows keeps (MinidumpsCount; 5 when not set).</param>
/// <param name="Set">Someone set the number (otherwise Windows' default).</param>
/// <param name="Folder">Where they are.</param>
/// <param name="Files">Dumps there now.</param>
/// <param name="Bytes">Their size.</param>
public sealed record DumpSettings(int Kept, bool Set, string Folder, int Files, long Bytes);

/// <summary>A Windows Memory Diagnostic result.</summary>
public sealed record MemoryTestResult(DateTimeOffset At, bool ErrorsFound, string Message);

/// <summary>The Stability tab: crashes from Windows' log, the dump setting and memory test results.</summary>
/// <param name="Since">The oldest time looked at (Windows' System log may not go back that far).</param>
/// <param name="LogStarts">Where the System log starts, when that is later than <paramref name="Since"/>.</param>
public sealed record StabilityReport(
    DateTimeOffset Since,
    DateTimeOffset? LogStarts,
    IReadOnlyList<CrashRecord> Crashes,
    DumpSettings? Dumps,
    IReadOnlyList<MemoryTestResult> MemoryTests,
    string? Problem = null);

/// <summary>Reads how stable the PC has been and changes what helps find out why (run by the elevated recorder).</summary>
public interface IStability
{
    /// <summary>Crashes in the last <paramref name="days"/> days (newest first), the dump setting and memory tests.</summary>
    Task<StabilityReport> ReportAsync(int days, CancellationToken cancellationToken);

    /// <summary>Sets how many small dumps Windows keeps; returns why not, or null when done.</summary>
    string? KeepDumps(int count);

    /// <summary>Opens Windows Memory Diagnostic, which asks to restart now or at the next start; returns why not, or null.</summary>
    string? OpenMemoryTest();
}

/// <summary>Bug check codes in plain words: the common ones, with what their parameters point at.</summary>
public static class BugChecks
{
    /// <summary>Most dumps a user may ask Windows to keep.</summary>
    public const int MaxDumpsKept = 250;

    /// <summary>Windows' default number of small dumps.</summary>
    public const int WindowsDefault = 5;

    private static readonly Dictionary<uint, (string Name, string Meaning, int? AddressParameter)> Known = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "A driver or Windows touched memory it may not touch at that moment. Usually a driver; sometimes faulty memory.", 4),
        [0x19] = ("BAD_POOL_HEADER", "Windows' kernel memory was damaged, most often by a driver writing past what it owns.", null),
        [0x1A] = ("MEMORY_MANAGEMENT", "Windows found its memory bookkeeping corrupted. Often faulty or unstable RAM (test it), sometimes a driver.", null),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "Code in the kernel failed and nothing caught it: an invalid instruction or memory access.", 2),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "A system call failed inside the kernel or a driver.", 2),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "Memory that must always be present was missing: a driver bug, or faulty RAM.", 3),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A system thread failed and nothing caught it.", 2),
        [0x8E] = ("KERNEL_MODE_EXCEPTION_NOT_HANDLED", "Code in the kernel failed and nothing caught it.", 2),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "A driver did not finish going to sleep or waking up in time.", null),
        [0xA0] = ("INTERNAL_POWER_ERROR", "Windows' power management failed (sleep, hibernation).", null),
        [0xC2] = ("BAD_POOL_CALLER", "A driver asked for or freed kernel memory the wrong way.", null),
        [0xC5] = ("DRIVER_CORRUPTED_EXPOOL", "A driver damaged kernel memory.", null),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "A driver touched memory it may not touch at that moment.", 4),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "A process Windows cannot run without ended.", null),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "A processor core stopped answering. Often a hardware or firmware problem (CPU, its power, BIOS settings).", null),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", "Kernel code or data was changed: a driver, or faulty memory.", null),
        [0x116] = ("VIDEO_TDR_FAILURE", "The graphics driver stopped responding and could not be restarted.", 2),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "The graphics driver stopped responding.", 2),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "The graphics scheduler found an error, usually in the graphics driver.", null),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "The hardware reported an error it could not correct (CPU, memory, PCIe). A hardware or overclocking problem, not a driver.", null),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "A driver kept a processor busy for too long without letting anything else run.", null),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "The kernel found one of its structures damaged.", null),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", "Kernel memory was damaged, usually by a driver.", null),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "Memory compression or the page file failed: a disk or memory problem.", null),
        [0x1D8] = ("ATTEMPTED_SWITCH_FROM_DPC", "A driver did something not allowed in a deferred call.", null),
    };

    /// <summary>The code as Windows shows it ("0x0000001E").</summary>
    public static string Format(uint code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The code without the 0x10000000 flag that some crash paths add (0x1000007E is 0x7E).</summary>
    public static uint Base(uint code) => code & ~0x10000000u;

    /// <summary>A code's name and meaning; null when it is not one of the common ones.</summary>
    public static (string Name, string Meaning)? Describe(uint code) => Known.TryGetValue(Base(code), out var known) ? (known.Name, known.Meaning) : null;

    /// <summary>Which of the four parameters (1-4) is the address of the failing code or access, for the codes that have one.</summary>
    public static int? AddressParameter(uint code) => Known.TryGetValue(Base(code), out var known) ? known.AddressParameter : null;

    /// <summary>A code read from an event: "0x0000007e" or a number ("126").</summary>
    public static uint? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
