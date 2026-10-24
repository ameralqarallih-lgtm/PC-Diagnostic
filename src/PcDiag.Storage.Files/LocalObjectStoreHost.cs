using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PcDiag.Storage.Files;

/// <summary>
/// Keeps the object storage on this PC (<see cref="LocalObjectStore"/>) running while a storage location is it and it
/// was not stopped: it starts SeaweedFS (hidden, below normal priority, its output to weed.log in its folder), checks
/// it answers, starts it again when it stops (at most 5 times in 10 minutes, then it rests 10 minutes), and stops it
/// when the location is removed or stopped, or when this process ends. SeaweedFS runs in a job object of this
/// process, so it never outlives it; one left from a crash is stopped first (by its pid file, only if it is weed).
/// <para>
/// The storage host (from the moment the recorder starts) and the Web host each run one; the one holding its lock
/// file looks after SeaweedFS, the other takes over if that one goes. The one looking after it says how it is in
/// storage/local-store.json for the page (<see cref="Read"/>).
/// </para>
/// </summary>
public sealed partial class LocalObjectStoreHost : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly FileStore _store;
    private readonly string _root;
    private readonly ILogger _logger;
    private readonly string? _baseDirectory;
    private readonly Queue<DateTime> _starts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private FileStream? _lock;
    private Process? _process;
    private StreamWriter? _log;
    private LocalServer? _running;
    private nint _job;
    private DateTime _restUntil;
    private string? _problem;
    private bool _ready;

    /// <param name="store">The storage locations (one of them may be it).</param>
    /// <param name="root">The data folder (its state and lock in storage/, a downloaded program in tools/).</param>
    /// <param name="logger">Its log.</param>
    /// <param name="baseDirectory">Where the shipped program is looked for (the program's folder; tests give another).</param>
    public LocalObjectStoreHost(FileStore store, string root, ILogger logger, string? baseDirectory = null)
    {
        _store = store;
        _root = root;
        _logger = logger;
        _baseDirectory = baseDirectory;
    }

    /// <summary>How long it may take to answer after starting.</summary>
    internal TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often it is looked after.</summary>
    internal TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Its process now (tests).</summary>
    internal Process? Process => _process;

    private string Folder => Path.Combine(_root, "storage");

    private string StateFile => StateFileOf(_root);

    private static string StateFileOf(string root) => Path.Combine(root, "storage", "local-store.json");

    /// <summary>How it is now, as the process looking after it last said (not running when that process has gone).</summary>
    public static LocalStoreState? Read(string root)
    {
        try
        {
            var file = StateFileOf(root);
            if (!File.Exists(file) || JsonSerializer.Deserialize<LocalStoreState>(File.ReadAllBytes(file), Json) is not { } state)
            {
                return null;
            }

            return Alive(state.OwnerPid) ? state : state with { Running = false, Pid = null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Looks after it until cancelled, then stops it.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
                {
                    _problem = ex.Message;
                    LogProblem(_logger, ex.Message);
                }

                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Stop();
        }
    }

    /// <summary>Once: starts, keeps or stops it as the locations say (only in the process holding its lock).</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!Own())
        {
            return;
        }

        var location = _store.Locations.FirstOrDefault(l => l.Local is not null);
        var server = location?.Local;
        if (location is null || server is null || server.Stopped)
        {
            Stop();
            _problem = null;
            Write();
            return;
        }

        // Moved, or other ports: started again as it is now.
        if (_running is not null && _running != server)
        {
            Stop();
        }

        if (_process is { HasExited: false })
        {
            Write();
            return;
        }

        if (_process is not null)
        {
            LogExited(_logger, _process.ExitCode);
            _problem ??= $"SeaweedFS stopped (exit code {_process.ExitCode}); it is started again.";
            Stop();
        }

        var now = DateTime.UtcNow;
        if (now < _restUntil)
        {
            Write();
            return;
        }

        if (LocalObjectStore.Exe(_root, _baseDirectory) is not { } exe)
        {
            _problem = "SeaweedFS is not on this PC: set PC Diagnostic Storage up again (it is downloaded once).";
            Write();
            return;
        }

        if (_store.KeysOf(location.Id) is not { } keys)
        {
            _problem = "Its keys are missing: remove PC Diagnostic Storage and set it up again.";
            Write();
            return;
        }

        while (_starts.Count > 0 && _starts.Peek() < now - TimeSpan.FromMinutes(10))
        {
            _starts.Dequeue();
        }

        if (_starts.Count >= 5)
        {
            _restUntil = now + TimeSpan.FromMinutes(10);
            _starts.Clear();
            _problem = $"SeaweedFS stopped 5 times in 10 minutes (see weed.log in {server.Folder}); it is tried again in 10 minutes.";
            LogResting(_logger, server.Folder);
            Write();
            return;
        }

        _starts.Enqueue(now);
        await StartAsync(exe, server, keys, cancellationToken).ConfigureAwait(false);
        Write();
    }

    private async Task StartAsync(string exe, LocalServer server, StorageKeys keys, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(server.Folder, "data"));
        StopLeftOver(server.Folder);

        // Its one identity (the keys the store has, protected there): written for it at every start.
        var config = Path.Combine(server.Folder, "s3-identities.json");
        File.WriteAllText(config, LocalObjectStore.ConfigJson(keys));
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = server.Folder,
        };
        foreach (var argument in LocalObjectStore.Arguments(server, config))
        {
            info.ArgumentList.Add(argument);
        }

        var log = new StreamWriter(new FileStream(Path.Combine(server.Folder, "weed.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new Win32Exception("SeaweedFS did not start.");
        }
        catch (Win32Exception ex)
        {
            await log.DisposeAsync().ConfigureAwait(false);
            _problem = "SeaweedFS could not be started: " + ex.Message;
            LogNotStarted(_logger, _problem);
            return;
        }

        void Line(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (log)
            {
                try
                {
                    log.WriteLine(e.Data);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or IOException)
                {
                    // Stopping.
                }
            }
        }

        process.OutputDataReceived += Line;
        process.ErrorDataReceived += Line;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // It runs at normal priority.
        }

        Contain(process);
        File.WriteAllText(Path.Combine(server.Folder, "weed.pid"), process.Id.ToString(CultureInfo.InvariantCulture));
        _process = process;
        _log = log;
        _running = server;
        _ready = false;

        // Ready when its S3 port answers (any answer: without keys it says 403).
        var until = DateTime.UtcNow + ReadyTimeout;
        while (DateTime.UtcNow < until && !process.HasExited)
        {
            if (await AnswersAsync(server.S3Port, cancellationToken).ConfigureAwait(false))
            {
                _ready = true;
                _problem = null;
                LogStarted(_logger, process.Id, server.S3Port);
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        _problem = process.HasExited
            ? $"SeaweedFS stopped as it started (exit code {process.ExitCode}): see weed.log in {server.Folder}."
            : $"SeaweedFS did not answer within {ReadyTimeout.TotalSeconds:0} seconds: see weed.log in {server.Folder}.";
        LogNotStarted(_logger, _problem);
        Stop();
    }

    private async Task<bool> AnswersAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri($"http://127.0.0.1:{port}/"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Stops it (and forgets it).</summary>
    public void Stop()
    {
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    LogStopped(_logger);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }

            process.Dispose();
            if (_running is { } server)
            {
                TryDelete(Path.Combine(server.Folder, "weed.pid"));
            }
        }

        if (_log is { } log)
        {
            // After its last line (written under the same lock).
            lock (log)
            {
                log.Dispose();
            }
        }

        _log = null;
        _process = null;
        _running = null;
        _ready = false;
    }

    /// <summary>A SeaweedFS left running by a crash of the process that looked after it: stopped (only if it is weed).</summary>
    private void StopLeftOver(string folder)
    {
        var pidFile = Path.Combine(folder, "weed.pid");
        if (!File.Exists(pidFile))
        {
            return;
        }

        try
        {
            if (int.TryParse(File.ReadAllText(pidFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                using var old = System.Diagnostics.Process.GetProcessById(pid);
                if (string.Equals(old.ProcessName, "weed", StringComparison.OrdinalIgnoreCase))
                {
                    old.Kill(entireProcessTree: true);
                    old.WaitForExit(5000);
                    LogLeftOver(_logger, pid);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or IOException)
        {
            // Gone already, or not ours.
        }

        TryDelete(pidFile);
    }

    /// <summary>Whether this process looks after it (holding its lock file; another process may hold it).</summary>
    private bool Own()
    {
        if (_lock is not null)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(Folder);
            _lock = new FileStream(Path.Combine(Folder, "local-store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            LogLooking(_logger, Environment.ProcessId);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void Write()
    {
        var state = new LocalStoreState(_process is { HasExited: false } && _ready, _process is { HasExited: false } p ? p.Id : null, _running?.S3Port, _problem, DateTimeOffset.UtcNow, Environment.ProcessId);
        try
        {
            var temp = $"{StateFile}.{Environment.ProcessId}.tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state, Json));
            File.Move(temp, StateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The page reads the last one meanwhile.
        }
    }

    /// <summary>Puts it in this process's job object: it ends when this process does, however that happens.</summary>
    private void Contain(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_job == 0)
        {
            _job = CreateJobObjectW(0, null);
            var info = new JobObjectExtendedLimit { LimitFlags = JobObjectLimitKillOnJobClose };
            if (_job == 0 || !SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JobObjectExtendedLimit>()))
            {
                LogNoJob(_logger, Marshal.GetLastPInvokeError());
                return;
            }
        }

        if (!AssignProcessToJobObject(_job, process.Handle))
        {
            LogNoJob(_logger, Marshal.GetLastPInvokeError());
        }
    }

    private static bool Alive(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next time.
        }
    }

    /// <summary>Stops it and lets another process look after it.</summary>
    public void Dispose()
    {
        Stop();
        _lock?.Dispose();
        _lock = null;
        if (_job != 0 && OperatingSystem.IsWindows())
        {
            CloseHandle(_job);
            _job = 0;
        }

        _http.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
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
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimit info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LoggerMessage(Level = LogLevel.Information, Message = "PC Diagnostic Storage (SeaweedFS) is looked after by this process ({Pid})")]
    private static partial void LogLooking(ILogger logger, int pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "PC Diagnostic Storage (SeaweedFS) started (pid {Pid}, S3 on 127.0.0.1:{Port})")]
    private static partial void LogStarted(ILogger logger, int pid, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "PC Diagnostic Storage (SeaweedFS) stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PC Diagnostic Storage (SeaweedFS) exited (code {Code}); it is started again")]
    private static partial void LogExited(ILogger logger, int code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PC Diagnostic Storage (SeaweedFS) did not start: {Problem}")]
    private static partial void LogNotStarted(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PC Diagnostic Storage (SeaweedFS) stopped 5 times in 10 minutes; resting 10 minutes (see weed.log in {Folder})")]
    private static partial void LogResting(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "A SeaweedFS left running (pid {Pid}) was stopped")]
    private static partial void LogLeftOver(ILogger logger, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SeaweedFS could not be tied to this process (error {Error}); it is stopped when this process stops normally")]
    private static partial void LogNoJob(ILogger logger, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Looking after PC Diagnostic Storage: {Problem}")]
    private static partial void LogProblem(ILogger logger, string problem);
}
