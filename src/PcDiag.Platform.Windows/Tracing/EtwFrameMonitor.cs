using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Overlay;
using static PcDiag.Platform.Windows.Tracing.EtwNative;

namespace PcDiag.Platform.Windows.Tracing;

/// <summary>
/// Frame rates from a real-time ETW session on the present events, as PresentMon counts them: an app's graphics
/// runtime when it reports its presents (Microsoft-Windows-DXGI Present_Start 42 and PresentMultiplaneOverlay_Start
/// 55 for Direct3D 10–12, Microsoft-Windows-D3D9 Present_Start 1), otherwise the graphics kernel's
/// (Microsoft-Windows-DxgKrnl: blits, flips and present history, which every API's presents go through, OpenGL and
/// Vulkan too). Only the presenting process, the event and the time are read; the desktop compositor's own flips
/// are left out. The session runs while something wants frame rates and stops a minute after the last ask. Needs
/// administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class EtwFrameMonitor(ElevationInfo elevation, TimeProvider time) : IFrameMonitor, IDisposable
{
    /// <summary>The session name (stopped and restarted if a previous run left it behind).</summary>
    public const string SessionName = "PcDiagnostic-Frames";

    /// <summary>How long the session runs after the last ask.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromMinutes(1);

    internal static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    internal static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    internal static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    /// <summary>
    /// The graphics kernel's present events: Blit_Info (166), Flip_Info (168), PresentHistory_Start (171),
    /// Present_Info (184), PresentHistoryDetailed_Start (215), FlipMultiPlaneOverlay_Info (252).
    /// </summary>
    internal static readonly ushort[] KernelPresents = [166, 168, 171, 184, 215, 252];

    // Processes whose kernel presents are not an app's frames: the desktop compositor, the system.
    private static readonly string[] Compositors = ["dwm", "csrss"];

    /// <summary>How often the session hands its buffers over, in ms.</summary>
    private const uint FlushMs = 100;

    private readonly FrameTimes _frames = new(Stopwatch.Frequency);
    private readonly Lock _lock = new();
    private readonly Dictionary<int, string?> _names = [];
    private volatile int[] _skip = [];
    private long _skipChecked;
    private ulong _session;
    private ulong _trace = InvalidHandle;
    private Thread? _consumer;
    private GCHandle _self;
    private Timer? _idle;
    private DateTimeOffset _wantedUntil;
    private bool _running;

    private const ulong InvalidHandle = ulong.MaxValue;

    /// <inheritdoc />
    public string? Problem { get; private set; }

    /// <inheritdoc />
    public void Want()
    {
        lock (_lock)
        {
            _wantedUntil = time.GetUtcNow() + Linger;
            if (_running || !elevation.IsElevated)
            {
                Problem ??= elevation.IsElevated ? null : "Frame rates need the recorder to run as administrator.";
                return;
            }

            _running = Start();
            _idle ??= new Timer(_ => StopWhenIdle(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    /// <inheritdoc />
    public FrameStats? Current(int? foregroundProcessId)
    {
        if (_frames.Stats(Stopwatch.GetTimestamp(), foregroundProcessId) is not { } stats)
        {
            return null;
        }

        return new FrameStats(stats.ProcessId, NameOf(stats.ProcessId), stats.Fps, stats.FrameTimeMs, stats.Low1Fps, stats.Foreground);
    }

    /// <summary>An app's name, looked up once per process.</summary>
    private string? NameOf(int processId)
    {
        lock (_names)
        {
            if (_names.TryGetValue(processId, out var known))
            {
                return known;
            }
        }

        string? name = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It just ended.
        }

        lock (_names)
        {
            if (_names.Count > 256)
            {
                _names.Clear();
            }

            _names[processId] = name;
        }

        return name;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _idle?.Dispose();
            _idle = null;
            Stop();
        }
    }

    private void StopWhenIdle()
    {
        lock (_lock)
        {
            if (_running && time.GetUtcNow() > _wantedUntil)
            {
                Stop();
            }
        }
    }

    private bool Start()
    {
        // Frames are shown live: buffers come every 100 ms rather than every second.
        var status = StartSession(SessionName, out _session, FlushMs);
        if (status == ErrorAlreadyExists)
        {
            StopSession(SessionName);
            status = StartSession(SessionName, out _session, FlushMs);
        }

        if (status != 0)
        {
            Problem = $"The frame rate session could not start (error {status}).";
            return false;
        }

        var dxgi = Dxgi;
        var d3d9 = D3D9;
        var enabled = EnableTraceEx2(_session, &dxgi, EnableProvider, TraceLevelInformation, ulong.MaxValue, 0, 0, null) == 0;
        enabled |= EnableTraceEx2(_session, &d3d9, EnableProvider, TraceLevelInformation, ulong.MaxValue, 0, 0, null) == 0;

        // The kernel's present events only (an event id filter: the rest of its busy log never reaches the session).
        enabled |= EnableEvents(_session, DxgKrnl, ulong.MaxValue, KernelPresents) == 0;
        RefreshSkip();
        if (!enabled)
        {
            StopSession(SessionName);
            Problem = "The DirectX present events could not be enabled.";
            return false;
        }

        _self = GCHandle.Alloc(this);
        var name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            var logfile = new EventTraceLogfile
            {
                LoggerName = name,
                // Raw (QPC) timestamps: the frames are measured against Stopwatch.GetTimestamp().
                ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp,
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
            Problem = "The frame rate session could not be read.";
            return false;
        }

        _consumer = new Thread(() =>
        {
            var handle = _trace;
            _ = ProcessTrace(&handle, 1, null, null); // returns when the session stops
        })
        { IsBackground = true, Name = "PcDiag ETW frames" };
        _consumer.Start();
        Problem = null;
        return true;
    }

    private void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
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
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(EventRecord* record)
    {
        if (record->UserContext == 0 || GCHandle.FromIntPtr(record->UserContext).Target is not EtwFrameMonitor self)
        {
            return;
        }

        // The header's time stamp is a QPC value (the session asks for raw time stamps).
        var header = record->EventHeader;
        if (IsPresent(header.ProviderId, header.Id))
        {
            self._frames.Add((int)header.ProcessId, header.TimeStamp);
        }
        else if (header.ProviderId == DxgKrnl && KernelPresents.AsSpan().Contains(header.Id) && header.ProcessId > 4 && !self.Skipped((int)header.ProcessId))
        {
            self._frames.AddKernel((int)header.ProcessId, header.Id, header.TimeStamp);
        }
    }

    private bool Skipped(int processId)
    {
        // The compositor can restart: its process id is looked up again every ten seconds.
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _skipChecked)) > TimeSpan.FromSeconds(10))
        {
            RefreshSkip();
        }

        return _skip.AsSpan().Contains(processId);
    }

    private void RefreshSkip()
    {
        Interlocked.Exchange(ref _skipChecked, Stopwatch.GetTimestamp());
        var ids = new List<int> { Environment.ProcessId };
        foreach (var name in Compositors)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                ids.Add(process.Id);
                process.Dispose();
            }
        }

        _skip = [.. ids];
    }

    /// <summary>A present call: DXGI Present_Start (42) or PresentMultiplaneOverlay_Start (55), D3D9 Present_Start (1).</summary>
    internal static bool IsPresent(Guid provider, ushort id) => (provider == Dxgi && id is 42 or 55) || (provider == D3D9 && id == 1);
}
