using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Processes;

/// <summary>
/// One process from SystemProcessInformation. <see cref="Name"/> points into the snapshot buffer and is valid only
/// during the visit. Times are in 100 ns units; CreateTime is a FILETIME (UTC).
/// </summary>
public readonly ref struct RawProcess
{
    /// <summary>Process ID.</summary>
    public int Pid { get; init; }

    /// <summary>Parent (creator) process ID. The parent may have exited, and its PID may have been reused.</summary>
    public int ParentPid { get; init; }

    /// <summary>Creation time as a FILETIME (UTC, 100 ns since 1601).</summary>
    public long CreateTime { get; init; }

    /// <summary>User-mode CPU time, 100 ns units.</summary>
    public long UserTime { get; init; }

    /// <summary>Kernel-mode CPU time, 100 ns units.</summary>
    public long KernelTime { get; init; }

    /// <summary>Thread count.</summary>
    public int Threads { get; init; }

    /// <summary>Handle count.</summary>
    public int Handles { get; init; }

    /// <summary>Base priority.</summary>
    public int BasePriority { get; init; }

    /// <summary>Terminal Services session.</summary>
    public int SessionId { get; init; }

    /// <summary>Working set bytes.</summary>
    public long WorkingSet { get; init; }

    /// <summary>Private working set bytes.</summary>
    public long PrivateWorkingSet { get; init; }

    /// <summary>Private commit (PagefileUsage) bytes.</summary>
    public long Commit { get; init; }

    /// <summary>Total bytes read by all I/O.</summary>
    public long ReadTransfer { get; init; }

    /// <summary>Total bytes written by all I/O.</summary>
    public long WriteTransfer { get; init; }

    /// <summary>Image name (no path); empty for the System Idle Process.</summary>
    public ReadOnlySpan<char> Name { get; init; }
}

/// <summary>Receives one process during <see cref="NtProcesses.Visit"/>.</summary>
public delegate void RawProcessVisitor<TState>(ref TState state, in RawProcess process);

/// <summary>
/// Reads all processes with one NtQuerySystemInformation(SystemProcessInformation) call into a reused buffer.
/// The documented part of SYSTEM_PROCESS_INFORMATION (winternl.h) marks create time, CPU times, parent PID and I/O
/// counters as reserved. Their offsets have been stable since Windows XP and are verified at runtime against
/// System.Diagnostics.Process (see the integration tests and docs/spikes/2026-09-29-process-information.md).
/// </summary>
public sealed partial class NtProcesses
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // SYSTEM_PROCESS_INFORMATION offsets (x64).
    private const int OffNextEntry = 0;
    private const int OffThreads = 4;
    private const int OffPrivateWorkingSet = 8;
    private const int OffCreateTime = 32;
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffNameLength = 56;
    private const int OffNameBuffer = 64;
    private const int OffBasePriority = 72;
    private const int OffPid = 80;
    private const int OffParentPid = 88;
    private const int OffHandles = 96;
    private const int OffSessionId = 100;
    private const int OffWorkingSet = 144;
    private const int OffPagefileUsage = 184;
    private const int OffReadTransfer = 232;
    private const int OffWriteTransfer = 240;

    private byte[] _buffer = new byte[1024 * 1024];

    /// <summary>Takes a snapshot and visits every process. Returns false if the call failed.</summary>
    public unsafe bool Visit<TState>(ref TState state, RawProcessVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        int status;
        for (var attempt = 0; ; attempt++)
        {
            fixed (byte* buffer = _buffer)
            {
                status = NtQuerySystemInformation(SystemProcessInformation, buffer, _buffer.Length, out var needed);
                if (status == StatusInfoLengthMismatch && attempt < 4)
                {
                    _buffer = new byte[Math.Max(needed, _buffer.Length) + (256 * 1024)]; // processes can start in between
                    continue;
                }
            }

            break;
        }

        if (status < 0)
        {
            return false;
        }

        fixed (byte* start = _buffer)
        {
            var entry = start;
            while (true)
            {
                var nameLength = *(ushort*)(entry + OffNameLength);
                var nameBuffer = *(char**)(entry + OffNameBuffer);
                var process = new RawProcess
                {
                    Pid = (int)*(nint*)(entry + OffPid),
                    ParentPid = (int)*(nint*)(entry + OffParentPid),
                    CreateTime = *(long*)(entry + OffCreateTime),
                    UserTime = *(long*)(entry + OffUserTime),
                    KernelTime = *(long*)(entry + OffKernelTime),
                    Threads = *(int*)(entry + OffThreads),
                    Handles = *(int*)(entry + OffHandles),
                    BasePriority = *(int*)(entry + OffBasePriority),
                    SessionId = *(int*)(entry + OffSessionId),
                    WorkingSet = (long)*(nuint*)(entry + OffWorkingSet),
                    PrivateWorkingSet = *(long*)(entry + OffPrivateWorkingSet),
                    Commit = (long)*(nuint*)(entry + OffPagefileUsage),
                    ReadTransfer = *(long*)(entry + OffReadTransfer),
                    WriteTransfer = *(long*)(entry + OffWriteTransfer),
                    Name = nameBuffer == null ? [] : new ReadOnlySpan<char>(nameBuffer, nameLength / 2),
                };
                visitor(ref state, process);

                var next = *(uint*)(entry + OffNextEntry);
                if (next == 0)
                {
                    break;
                }

                entry += next;
            }
        }

        return true;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQuerySystemInformation(int informationClass, void* information, int length, out int returnLength);
}
