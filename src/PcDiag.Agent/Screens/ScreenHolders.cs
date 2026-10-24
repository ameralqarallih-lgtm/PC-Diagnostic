using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PcDiag.Platform.Windows.Processes;
using PcDiag.Platform.Windows.Services;
using PcDiag.Screens;

namespace PcDiag.Agent.Screens;

/// <summary>Windows services, as the take-over sees them (a fake in tests).</summary>
internal interface IServiceSwitch
{
    /// <summary>Whether a service is running (or starting).</summary>
    bool IsRunning(string name);

    /// <summary>Stops a service and waits for it (and for its program to end).</summary>
    ControlOutcome Stop(string name);

    /// <summary>Starts a service and waits for it.</summary>
    ControlOutcome Start(string name);
}

/// <summary>The Service Control Manager (the recorder has administrator rights).</summary>
internal sealed class WindowsServiceSwitch : IServiceSwitch
{
    private const int OwnProcess = 0x10;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProgramEnd = TimeSpan.FromSeconds(30);

    public bool IsRunning(string name) => Find(name) is { State: 2 or 4 };

    /// <summary>
    /// Stops it, then waits for its program to end when it has one of its own: a service can say it has stopped while
    /// its program is still closing, and still holding the screen (L-Connect's took over a minute once). A program that
    /// is slower still is not ended: the screen is asked for again when it is free.
    /// </summary>
    public ControlOutcome Stop(string name)
    {
        var program = Find(name) is { ProcessId: > 0 } service && (service.ServiceType & OwnProcess) != 0 ? service.ProcessId : 0;
        var outcome = ServiceControl.Stop(name, Wait);
        if (outcome.Ok && program > 0)
        {
            try
            {
                using var process = Process.GetProcessById(program);
                process.WaitForExit(ProgramEnd);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already ended.
            }
        }

        return outcome;
    }

    public ControlOutcome Start(string name) => ServiceControl.Start(name, Wait);

    private static ServiceStatusRecord? Find(string name) =>
        ServiceControlManager.Enumerate().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Pauses the programs that hold a USB screen (its driver package's holders: L-Connect's services for Lian Li's) while
/// the recorder uses it, and starts them again after, in the opposite order. Only services that were running are
/// paused, and they are written down first (screen-holders.json): if the recorder stops before it can start them
/// again, it does so when it next starts. Their start types are never changed.
/// </summary>
internal sealed partial class ScreenHolders(string file, IServiceSwitch services, ILogger logger)
{
    private readonly Lock _lock = new();
    private List<ScreenHolder> _paused = Load(file);

    /// <summary>The programs paused now.</summary>
    public IReadOnlyList<ScreenHolder> Paused
    {
        get
        {
            lock (_lock)
            {
                return [.. _paused];
            }
        }
    }

    /// <summary>
    /// Pauses the holders that run (in their order); the problem when one could not be paused. One written down as
    /// paused that runs again (Windows started it at sign-in, or someone did) is paused again.
    /// </summary>
    public string? Take(IReadOnlyList<ScreenHolder> holders)
    {
        ArgumentNullException.ThrowIfNull(holders);
        lock (_lock)
        {
            foreach (var holder in holders)
            {
                if (!services.IsRunning(holder.Service))
                {
                    continue;
                }

                // Written down before it stops: a recorder that stops now still starts it again.
                var listed = _paused.Any(p => string.Equals(p.Service, holder.Service, StringComparison.OrdinalIgnoreCase));
                if (!listed)
                {
                    _paused.Add(holder);
                    Save();
                }

                var outcome = services.Stop(holder.Service);
                LogPaused(logger, holder.Service, holder.Name, outcome.Error ?? "done");
                if (!outcome.Ok && services.IsRunning(holder.Service))
                {
                    if (!listed)
                    {
                        _paused.Remove(holder);
                        Save();
                    }

                    return $"{holder.Name} could not be paused: {outcome.Error}";
                }
            }

            return null;
        }
    }

    /// <summary>Starts again everything paused, the last paused first.</summary>
    public void Release()
    {
        lock (_lock)
        {
            for (var i = _paused.Count - 1; i >= 0; i--)
            {
                var holder = _paused[i];
                var outcome = services.Start(holder.Service);
                LogResumed(logger, holder.Service, holder.Name, outcome.Error ?? "done");
            }

            _paused = [];
            Save();
        }
    }

    private void Save()
    {
        try
        {
            if (_paused.Count == 0)
            {
                File.Delete(file);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(_paused));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(logger, ex);
        }
    }

    private static List<ScreenHolder> Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<ScreenHolder>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Paused {Service} ({Program}) to use a USB screen: {Outcome}")]
    private static partial void LogPaused(ILogger logger, string service, string program, string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started {Service} ({Program}) again: {Outcome}")]
    private static partial void LogResumed(ILogger logger, string service, string program, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The list of paused programs could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
