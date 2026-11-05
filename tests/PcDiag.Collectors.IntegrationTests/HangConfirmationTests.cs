using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PcDiag.Collectors;
using PcDiag.Collectors.Gpu;
using PcDiag.Collectors.Processes;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;

namespace PcDiag.Collectors.IntegrationTests;

public class HangConfirmationTests
{
    private const int Pid = 4242;
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Process ticks every 2 s with the GPU share given for each second since the start; the first tick that counts it.</summary>
    private static int? FirstConfirmed(HangConfirmation hangs, int seconds, DateTimeOffset? from = null)
    {
        var at = from ?? Start;
        for (var s = 0; s <= seconds; s += 2)
        {
            if (hangs.Observe([Pid], at.AddSeconds(s)).Contains(Pid))
            {
                return s;
            }
        }

        return null;
    }

    [Fact]
    public void A_short_pause_never_counts_and_a_window_that_stays_hung_counts_once_it_has_lasted()
    {
        var hangs = new HangConfirmation(_ => 0);
        FirstConfirmed(hangs, 28).ShouldBeNull();

        // It answered: the clock starts again for the next pause.
        hangs.Observe([], Start.AddSeconds(30)).ShouldBeEmpty();
        hangs.Since(Pid).ShouldBeNull();
        var again = Start.AddSeconds(40);
        FirstConfirmed(hangs, 30, again).ShouldBe(30);
        hangs.Since(Pid).ShouldBe(again); // dated when it began
    }

    [Fact]
    public void A_game_that_goes_on_drawing_is_never_hung_however_long_its_window_does_not_answer()
    {
        // VALORANT on the dev PC: 56 s without handling messages at 16-29 % of the GPU.
        var hangs = new HangConfirmation(_ => 20);

        FirstConfirmed(hangs, 120).ShouldBeNull();
    }

    [Fact]
    public void One_empty_reading_in_the_middle_of_a_load_does_not_count_as_stopped_drawing()
    {
        // ARK on the dev PC: read 0 % at the end of a 38 s load, averaging 44 %.
        var seconds = 0;
        var hangs = new HangConfirmation(_ => seconds == 32 ? 0 : 45);
        for (; seconds <= 40; seconds += 2)
        {
            hangs.Observe([Pid], Start.AddSeconds(seconds)).ShouldBeEmpty();
        }
    }

    [Fact]
    public void A_game_that_really_freezes_counts_once_its_drawing_has_faded_from_the_window()
    {
        var seconds = 0;
        var hangs = new HangConfirmation(_ => seconds < 40 ? 30 : 0);
        int? confirmed = null;
        for (; seconds <= 90 && confirmed is null; seconds += 2)
        {
            if (hangs.Observe([Pid], Start.AddSeconds(seconds)).Contains(Pid))
            {
                confirmed = seconds;
            }
        }

        // Drawing stopped at 40 s; the 15 s average falls under 1 % when the last drawing sample leaves it.
        confirmed.ShouldBe(54);
    }

    [Fact]
    public void Once_counted_it_stays_counted_until_the_window_answers()
    {
        var drawing = false;
        var hangs = new HangConfirmation(_ => drawing ? 50 : 0);
        FirstConfirmed(hangs, 30).ShouldBe(30);

        drawing = true;
        hangs.Observe([Pid], Start.AddSeconds(50)).ShouldContain(Pid);
        hangs.Observe([], Start.AddSeconds(52)).ShouldBeEmpty();
    }

    [Fact]
    public void Each_process_has_its_own_clock_and_its_own_gpu_use()
    {
        var hangs = new HangConfirmation(pid => pid == 1 ? 40 : 0);

        hangs.Observe([1, 2], Start).ShouldBeEmpty();
        hangs.Observe([1, 2, 3], Start.AddSeconds(30)).ShouldBe([2]); // 1 draws, 3 has only just hung
    }

    [Fact]
    public void The_recorder_hands_the_process_collector_what_the_gpu_collector_sees()
    {
        // Without this wiring nothing fails: every game would just read as not drawing and be called hung after 30 s.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new ElevationInfo(false));
        services.AddWindowsCollectors();
        using var provider = services.BuildServiceProvider();

        const BindingFlags Own = BindingFlags.NonPublic | BindingFlags.Instance;
        var activity = provider.GetRequiredService<ProcessGpuActivity>();
        typeof(GpuCollector).GetField("_activity", Own)!.GetValue(provider.GetRequiredService<GpuCollector>()).ShouldBeSameAs(activity);

        var hangs = typeof(ProcessCollector).GetField("_hangs", Own)!.GetValue(provider.GetRequiredService<ProcessCollector>())!;
        var reading = (Delegate)hangs.GetType().GetFields(Own).Single(f => typeof(Delegate).IsAssignableFrom(f.FieldType)).GetValue(hangs)!;
        reading.Target.ShouldBeSameAs(activity);
    }

    [Fact]
    public void Gpu_activity_is_the_busiest_adapter_of_the_process_at_the_latest_tick()
    {
        var activity = new ProcessGpuActivity();
        activity.PercentOf(Pid).ShouldBe(0);

        activity.Update(
        [
            new GpuProcessUsage(Pid, "gpu-a", 12, "3D", Reading<long>.Of(1)),
            new GpuProcessUsage(Pid, "gpu-b", 30, "3D", Reading<long>.Of(1)),
            new GpuProcessUsage(7, "gpu-a", 90, "3D", Reading<long>.Of(1)),
        ]);
        activity.PercentOf(Pid).ShouldBe(30);

        activity.Update([]);
        activity.PercentOf(Pid).ShouldBe(0);
    }
}
