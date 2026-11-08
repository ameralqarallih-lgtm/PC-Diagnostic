using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace PcDiag.Agent.Tests;

/// <summary>A clean stop says why; the installer's signal stops the recorder cleanly instead of killing it.</summary>
public sealed class StopReasonTests
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped.TrySetResult();
    }

    [Fact]
    public void The_first_reason_given_is_the_one_logged()
    {
        var reason = new StopReason();
        reason.Why.ShouldContain("gave no reason");

        reason.Set("you chose Exit PC Diagnostic in the tray");
        reason.Set("Windows ended the session (sign-out, restart or shutdown)");

        reason.Why.ShouldBe("you chose Exit PC Diagnostic in the tray");
    }

    [Fact]
    public async Task The_installers_signal_stops_the_recorder_cleanly()
    {
        // Its own name: the real one belongs to the recorder running on this PC.
        var name = @"Local\PcDiagnostic.Tests.Exit." + Guid.NewGuid().ToString("N");
        var reason = new StopReason();
        var lifetime = new Lifetime();
        using var service = new ExitSignalService(reason, lifetime, NullLogger<ExitSignalService>.Instance, name);
        await service.StartAsync(CancellationToken.None);

        // What the installer does: open the recorder's event and set it.
        await WaitUntilAsync(() => EventWaitHandle.TryOpenExisting(name, out var opened) && Dispose(opened));
        EventWaitHandle.OpenExisting(name).Set();

        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        reason.Why.ShouldBe("the installer asked it to stop");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Without_the_signal_nothing_stops_and_stopping_ends_the_wait()
    {
        var lifetime = new Lifetime();
        using var service = new ExitSignalService(new StopReason(), lifetime, NullLogger<ExitSignalService>.Instance, @"Local\PcDiagnostic.Tests.Exit." + Guid.NewGuid().ToString("N"));
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        await service.StopAsync(CancellationToken.None);

        lifetime.Stopped.Task.IsCompleted.ShouldBeFalse();
    }

    private static bool Dispose(EventWaitHandle handle)
    {
        handle.Dispose();
        return true;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        condition().ShouldBeTrue();
    }
}
