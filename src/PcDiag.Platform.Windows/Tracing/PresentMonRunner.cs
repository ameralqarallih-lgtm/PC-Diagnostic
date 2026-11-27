using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Overlay;

namespace PcDiag.Platform.Windows.Tracing;

/// <summary>
/// Frames that reached the screen, from Intel's PresentMon (open source, MIT; shipped in tools\presentmon): it follows each
/// frame through the graphics kernel and the desktop compositor until it is shown or replaced, from Windows' own event
/// tracing, without touching the game. It runs for one process at a time (the game in front), its CSV read from its
/// output as it comes; it stops a minute after the last ask or when another process is asked for. It runs in a job
/// object that ends it with the recorder, and its trace session is stopped by name after it ends (killing it would
/// leave the session running). Needs administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class PresentMonRunner : IDisplayedFrames, IDisposable
{
    /// <summary>PresentMon's trace session.</summary>
    public const string SessionName = "PcDiagnostic-PresentMon";

    /// <summary>How long it runs after the last ask.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromMinutes(1);

    /// <summary>A start that failed is not tried again for this long.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly ElevationInfo _elevation;
    private readonly TimeProvider _time;
    private readonly ILogger<PresentMonRunner> _logger;
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly DisplayedFrameTimes _frames = new(Stopwatch.Frequency);
    private Process? _process;
    private int _processId;
    private nint _job;
    private Timer? _idle;
    private DateTimeOffset _wantedUntil;
    private DateTimeOffset _failedAt = DateTimeOffset.MinValue;

    /// <summary>Creates the runner for PresentMon.exe in the program's tools folder.</summary>
    public PresentMonRunner(ElevationInfo elevation, TimeProvider time, ILogger<PresentMonRunner> logger)
        : this(elevation, time, logger, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "presentmon", "PresentMon.exe")))
    {
    }

    /// <summary>Creates the runner for PresentMon.exe at <paramref name="path"/>.</summary>
    public PresentMonRunner(ElevationInfo elevation, TimeProvider time, ILogger<PresentMonRunner> logger, string path)
    {
        _elevation = elevation;
        _time = time;
        _logger = logger;
        _path = path;
    }

    /// <inheritdoc />
    public string? Problem => !_elevation.IsElevated ? "Frames on the screen need the recorder to run as administrator."
        : !File.Exists(_path) ? "PresentMon is not installed with this copy, so only the frames sent are counted."
        : null;

    /// <summary>The command line it runs with (exposed for tests).</summary>
    internal static string Arguments(int processId) =>
        $"--process_id {processId} --output_stdout --no_console_stats --qpc_time --no_track_input --session_name {SessionName} --stop_existing_session --terminate_on_proc_exit";

    /// <inheritdoc />
    public void Want(int processId)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _wantedUntil = now + Linger;
            if (_process is { HasExited: false } && _processId == processId)
            {
                return;
            }

            Stop();
            if (Problem is not null || now - _failedAt < RetryAfter)
            {
                return;
            }

            Start(processId, now);
            _idle ??= new Timer(_ => StopWhenIdle(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    /// <inheritdoc />
    public DisplayedStats? Current(int processId) => Volatile.Read(ref _processId) == processId && _process is not null ? _frames.Stats(Stopwatch.GetTimestamp()) : null;

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _idle?.Dispose();
            _idle = null;
            Stop();
            if (_job != 0)
            {
                _ = CloseHandle(_job);
                _job = 0;
            }
        }
    }

    private void StopWhenIdle()
    {
        lock (_lock)
        {
            if (_process is not null && _time.GetUtcNow() > _wantedUntil)
            {
                Stop();
            }
        }
    }

    private void Start(int processId, DateTimeOffset now)
    {
        _frames.Clear();
        var parser = new PresentMonCsv();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(_path, Arguments(processId))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_path)!,
            },
            EnableRaisingEvents = true,
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (parser.Parse(e.Data) is { } frame)
            {
                _frames.Add(frame);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                LogOutput(_logger, e.Data.Length > 300 ? e.Data[..300] : e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("the process did not start");
            }

            InJob(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            Volatile.Write(ref _processId, processId);
            LogStarted(_logger, processId);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _failedAt = now;
            process.Dispose();
            LogStartFailed(_logger, ex);
        }
    }

    private void Stop()
    {
        if (_process is not { } process)
        {
            return;
        }

        _process = null;
        Volatile.Write(ref _processId, 0);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It ended by itself (its process exited).
        }
        finally
        {
            process.Dispose();
            EtwNative.StopSession(SessionName);
        }
    }

    /// <summary>Puts PresentMon in a job that ends it when the recorder ends, however the recorder ends.</summary>
    private void InJob(Process process)
    {
        if (_job == 0)
        {
            _job = CreateJobObjectW(0, null);
            var info = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
            if (_job == 0 || !SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            {
                return;
            }
        }

        _ = AssignProcessToJobObject(_job, process.Handle);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PresentMon started for process {ProcessId}")]
    private static partial void LogStarted(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PresentMon could not start; frames on the screen are not counted for a minute")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PresentMon: {Line}")]
    private static partial void LogOutput(ILogger logger, string line);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
