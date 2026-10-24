using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PcDiag.Core.Abstractions;
using static PcDiag.Platform.Windows.Tracing.EtwNative;

namespace PcDiag.Platform.Windows.Tracing;

/// <summary>
/// True per-process disk and network I/O (Enhanced mode) from a real-time ETW session on two manifest providers:
/// Microsoft-Windows-Kernel-Network (TCP/UDP send and receive, with the process ID and size in each event) and
/// Microsoft-Windows-Kernel-Disk (reads and writes, logged in the issuing process: its ID is in the event header;
/// verified with a decoded trace, where a program's write-through writes carried its own process ID and the system's
/// background writes PID 4). Events are only summed per process; nothing else is kept. Needs administrator rights.
/// Platform APIs only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class EtwProcessIo(ElevationInfo elevation) : IProcessIoMonitor, IDisposable
{
    /// <summary>The session name (stopped and restarted if a previous run left it behind).</summary>
    public const string SessionName = "PcDiagnostic-ProcessIo";

    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private static readonly Guid KernelDisk = new("C7BDE69A-E1E0-4177-B6EF-283AD1525271");

    private readonly Lock _lock = new();
    private Dictionary<int, Totals> _totals = [];
    private ulong _session;
    private ulong _trace = InvalidHandle;
    private Thread? _consumer;
    private GCHandle _self;

    private const ulong InvalidHandle = ulong.MaxValue;

    /// <inheritdoc />
    public bool Running { get; private set; }

    /// <inheritdoc />
    public string? Problem { get; private set; }

    private long _eventsSeen;

    /// <summary>Events seen (for diagnostics).</summary>
    public long EventsSeen => Interlocked.Read(ref _eventsSeen);

    /// <inheritdoc />
    public bool StartCollecting()
    {
        if (Running)
        {
            return true;
        }

        if (!elevation.IsElevated)
        {
            Problem = "Needs the recorder to run as administrator.";
            return false;
        }

        var status = StartSession(SessionName, out _session);
        if (status == ErrorAlreadyExists)
        {
            StopSession(SessionName);
            status = StartSession(SessionName, out _session);
        }

        if (status != 0)
        {
            Problem = $"The kernel event session could not start (error {status}).";
            return false;
        }

        var network = KernelNetwork;
        var disk = KernelDisk;
        var enabled = EnableTraceEx2(_session, &network, EnableProvider, TraceLevelInformation, ulong.MaxValue, 0, 0, null) == 0;
        enabled &= EnableTraceEx2(_session, &disk, EnableProvider, TraceLevelInformation, ulong.MaxValue, 0, 0, null) == 0;
        if (!enabled)
        {
            StopSession(SessionName);
            Problem = "The kernel network and disk providers could not be enabled.";
            return false;
        }

        _self = GCHandle.Alloc(this);
        var name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            var logfile = new EventTraceLogfile
            {
                LoggerName = name,
                ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord,
                EventRecordCallback = (nint)(delegate* unmanaged<EventRecord*, void>)&OnEvent,
                Context = GCHandle.ToIntPtr(_self),
            };
            _trace = OpenTraceW(&logfile);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }

        if (_trace == InvalidHandle)
        {
            StopSession(SessionName);
            _self.Free();
            Problem = "The kernel event session could not be read.";
            return false;
        }

        _consumer = new Thread(() =>
        {
            var handle = _trace;
            _ = ProcessTrace(&handle, 1, null, null); // returns when the session stops
        })
        { IsBackground = true, Name = "PcDiag ETW process I/O" };
        _consumer.Start();
        Running = true;
        Problem = null;
        return true;
    }

    /// <inheritdoc />
    public void StopCollecting()
    {
        if (!Running)
        {
            return;
        }

        Running = false;
        StopSession(SessionName);
        if (_trace != InvalidHandle)
        {
            _ = CloseTrace(_trace);
            _trace = InvalidHandle;
        }

        _consumer?.Join(TimeSpan.FromSeconds(5));
        _consumer = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }

        lock (_lock)
        {
            _totals.Clear();
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<int, ProcessIoTotals> Take()
    {
        Dictionary<int, Totals> taken;
        lock (_lock)
        {
            taken = _totals;
            _totals = [];
        }

        return taken.ToDictionary(kv => kv.Key, kv => new ProcessIoTotals(kv.Value.DiskRead, kv.Value.DiskWrite, kv.Value.NetReceive, kv.Value.NetSend));
    }

    /// <inheritdoc />
    public void Dispose() => StopCollecting();

    /// <summary>
    /// The native structure sizes on this platform (x64: EVENT_RECORD 112, EVENT_TRACE_PROPERTIES 120, EVENT_HEADER 80);
    /// checked by a test because a wrong layout would read the wrong memory.
    /// </summary>
    internal static (int EventRecord, int Properties, int Header, int UserDataOffset) Layout() =>
        (sizeof(EventRecord), sizeof(EventTraceProperties), sizeof(EventHeader), (int)Marshal.OffsetOf<EventRecord>(nameof(EventRecord.UserData)));

    [UnmanagedCallersOnly]
    private static void OnEvent(EventRecord* record)
    {
        if (record->UserContext == 0)
        {
            return;
        }

        if (GCHandle.FromIntPtr(record->UserContext).Target is EtwProcessIo self)
        {
            self.Handle(record);
        }
    }

    private void Handle(EventRecord* record)
    {
        Interlocked.Increment(ref _eventsSeen);
        if (record->UserData == 0)
        {
            return;
        }

        var data = new ReadOnlySpan<byte>((void*)record->UserData, record->UserDataLength);
        var transfer = record->EventHeader.ProviderId == KernelNetwork ? Network(record->EventHeader.Id, data)
            : record->EventHeader.ProviderId == KernelDisk ? Disk(record->EventHeader.Id, data, record->EventHeader.ProcessId)
            : null;
        if (transfer is { } t)
        {
            Add(t.Pid, t.Kind, t.Bytes);
        }
    }

    /// <summary>
    /// Kernel-Network TCP/UDP send (10, 26, 42, 58) and receive (11, 27, 43, 59) over IPv4 and IPv6: PID (UInt32),
    /// size (UInt32), then the addresses.
    /// </summary>
    internal static Transfer? Network(ushort id, ReadOnlySpan<byte> data)
    {
        var receive = id is 11 or 27 or 43 or 59;
        var send = id is 10 or 26 or 42 or 58;
        if ((!receive && !send) || data.Length < 8)
        {
            return null;
        }

        return new Transfer((int)BitConverter.ToUInt32(data), receive ? Kind.NetReceive : Kind.NetSend, BitConverter.ToUInt32(data[4..]));
    }

    /// <summary>
    /// Kernel-Disk read (10) and write (11): DiskNumber, IrpFlags, TransferSize (UInt32 at 8), Reserved, ByteOffset,
    /// FileObject, IORequestPacket, HighResResponseTime (48 bytes on x64; there is no thread field). The process is
    /// the one in the event header.
    /// </summary>
    internal static Transfer? Disk(ushort id, ReadOnlySpan<byte> data, uint headerProcessId) =>
        id is 10 or 11 && data.Length >= 12 ? new Transfer((int)headerProcessId, id == 10 ? Kind.DiskRead : Kind.DiskWrite, BitConverter.ToUInt32(data[8..])) : null;

    /// <summary>Bytes one process moved, from one event.</summary>
    internal readonly record struct Transfer(int Pid, Kind Kind, uint Bytes);

    private void Add(int pid, Kind kind, uint bytes)
    {
        lock (_lock)
        {
            if (!_totals.TryGetValue(pid, out var totals))
            {
                totals = new Totals();
                _totals[pid] = totals;
            }

            switch (kind)
            {
                case Kind.DiskRead:
                    totals.DiskRead += bytes;
                    break;
                case Kind.DiskWrite:
                    totals.DiskWrite += bytes;
                    break;
                case Kind.NetReceive:
                    totals.NetReceive += bytes;
                    break;
                default:
                    totals.NetSend += bytes;
                    break;
            }
        }
    }

    internal enum Kind
    {
        DiskRead,
        DiskWrite,
        NetReceive,
        NetSend,
    }

    private sealed class Totals
    {
        public long DiskRead;
        public long DiskWrite;
        public long NetReceive;
        public long NetSend;
    }
}
