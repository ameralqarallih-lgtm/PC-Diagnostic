using PcDiag.Core.Telemetry;
using PcDiag.Engine.Control;

namespace PcDiag.Engine.Tests.Control;

public sealed class SteadyCoreWatchTests
{
    private static readonly ProcessKey Spinner = new(1234, 1);
    private static readonly TimeSpan Slice = SteadyCoreWatch.DefaultSlice;
    private static readonly TimeSpan After = SteadyCoreWatch.DefaultAfter;

    private readonly SteadyCoreWatch _watch = new();
    private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private long _cpuMs;
    private double _gpu;

    public SteadyCoreWatchTests() => Tick();

    private static ProcessMetrics Metrics(ProcessKey key, long cpuMs) => new(
        key, "script.exe", Reading<double>.Of(0), Reading<long>.Of(cpuMs), Reading<long>.Of(0), Reading<long>.Of(0), Reading<long>.Of(0),
        Reading<double>.Of(0), Reading<double>.Of(0), Reading<int>.Of(1), Reading<int>.Of(1), Reading<int>.Of(8), ProcessState.Running);

    private void Tick() => _watch.Observe([Metrics(Spinner, _cpuMs)], _now, _ => _gpu);

    /// <summary>Runs the process at this many cores, ticking well inside what counts as the recorder running.</summary>
    private void Run(double cores, TimeSpan time)
    {
        var step = TimeSpan.FromSeconds(30);
        for (var done = TimeSpan.Zero; done < time; done += step)
        {
            _now += step;
            _cpuMs += (long)(cores * step.TotalMilliseconds);
            Tick();
        }
    }

    private TimeSpan? Steady => _watch.SteadyFor(Spinner);

    [Fact]
    public void One_core_for_two_hours_is_noticed_and_not_a_slice_sooner()
    {
        Run(1, After - Slice);
        Steady.ShouldBeNull();

        Run(1, Slice);
        Steady.ShouldBe(After);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(0.79)]
    [InlineData(1.21)]
    [InlineData(4)] // a compiler or a virtual machine
    public void What_is_not_about_one_core_is_never_noticed(double cores)
    {
        Run(cores, After * 3);
        Steady.ShouldBeNull();
    }

    [Fact]
    public void A_program_going_up_and_down_is_not_steady()
    {
        for (var i = 0; i < 40; i++)
        {
            Run(1, Slice);
            Run(0.2, Slice);
        }

        Steady.ShouldBeNull();
    }

    [Fact]
    public void A_program_that_draws_is_at_work_not_stuck()
    {
        _gpu = 20;
        Run(1, After * 2);
        Steady.ShouldBeNull();
    }

    [Fact]
    public void The_stuck_script_is_found()
    {
        // Its 5-minute slices and the averages across the gaps between the stored samples, 5 Nov 2026 from 15:28, on a PC in use.
        double[] measured =
        [
            0.99, 0.98, 0.88, 0.96, 0.96, 0.92, 0.96, 0.93, 0.95, 0.92, 0.98, 0.96, 0.95, 0.94, 0.96, 0.96, 0.98, 0.97, 0.99, 0.98,
            0.92, 0.90, 0.98, 0.97, 0.96, 0.80, 0.97, 0.84, 0.96, 0.80, 0.94, 0.92, 0.89, 0.97,
        ];
        foreach (var cores in measured)
        {
            Run(cores, Slice);
        }

        Steady.ShouldBe(Slice * measured.Length);
    }

    [Fact]
    public void A_short_dip_is_forgiven_but_a_stop_is_not()
    {
        Run(1, After);
        Steady.ShouldBe(After);

        // Not claimed while it is outside, and the run goes on when it is back.
        Run(0.3, Slice);
        Steady.ShouldBeNull();
        Run(1, Slice);
        Steady.ShouldBe(After + (Slice * 2));

        // Two slices in a row end the run, however long it has been.
        Run(0, Slice * 2);
        Steady.ShouldBeNull();
        Run(1, Slice);
        Steady.ShouldBeNull();
    }

    [Fact]
    public void A_dip_early_on_begins_again()
    {
        Run(1, Slice * 6);
        Run(0.3, Slice);
        Run(1, After - Slice);
        Steady.ShouldBeNull();

        Run(1, Slice);
        Steady.ShouldBe(After);
    }

    [Fact]
    public void Time_the_pc_was_asleep_neither_counts_nor_ends_a_run()
    {
        Run(1, After / 2);

        _now += TimeSpan.FromHours(8);
        Tick();
        Steady.ShouldBeNull();

        Run(1, After / 2);
        Steady.ShouldBe(After);
    }

    [Fact]
    public void A_process_that_ended_is_forgotten_and_its_pid_means_nothing_to_the_next_one()
    {
        Run(1, After);
        Steady.ShouldBe(After);

        _now += Slice;
        var next = new ProcessKey(Spinner.Pid, Spinner.CreateTimeUtcTicks + 1);
        _watch.Observe([Metrics(next, _cpuMs)], _now, _ => 0);

        Steady.ShouldBeNull();
        _watch.SteadyFor(next).ShouldBeNull();
    }
}
