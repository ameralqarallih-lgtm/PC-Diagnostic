using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using static PcDiag.Platform.Windows.Tracing.EtwNative;

namespace PcDiag.Platform.Windows.Tracing;

/// <summary>
/// Driver latency, as LatencyMon measures it: a kernel trace (a private system logger session) of every DPC and ISR
/// for a few seconds. Each event says when its routine was entered and is logged when it returns, so its run time is
/// the difference; the routine's address is put in the kernel module that holds it (the loaded drivers' addresses come
/// from Windows' module list, read with the debug privilege, which Windows 11 24H2 asks for). Only the routine address
/// and the two times are read. Needs administrator rights; one measurement at a time.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class EtwDriverLatency(ElevationInfo elevation, TimeProvider time) : IDriverLatencyTracer
{
    /// <summary>The session name (stopped first if a previous run left it behind).</summary>
    public const string SessionName = "PcDiagnostic-DriverLatency";

    /// <summary>The kernel's PerfInfo events (DPCs and ISRs are among them).</summary>
    internal static readonly Guid PerfInfo = new("CE1DBFB4-137E-4DA6-87B0-3F59AA102CBC");

    private const uint SystemLoggerMode = 0x02000000;
    private const uint FlagDpc = 0x00000020;
    private const uint FlagInterrupt = 0x00000040;

    private int _running;

    /// <inheritdoc />
    public string? Problem => elevation.IsElevated ? null : "Measuring driver latency needs the recorder to run as administrator.";

    /// <summary>A DPC (threaded 66, normal 68, timer 69) or an ISR (67, and 50 for message-signaled interrupts).</summary>
    internal static (bool Known, bool Isr) Kind(byte opcode) => opcode switch
    {
        66 or 68 or 69 => (true, false),
        67 or 50 => (true, true),
        _ => (false, false),
    };

    /// <inheritdoc />
    public Task<DriverLatencySample> MeasureAsync(TimeSpan duration, string reason, CancellationToken cancellationToken)
    {
        var from = time.GetUtcNow();
        if (Problem is { } problem)
        {
            return Task.FromResult(new DriverLatencySample(from, from, 0, reason, [], problem));
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return Task.FromResult(new DriverLatencySample(from, from, 0, reason, [], "Another driver latency measurement is running."));
        }

        // Started whatever the token says, so the flag is always cleared; the token ends the measurement early.
        return Task.Factory.StartNew(
            () =>
            {
                try
                {
                    return Measure(duration, reason, cancellationToken);
                }
                finally
                {
                    Volatile.Write(ref _running, 0);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private DriverLatencySample Measure(TimeSpan duration, string reason, CancellationToken cancellationToken)
    {
        var from = time.GetUtcNow();
        var counter = new DriverLatencyCounter(Stopwatch.Frequency);
        StopSession(SessionName);
        var status = Start(out var session);
        if (status != 0)
        {
            return new DriverLatencySample(from, from, 0, reason, [], $"The kernel trace could not start (error {status}).");
        }

        var self = GCHandle.Alloc(counter);
        ulong trace;
        var name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            var logfile = new EventTraceLogfile
            {
                LoggerName = name,
                ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp,
                EventRecordCallback = (nint)(delegate* unmanaged<EventRecord*, void>)&OnEvent,
                Context = GCHandle.ToIntPtr(self),
            };
            trace = OpenTraceW(&logfile);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }

        if (trace == ulong.MaxValue)
        {
            StopSession(SessionName);
            self.Free();
            return new DriverLatencySample(from, from, 0, reason, [], "The kernel trace could not be read.");
        }

        var consumer = new Thread(() =>
        {
            var handle = trace;
            _ = ProcessTrace(&handle, 1, null, null); // returns when the session stops
        })
        { IsBackground = true, Name = "PcDiag ETW driver latency" };
        consumer.Start();
        var started = Stopwatch.GetTimestamp();
        _ = cancellationToken.WaitHandle.WaitOne(duration);
        var lost = StopAndCountLost(session);
        _ = CloseTrace(trace);
        consumer.Join(TimeSpan.FromSeconds(10));
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        self.Free();

        var modules = KernelModules();
        var drivers = counter.ByDriver(modules);
        string? problem = modules.Count == 0 ? "The loaded drivers could not be listed, so the DPCs and ISRs could not be put in their drivers."
            : counter.Events == 0 ? "The kernel trace delivered no DPC or ISR events."
            : lost > 0 ? $"The kernel trace lost {lost} events (the PC was very busy); the longest times are still measured from those it kept."
            : null;
        return new DriverLatencySample(time.GetUtcNow(), from, Math.Round(seconds, 1), reason, drivers, problem);
    }

    private static uint Start(out ulong session)
    {
        var buffer = Properties(out var props);
        try
        {
            props->LogFileMode = EventTraceRealTimeMode | SystemLoggerMode;
            props->EnableFlags = FlagDpc | FlagInterrupt;
            props->BufferSize = 256;
            props->MinimumBuffers = 16;
            props->MaximumBuffers = 64;
            props->FlushTimer = 1;
            fixed (ulong* handle = &session)
            {
                return StartTraceW(handle, SessionName, props);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Stops the session and returns how many events it lost.</summary>
    private static uint StopAndCountLost(ulong session)
    {
        var buffer = Properties(out var props);
        try
        {
            _ = ControlTraceW(session, SessionName, props, EventTraceControlStop);
            return props->EventsLost + props->RealTimeBuffersLost;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(EventRecord* record)
    {
        var header = record->EventHeader;
        if (record->UserContext == 0 || header.ProviderId != PerfInfo || record->UserDataLength < 16 || record->UserData == 0)
        {
            return;
        }

        var (known, isr) = Kind(header.Opcode);
        if (!known || GCHandle.FromIntPtr(record->UserContext).Target is not DriverLatencyCounter counter)
        {
            return;
        }

        // InitialTime (when the routine was entered, in the session's clock: QPC), then the routine's address. The event
        // is logged as the routine returns, so its own time stamp is the end.
        var data = (byte*)record->UserData;
        var initial = *(long*)data;
        var routine = *(ulong*)(data + 8);
        counter.Add(isr, routine, initial, header.TimeStamp);
    }

    /// <summary>
    /// The loaded kernel modules with their addresses (NtQuerySystemInformation's module list). Windows 11 24H2 gives
    /// the addresses only to a caller holding the debug privilege, so it is enabled for the call and put back after.
    /// Empty when the list cannot be read.
    /// </summary>
    internal static List<KernelModule> KernelModules()
    {
        var restore = EnableDebugPrivilege();
        try
        {
            var size = 256 * 1024;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQuerySystemInformation(SystemModuleInformation, buffer, (uint)size, out var needed);
                    if (status == StatusInfoLengthMismatch)
                    {
                        size = (int)Math.Max(needed + 4096, (uint)size * 2);
                        continue;
                    }

                    return status == 0 ? ParseModules(new ReadOnlySpan<byte>((void*)buffer, size)) : [];
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return [];
        }
        finally
        {
            restore?.Invoke();
        }
    }

    /// <summary>
    /// RTL_PROCESS_MODULES on x64: the count (UInt32, padded to 8), then 296-byte entries: Section, MappedBase,
    /// ImageBase (8 bytes each), ImageSize (UInt32), Flags, LoadOrderIndex, InitOrderIndex, LoadCount,
    /// OffsetToFileName (UInt16), FullPathName (256 bytes, ANSI).
    /// </summary>
    internal static List<KernelModule> ParseModules(ReadOnlySpan<byte> data)
    {
        const int entrySize = 296;
        var modules = new List<KernelModule>();
        if (data.Length < 8)
        {
            return modules;
        }

        var count = BitConverter.ToUInt32(data);
        for (var i = 0; i < count && 8 + ((i + 1) * entrySize) <= data.Length; i++)
        {
            var entry = data.Slice(8 + (i * entrySize), entrySize);
            var imageBase = BitConverter.ToUInt64(entry[16..]);
            var imageSize = BitConverter.ToUInt32(entry[24..]);
            var nameOffset = BitConverter.ToUInt16(entry[38..]);
            var path = entry.Slice(40, 256);
            var end = path.IndexOf((byte)0);
            var full = Encoding.ASCII.GetString(end < 0 ? path : path[..end]);
            var file = nameOffset < full.Length ? full[nameOffset..] : Path.GetFileName(full);
            modules.Add(new KernelModule(imageBase, imageSize, file));
        }

        return modules;
    }

    /// <summary>Enables the debug privilege for this process; returns how to put it back (null when nothing changed).</summary>
    private static Action? EnableDebugPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return null;
        }

        if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid))
        {
            _ = CloseHandle(token);
            return null;
        }

        var wanted = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
        var previous = default(TokenPrivileges);
        if (!AdjustTokenPrivileges(token, false, ref wanted, (uint)sizeof(TokenPrivileges), ref previous, out _) || Marshal.GetLastPInvokeError() != 0)
        {
            _ = CloseHandle(token);
            return null;
        }

        return () =>
        {
            // The privilege was already on when the previous state lists none.
            if (previous.Count > 0)
            {
                var dummy = default(TokenPrivileges);
                _ = AdjustTokenPrivileges(token, false, ref previous, (uint)sizeof(TokenPrivileges), ref dummy, out _);
            }

            _ = CloseHandle(token);
        };
    }

    private const int SystemModuleInformation = 11;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    // TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES: the LUID is two 32-bit halves, so it sits at offset 4 (16 bytes in all).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("ntdll.dll")]
    private static partial uint NtQuerySystemInformation(int infoClass, nint buffer, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint length, ref TokenPrivileges previous, out uint returnLength);
}
