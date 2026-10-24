using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Collectors.Cpu;
using PcDiag.Collectors.Memory;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Kernel;
using PcDiag.Platform.Windows.Pdh;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>Runs the real collectors against this machine. Values vary, so tests check shape and ranges.</summary>
public class CollectorIntegrationTests
{
    private static SampleContext Context(long sequence) => new(sequence, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), IsElevated: false);

    [Fact]
    public async Task Cpu_collector_reports_every_logical_processor()
    {
        using var collector = new CpuCollector(NullLogger<CpuCollector>.Instance);
        var status = await collector.InitializeAsync(CancellationToken.None);
        status.Availability.ShouldBe(Availability.Available);

        await Task.Delay(1000); // rate counters need two samples spaced apart
        var sample = await collector.CollectAsync(Context(1), CancellationToken.None);

        sample.TotalPercent.HasValue.ShouldBeTrue();
        sample.TotalPercent.Value!.Value.ShouldBeInRange(0, 100);
        sample.Cores.Count.ShouldBe(Environment.ProcessorCount);
        sample.Cores.Select(c => c.Index).ShouldBe(Enumerable.Range(0, Environment.ProcessorCount));
        sample.Cores.ShouldAllBe(c => !c.UtilizationPercent.HasValue || (c.UtilizationPercent.Value >= 0 && c.UtilizationPercent.Value <= 100));
        sample.FrequencyMhz.HasValue.ShouldBeTrue();
        sample.MaxFrequencyMhz.Value.GetValueOrDefault().ShouldBeGreaterThan(0);

        // Sensor-backed values are explicitly unavailable here, never zero.
        sample.PackageTemperatureC.HasValue.ShouldBeFalse();
        sample.PackageTemperatureC.Availability.ShouldNotBe(Availability.Available);
    }

    [Fact]
    public async Task Memory_collector_reports_consistent_figures()
    {
        using var collector = new MemoryCollector(NullLogger<MemoryCollector>.Instance);
        (await collector.InitializeAsync(CancellationToken.None)).Availability.ShouldBe(Availability.Available);

        await Task.Delay(1000);
        var sample = await collector.CollectAsync(Context(1), CancellationToken.None);

        var total = sample.TotalBytes.Value.GetValueOrDefault();
        total.ShouldBeGreaterThan(1L << 30);
        sample.AvailableBytes.Value.GetValueOrDefault().ShouldBeInRange(0, total);
        sample.CommittedBytes.Value.GetValueOrDefault().ShouldBeLessThanOrEqualTo(sample.CommitLimitBytes.Value.GetValueOrDefault());
        sample.Pressure.Value.GetValueOrDefault().ShouldBeInRange(0, 1);
        sample.PageFaultsPerSec.HasValue.ShouldBeTrue();
        // Compressed memory is the compression store's working set (no such process when compression is turned off).
        if (System.Diagnostics.Process.GetProcessesByName("Memory Compression").Length > 0)
        {
            sample.CompressedBytes.Value.GetValueOrDefault(-1).ShouldBeInRange(0, total);
        }
        else
        {
            sample.CompressedBytes.Availability.ShouldBe(Availability.Unsupported);
        }
    }

    [Fact]
    public void Missing_pdh_counters_are_reported_not_thrown()
    {
        using var query = PdhQuery.Open();

        var counter = query.TryAddCounter(@"\No Such Object\No Such Counter", out var status);

        counter.ShouldBeNull();
        status.ShouldNotBe(0u);
    }

    [Fact]
    public void Rate_counter_is_pending_until_it_has_two_samples()
    {
        using var query = PdhQuery.Open();
        var counter = query.TryAddCounter(@"\Processor Information(_Total)\% Processor Utility", out _)!;

        query.Collect();
        counter.GetDouble(noCap100: true).Availability.ShouldBe(Availability.Pending);

        Thread.Sleep(200);
        query.Collect();
        counter.GetDouble(noCap100: true).HasValue.ShouldBeTrue();
    }

    [Fact]
    public void Max_processor_frequency_is_available()
    {
        SystemInfo.TryGetMaxProcessorMhz().GetValueOrDefault().ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("0,0", true, 0, 0)]
    [InlineData("1,15", true, 1, 15)]
    [InlineData("0,_Total", false, 0, 0)]
    [InlineData("_Total", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void Processor_instance_names_are_parsed(string instance, bool valid, int group, int number)
    {
        CpuCollector.TryParseInstance(instance, out var g, out var n).ShouldBe(valid);
        if (valid)
        {
            (g, n).ShouldBe((group, number));
        }
    }

    [Theory]
    [InlineData(16, 4, 8, 32, 0.75)]  // physical usage dominates
    [InlineData(16, 12, 30, 32, 0.9375)] // commit dominates
    [InlineData(0, 0, 0, 0, 0)]       // degenerate input does not divide by zero
    public void Memory_pressure_is_the_larger_of_physical_use_and_commit_ratio(long totalGb, long availableGb, long commitGb, long limitGb, double expected)
    {
        var info = new MemoryInfo(totalGb << 30, availableGb << 30, commitGb << 30, limitGb << 30, 0, 0, 0, 0, 0, 0);

        MemoryCollector.ComputePressure(info).ShouldBe(expected, tolerance: 1e-9);
    }
}
