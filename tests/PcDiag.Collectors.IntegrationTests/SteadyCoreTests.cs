using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Collectors.Control;
using PcDiag.Core.Control;
using PcDiag.Core.Ipc;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Control;
using PcDiag.Engine.Events;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>The recorder's process list says how long a process has held one core; limits on CPU use mean the same work on any PC.</summary>
public sealed class SteadyCoreTests
{
    private static readonly TimeSpan Slice = SteadyCoreWatch.DefaultSlice;
    private static readonly TimeSpan After = SteadyCoreWatch.DefaultAfter;

    private static ProcessMetrics Metrics(ProcessKey key, long cpuMs) => new(
        key, "self", Reading<double>.Of(0), Reading<long>.Of(cpuMs), Reading<long>.Of(0), Reading<long>.Of(0), Reading<long>.Of(0),
        Reading<double>.Of(0), Reading<double>.Of(0), Reading<int>.Of(1), Reading<int>.Of(1), Reading<int>.Of(8), ProcessState.Running);

    /// <summary>Feeds the directory what the recorder would send over this time, at one core, with this GPU use.</summary>
    private static async Task FeedAsync(ProcessDirectory directory, ProcessKey key, TimeSpan time, double gpuPercent)
    {
        var at = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var step = TimeSpan.FromSeconds(30);
        var gpu = new GpuSample(at, [], [new GpuProcessUsage(key.Pid, "gpu", gpuPercent, "3D", Reading<long>.Of(0))]);
        long sequence = 0;
        for (var done = TimeSpan.Zero; done <= time; done += step)
        {
            var now = at + done;
            await directory.ConsumeAsync(
                new SampleFrame(++sequence, now, Gpu: gpu, Processes: new ProcessSample(now, [Metrics(key, (long)done.TotalMilliseconds)], [], [])),
                CancellationToken.None);
        }
    }

    private static (SystemControlService Control, ProcessDirectory Directory, ProcessKey Self) Recorder()
    {
        var directory = new ProcessDirectory();
        var control = new SystemControlService(directory, new SystemEventBuffer(), TimeProvider.System, NullLogger<SystemControlService>.Instance);
        var self = control.Processes().Single(p => p.Pid == Environment.ProcessId);
        return (control, directory, new ProcessKey(self.Pid, self.StartTicks));
    }

    private static double? SteadyCoreSeconds(SystemControlService control) => control.Processes().Single(p => p.Pid == Environment.ProcessId).SteadyCoreSeconds;

    [Fact]
    public async Task The_process_list_says_how_long_a_process_has_held_one_core()
    {
        var (control, directory, self) = Recorder();
        SteadyCoreSeconds(control).ShouldBeNull();

        await FeedAsync(directory, self, After, gpuPercent: 0);

        SteadyCoreSeconds(control).ShouldBe(After.TotalSeconds);
        control.Processes().Where(p => p.Pid != self.Pid).ShouldAllBe(p => p.SteadyCoreSeconds == null);
    }

    [Fact]
    public async Task The_recorder_hands_the_watch_what_the_gpu_collector_sees()
    {
        // Without this wiring nothing fails: a game on one core would just read as not drawing.
        var (control, directory, self) = Recorder();

        await FeedAsync(directory, self, After + Slice, gpuPercent: 25);

        SteadyCoreSeconds(control).ShouldBeNull();
    }

    [Fact]
    public void How_long_it_held_a_core_crosses_the_pipe_from_the_recorder()
    {
        var process = new ControlProcess(
            13, 1, 4, "script.exe", null, null, "background", ControlProtection.None, null, 4, 100, 100, 1, 1, null, false, 1, [], DateTimeOffset.UnixEpoch, SteadyCoreSeconds: 75_600);

        var json = JsonSerializer.Serialize(new AgentResponse("1", true, Processes: [process]), AgentIpcJsonContext.Default.AgentResponse);

        JsonSerializer.Deserialize(json, AgentIpcJsonContext.Default.AgentResponse)!.Processes.ShouldNotBeNull().ShouldHaveSingleItem().SteadyCoreSeconds.ShouldBe(75_600);
        JsonSerializer.Serialize(new AgentResponse("1", true, Processes: [process with { SteadyCoreSeconds = null }]), AgentIpcJsonContext.Default.AgentResponse)
            .ShouldNotContain("steadyCore", Case.Insensitive, "hundreds of quiet processes must not grow the answer, which has a size limit");
    }

    [Theory]
    [InlineData(0.16, 4, 4)]
    [InlineData(0.16, 8, 2)]
    [InlineData(0.4, 8, 5)]
    [InlineData(1, 24, 4.1667)]
    public void A_number_of_cores_is_a_share_of_the_whole_pc(double cores, int processors, double percent) =>
        ResourceGuard.PercentOfPc(cores, processors).ShouldBe(percent, 0.001);

    [Fact]
    public void Busy_means_the_same_work_whatever_the_pc()
    {
        static ControlProcess Process(double cores) => new(
            13, 1, 4, "worker.exe", null, null, "background", ControlProtection.None, null, ResourceGuard.PercentOfPc(cores, Environment.ProcessorCount), 100, 100, 1, 1, null, false, 1, [],
            DateTimeOffset.UnixEpoch, 0, 0, 0, 0);

        var working = Process(0.5);
        var idle = Process(0.1);
        var guard = new ResourceGuard([working, idle], 0);

        guard.EndBlocked(working).ShouldNotBeNull().ShouldStartWith("it is busy");
        guard.TrimBlocked(working).ShouldNotBeNull().ShouldStartWith("it is busy");
        guard.EndBlocked(idle).ShouldBeNull();
        guard.TrimBlocked(idle).ShouldBeNull();
    }
}
