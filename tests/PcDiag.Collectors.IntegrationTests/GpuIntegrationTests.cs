using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Collectors.Gpu;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Gpu;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>Runs the GPU stack against this machine. Assertions hold on any hardware; vendor checks are conditional.</summary>
public class GpuIntegrationTests
{
    private static bool NvmlInstalled => File.Exists(Path.Combine(Environment.SystemDirectory, "nvml.dll"));

    [Fact]
    public void Dxgi_lists_hardware_adapters_with_luids()
    {
        var adapters = Dxgi.EnumerateAdapters();

        adapters.ShouldNotBeEmpty();
        adapters.Where(a => !a.IsSoftware).ShouldAllBe(a => a.VendorId != 0 && !string.IsNullOrWhiteSpace(a.Description));
        adapters.Select(a => a.Luid).Distinct().Count().ShouldBe(adapters.Count);
    }

    [Fact]
    public void D3dkmt_perf_data_is_either_plausible_or_absent()
    {
        using var kmt = D3dkmtAdapters.Open();
        foreach (var adapter in Dxgi.EnumerateAdapters().Where(a => !a.IsSoftware))
        {
            var perf = kmt.TryGetPerfData(adapter.Luid);
            if (perf is { AllZero: false })
            {
                perf.TemperatureC.ShouldBeInRange(1, 125);
                perf.PowerPercent.ShouldBeInRange(0, 200);
                kmt.TryGetNodePerfData(adapter.Luid, 0)?.FrequencyMhz.ShouldBeInRange(1, 10_000);
            }
        }
    }

    [Fact]
    public async Task Collector_reports_every_hardware_adapter_with_explicit_availability()
    {
        using var collector = new GpuCollector(
            [new NvmlGpuDetailProvider(), new D3dkmtGpuDetailProvider()],
            TimeProvider.System,
            NullLogger<GpuCollector>.Instance);
        (await collector.InitializeAsync(CancellationToken.None)).Availability.ShouldBe(Availability.Available);

        await Task.Delay(1000);
        var sample = await collector.CollectAsync(new SampleContext(1, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), false), CancellationToken.None);

        // Every hardware adapter, minus virtual twins of a real card (e.g. WSL2's GPU adapter, which has no PCI location).
        using var kmt = D3dkmtAdapters.Open();
        sample.Adapters.Count.ShouldBe(GpuCollector.WithoutVirtualTwins(Dxgi.EnumerateAdapters(), kmt.TryGetPciAddress).Count(a => !a.IsSoftware));
        sample.Adapters.Select(a => a.Adapter.AdapterKey).ShouldBeUnique();
        foreach (var adapter in sample.Adapters)
        {
            adapter.UtilizationPercent.HasValue.ShouldBeTrue();
            adapter.UtilizationPercent.Value!.Value.ShouldBeInRange(0, 100);
            adapter.VramUsedBytes.HasValue.ShouldBeTrue();
            if (adapter.VramTotalBytes.HasValue && adapter.Adapter.DedicatedMemoryBytes > (1L << 30))
            {
                adapter.VramUsedBytes.Value!.Value.ShouldBeLessThanOrEqualTo(adapter.VramTotalBytes.Value!.Value);
            }

            // Never a silent zero: a missing temperature carries a reason.
            (adapter.TemperatureC.HasValue || adapter.TemperatureC.Availability != Availability.Available).ShouldBeTrue();
        }

        var nvidia = sample.Adapters.FirstOrDefault(a => a.Adapter.VendorId == 0x10DE);
        if (nvidia is not null && NvmlInstalled)
        {
            nvidia.Adapter.DetailSources.ShouldContain("nvml");
            nvidia.TemperatureC.Value.GetValueOrDefault().ShouldBeInRange(1, 125);
            nvidia.PowerW.Value.GetValueOrDefault().ShouldBeGreaterThan(0);
            nvidia.PowerLimitW.Value.GetValueOrDefault().ShouldBeGreaterThan(0);
            nvidia.CoreClockMhz.HasValue.ShouldBeTrue();
        }

        sample.Processes.Count.ShouldBeLessThanOrEqualTo(GpuCollector.MaxProcesses);
    }
}
