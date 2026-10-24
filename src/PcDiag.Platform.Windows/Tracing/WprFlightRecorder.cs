using System.Diagnostics;
using System.Runtime.Versioning;
using PcDiag.Core.Abstractions;

namespace PcDiag.Platform.Windows.Tracing;

/// <summary>
/// The Enhanced-mode flight recorder: Windows Performance Recorder (wpr.exe from System32, with fixed arguments) in
/// memory mode under its own instance name, so it never touches a recording the user runs with WPR. It uses PC
/// Diagnostic's own profile (PcDiagnostic.wprp, next to the program): the providers of the built-in GPU and desktop
/// composition profiles with a fixed memory cap of <see cref="BufferMegabytes"/> MB. The built-in profiles size their
/// buffers as a share of RAM, which on a large PC pinned gigabytes and saved multi-gigabyte traces. Saving writes a
/// compressed .etl for Windows Performance Analyzer and recording starts again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WprFlightRecorder(ElevationInfo elevation) : IFlightRecorder, IDisposable
{
    /// <summary>The WPR instance this recorder owns.</summary>
    public const string InstanceName = "PcDiagnostic";

    /// <summary>The profile file, shipped next to the program.</summary>
    public const string ProfileFile = "PcDiagnostic.wprp";

    /// <summary>The profile in it.</summary>
    public const string ProfileName = "PcDiagnostic.Light";

    /// <summary>Memory the profile's buffers may use (kernel events + GPU and composition events).</summary>
    public const int BufferMegabytes = 256 + 128;

    /// <summary>Longest a start or cancel may take.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Longest a save may take (it merges and compresses the buffers).</summary>
    public static readonly TimeSpan SaveTimeout = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public bool Running { get; private set; }

    /// <inheritdoc />
    public string? Problem { get; private set; }

    /// <summary>wpr.exe, resolved from the Windows folder (never from PATH).</summary>
    public static string WprPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpr.exe");

    /// <summary>The profile next to the program (Program Files when installed, so only administrators can change it).</summary>
    public static string ProfilePath => Path.Combine(AppContext.BaseDirectory, ProfileFile);

    /// <summary>The arguments that start recording.</summary>
    internal static IReadOnlyList<string> StartArguments(string profilePath) => ["-start", $"{profilePath}!{ProfileName}", "-instancename", InstanceName];

    /// <summary>The arguments that save the buffers into <paramref name="etlPath"/>.</summary>
    internal static IReadOnlyList<string> SaveArguments(string etlPath, string description) =>
        ["-stop", etlPath, description, "-skipPdbGen", "-compress", "-instancename", InstanceName];

    /// <inheritdoc />
    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> SaveAsync(string etlPath, string description, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etlPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Running)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(etlPath)!);
            var (exit, output) = await RunAsync(SaveArguments(etlPath, description), SaveTimeout, cancellationToken).ConfigureAwait(false);
            Running = false;
            var saved = exit == 0 && File.Exists(etlPath);
            string? saveProblem = null;
            if (!saved)
            {
                // A timed-out or failed save leaves the instance half stopped and maybe a partial file behind.
                saveProblem = exit == TimedOut
                    ? $"Saving the trace took longer than {SaveTimeout.TotalMinutes:F0} minutes and was cancelled."
                    : $"Saving the trace failed: {Summarize(output)}";
                await RunAsync(["-cancel", "-instancename", InstanceName], CommandTimeout, cancellationToken).ConfigureAwait(false);
                TryDelete(etlPath);
            }

            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            if (saveProblem is not null)
            {
                // Recording again (or not): either way the failed save is what the caller reports.
                Problem = Running ? saveProblem : $"{saveProblem} {Problem}";
            }

            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Running)
            {
                await RunAsync(["-cancel", "-instancename", InstanceName], CommandTimeout, cancellationToken).ConfigureAwait(false);
                Running = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StartCoreAsync(CancellationToken cancellationToken)
    {
        if (!elevation.IsElevated)
        {
            Problem = "Needs the recorder to run as administrator.";
            return false;
        }

        if (!File.Exists(WprPath))
        {
            Problem = "Windows Performance Recorder (wpr.exe) is not available on this PC.";
            return false;
        }

        var profile = ProfilePath;
        if (!File.Exists(profile))
        {
            Problem = $"The recording profile {ProfileFile} is missing from the program folder.";
            return false;
        }

        var (exit, output) = await RunAsync(StartArguments(profile), CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (exit != 0 && output.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            // Left running by a previous run of the recorder (same instance name): start over.
            await RunAsync(["-cancel", "-instancename", InstanceName], CommandTimeout, cancellationToken).ConfigureAwait(false);
            (exit, output) = await RunAsync(StartArguments(profile), CommandTimeout, cancellationToken).ConfigureAwait(false);
        }

        Running = exit == 0;
        Problem = Running ? null : $"Windows Performance Recorder did not start: {Summarize(output)}";
        return Running;
    }

    /// <summary>Exit code reported when a command ran out of time (and was killed).</summary>
    private const int TimedOut = -2;

    private static async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(WprPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("wpr.exe did not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested(); // the caller is stopping
            return (TimedOut, "timed out");
        }

        return (process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the user; the problem text explains the save failed.
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static string Summarize(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => !l.StartsWith("Microsoft Windows Performance Recorder", StringComparison.Ordinal) && !l.StartsWith("Copyright", StringComparison.Ordinal));
        return string.IsNullOrEmpty(line) ? "no details" : line.Length > 200 ? line[..200] : line;
    }
}
