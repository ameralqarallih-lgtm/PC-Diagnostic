namespace PcDiag.Agent;

/// <summary>
/// Stops the Agent cleanly when the installer asks (<see cref="StopReason.ExitEventName"/>). The installer used to kill it, which
/// cut its last writes and counted as an unexpected stop at the next start (11 times on the dev PC, Oct–Nov 2026). The event
/// is created by this elevated process, so a program running without administrator rights cannot set it.
/// </summary>
/// <param name="eventName">Another name, for tests only: setting the real one would close the recorder of the PC they run on.</param>
internal sealed partial class ExitSignalService(StopReason reason, IHostApplicationLifetime lifetime, ILogger<ExitSignalService> logger, string? eventName = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EventWaitHandle signal;
        try
        {
            signal = new EventWaitHandle(false, EventResetMode.ManualReset, eventName ?? StopReason.ExitEventName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            LogUnavailable(logger, ex);
            return;
        }

        using (signal)
        {
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => asked.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
                await asked.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
                reason.Set("the installer asked it to stop");
                lifetime.StopApplication();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Stopping for another reason.
            }
            finally
            {
                wait.Unregister(null);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The installer's stop signal could not be created; an update will end the recorder by force")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);
}
