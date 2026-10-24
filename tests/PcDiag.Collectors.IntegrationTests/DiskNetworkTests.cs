using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PcDiag.Collectors.Disk;
using PcDiag.Collectors.Network;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Network;
using PcDiag.Platform.Windows.Storage;
using PcDiag.Engine.Events;

namespace PcDiag.Collectors.IntegrationTests;

public class DiskLogicTests
{
    [Theory]
    [InlineData("0 g: f:", 0, new[] { "G:", "F:" })]
    [InlineData("1 c:", 1, new[] { "C:" })]
    [InlineData("2", 2, new string[0])]
    public void Physical_disk_instances_are_parsed(string instance, int number, string[] volumes)
    {
        DiskCollector.TryParseInstance(instance, out var n, out var v).ShouldBeTrue();
        n.ShouldBe(number);
        v.ShouldBe(volumes);
    }

    [Theory]
    [InlineData("_total")]
    [InlineData("")]
    [InlineData("x c:")]
    public void Non_disk_instances_are_rejected(string instance)
    {
        DiskCollector.TryParseInstance(instance, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Disk_keys_are_stable_and_do_not_contain_the_serial()
    {
        var key = DiskCollector.DiskKey("Samsung SSD", "S6XYZ123", 1_000_000, 0);

        key.ShouldBe(DiskCollector.DiskKey("Samsung SSD", "S6XYZ123", 1_000_000, 3)); // number changes don't matter
        key.ShouldNotContain("S6XYZ123");
        key.ShouldStartWith("disk-");
        DiskCollector.DiskKey("Samsung SSD", "OTHER", 1_000_000, 0).ShouldNotBe(key);
    }

    [Theory]
    [InlineData(true, "SATA", StorageMediaKind.Hdd)]
    [InlineData(false, "SATA", StorageMediaKind.Ssd)]
    [InlineData(null, "NVMe", StorageMediaKind.Ssd)]
    [InlineData(null, "USB", StorageMediaKind.Unknown)]
    public void Media_kind_comes_from_seek_penalty_or_bus(bool? seeks, string bus, StorageMediaKind expected)
    {
        DiskCollector.MediaFor(seeks, bus).ShouldBe(expected);
    }

    [Fact]
    public async Task Real_disks_are_identified_and_mapped_to_volumes()
    {
        using var collector = new DiskCollector(TimeProvider.System, NullLogger<DiskCollector>.Instance);
        (await collector.InitializeAsync(CancellationToken.None)).Availability.ShouldBe(Availability.Available);

        await Task.Delay(1000);
        var sample = await collector.CollectAsync(new SampleContext(1, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), false), CancellationToken.None);

        sample.Disks.ShouldNotBeEmpty();
        sample.Disks.ShouldAllBe(d => !string.IsNullOrWhiteSpace(d.Disk.Model) && d.Disk.SizeBytes > 0);
        sample.Disks.ShouldAllBe(d => d.ActivePercent.HasValue && d.ActivePercent.Value >= 0 && d.ActivePercent.Value <= 100);
        sample.Disks.ShouldAllBe(d => d.TemperatureC.Availability == Availability.Unsupported); // sensors arrive in Phase 6

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        var systemVolume = sample.Volumes.Single(v => string.Equals(v.Mount, systemDrive, StringComparison.OrdinalIgnoreCase));
        systemVolume.DiskKey.ShouldNotBeNull();
        systemVolume.FreeBytes.Value.GetValueOrDefault().ShouldBeLessThan(systemVolume.TotalBytes.Value.GetValueOrDefault());
    }

    [Fact]
    public void Storage_device_query_works_without_elevation()
    {
        var disk = StorageDevices.TryQuery(0);

        disk.ShouldNotBeNull();
        disk.SizeBytes.ShouldBeGreaterThan(0);
        disk.BusType.ShouldNotBe("Unknown");
    }
}

public class NetworkLogicTests
{
    [Theory]
    [InlineData(NetworkInterfaceType.Wireless80211, "MediaTek Wi-Fi 7", NetworkInterfaceKind.Wifi)]
    [InlineData(NetworkInterfaceType.Ethernet, "Intel(R) Ethernet Controller I226-V", NetworkInterfaceKind.Ethernet)]
    [InlineData(NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceKind.Virtual)]
    [InlineData(NetworkInterfaceType.Unknown, "WireGuard Tunnel", NetworkInterfaceKind.Vpn)]
    [InlineData(NetworkInterfaceType.Ethernet, "Bluetooth Device (Personal Area Network)", NetworkInterfaceKind.Bluetooth)]
    [InlineData(NetworkInterfaceType.Wwanpp, "Mobile Broadband", NetworkInterfaceKind.Cellular)]
    public void Interfaces_are_classified(NetworkInterfaceType type, string description, NetworkInterfaceKind expected)
    {
        NetworkCollector.Classify(type, description).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Intel(R) Ethernet Controller I226-V-QoS Packet Scheduler-0000", false, true, false)] // NDIS filter layer
    [InlineData("Intel(R) Ethernet Controller I226-V", true, false, true)]
    [InlineData("WAN Miniport (IP)", false, false, false)]
    [InlineData("Hyper-V Virtual Switch Extension Adapter", false, false, false)]
    [InlineData("Hyper-V Virtual Ethernet Adapter", false, false, true)]
    [InlineData("Microsoft Teredo Tunneling Adapter", false, false, false)]
    public void Only_traffic_bearing_interfaces_are_recorded(string description, bool hardware, bool filter, bool expected)
    {
        NetworkCollector.IsRelevant(NetworkInterfaceType.Ethernet, description, new InterfaceFlags(hardware, filter)).ShouldBe(expected);
    }

    [Fact]
    public void Filter_layers_are_recognised_by_name_when_flags_are_unavailable()
    {
        NetworkCollector.IsRelevant(NetworkInterfaceType.Ethernet, "Intel NIC-WFP 802.3 MAC Layer LightWeight Filter-0000", null).ShouldBeFalse();
        NetworkCollector.IsRelevant(NetworkInterfaceType.Ethernet, "Intel NIC", null).ShouldBeTrue();
        NetworkCollector.IsRelevant(NetworkInterfaceType.Loopback, "Software Loopback Interface 1", null).ShouldBeFalse();
    }

    [Fact]
    public void Counter_resets_yield_no_delta_instead_of_a_negative_rate()
    {
        NetworkCollector.Delta(1500, 1000).ShouldBe(500);
        NetworkCollector.Delta(10, 1000).ShouldBeNull();
    }

    [Theory]
    [InlineData(-50, 100)]
    [InlineData(-75, 50)]
    [InlineData(-100, 0)]
    [InlineData(-30, 100)]
    [InlineData(-120, 0)]
    public void Wifi_signal_quality_follows_the_documented_rssi_mapping(int rssi, double quality)
    {
        WlanClient.SignalQualityFromRssi(rssi).ShouldBe(quality);
    }

    [Fact]
    public async Task Real_interfaces_exclude_filter_layers_and_report_rates()
    {
        var privacy = new StaticPrivacy(new PrivacyOptions());
        using var collector = new NetworkCollector(privacy, new SystemEventBuffer(), TimeProvider.System, NullLogger<NetworkCollector>.Instance);
        await collector.InitializeAsync(CancellationToken.None);
        var context = new SampleContext(1, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), false);

        var first = await collector.CollectAsync(context, CancellationToken.None);
        await Task.Delay(1000);
        var second = await collector.CollectAsync(context, CancellationToken.None);

        var flags = InterfaceFlagsReader.Read();
        flags.ShouldNotBeEmpty();
        second.Interfaces.ShouldAllBe(i => !(flags.ContainsKey(Guid.Parse(i.Interface.InterfaceKey)) && flags[Guid.Parse(i.Interface.InterfaceKey)].IsFilter));
        first.Interfaces.Where(i => i.IsUp).ShouldAllBe(i => i.ReceiveBytesPerSec.Availability == Availability.Pending);
        second.Interfaces.Where(i => i.IsUp).ShouldAllBe(i => i.ReceiveBytesPerSec.HasValue && i.ReceiveBytesPerSec.Value >= 0);
    }

    [Fact]
    public async Task Network_details_can_be_hidden_for_privacy()
    {
        var privacy = new StaticPrivacy(new PrivacyOptions { CollectNetworkDetails = false });
        using var collector = new NetworkCollector(privacy, new SystemEventBuffer(), TimeProvider.System, NullLogger<NetworkCollector>.Instance);
        await collector.InitializeAsync(CancellationToken.None);

        var sample = await collector.CollectAsync(new SampleContext(1, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), false), CancellationToken.None);

        sample.Interfaces.ShouldAllBe(i => i.Interface.Description == string.Empty && i.Interface.Name.StartsWith(i.Interface.Kind.ToString()));
    }

    private sealed class StaticPrivacy(PrivacyOptions value) : IOptionsMonitor<PrivacyOptions>
    {
        public PrivacyOptions CurrentValue => value;

        public PrivacyOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<PrivacyOptions, string?> listener) => null;
    }
}
