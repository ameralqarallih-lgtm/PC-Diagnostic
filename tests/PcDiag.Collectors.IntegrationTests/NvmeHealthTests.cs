using System.Buffers.Binary;
using System.Security.Principal;
using PcDiag.Platform.Windows.Storage;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>The NVMe health log (log page 02h), which fills what Windows' reliability counters leave empty.</summary>
public class NvmeHealthTests
{
    private static byte[] Log()
    {
        var log = new byte[512];
        log[0] = 0b0000_0010; // temperature warning
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), 318); // 45 °C
        log[3] = 100;
        log[4] = 10;
        log[5] = 3;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(32), 2_000_000); // data units read
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(48), 4_000_000); // data units written
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(112), 1234); // power cycles
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128), 5678); // power-on hours
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(144), 42); // unsafe shutdowns
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(160), 1); // media errors
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(168), 1); // …upper half: 2^64 + 1
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(176), 7); // error log entries
        BinaryPrimitives.WriteUInt32LittleEndian(log.AsSpan(192), 15);
        BinaryPrimitives.WriteUInt32LittleEndian(log.AsSpan(196), 2);
        return log;
    }

    [Fact]
    public void The_health_log_is_decoded()
    {
        var health = NvmeHealth.Parse(Log())!;

        health.CriticalWarning.ShouldBe(2);
        health.TemperatureC.ShouldBe(45);
        health.AvailableSparePercent.ShouldBe(100);
        health.AvailableSpareThresholdPercent.ShouldBe(10);
        health.PercentageUsed.ShouldBe(3);
        health.DataReadBytes.ShouldBe(2_000_000 * 512_000.0);
        health.DataWrittenBytes.ShouldBe(4_000_000 * 512_000.0);
        health.PowerCycles.ShouldBe(1234);
        health.PowerOnHours.ShouldBe(5678);
        health.UnsafeShutdowns.ShouldBe(42);
        health.MediaErrors.ShouldBe(18446744073709551617.0);
        health.ErrorLogEntries.ShouldBe(7);
        health.WarningTemperatureMinutes.ShouldBe(15);
        health.CriticalTemperatureMinutes.ShouldBe(2);
    }

    [Fact]
    public void A_short_or_empty_log_reads_as_nothing()
    {
        NvmeHealth.Parse(new byte[100]).ShouldBeNull();
        NvmeHealth.Parse(new byte[512])!.TemperatureC.ShouldBeNull(); // 0 K is no reading
    }

    /// <summary>On this PC (administrator only): a drive either answers with a plausible log or not at all.</summary>
    [Fact]
    public void A_real_drive_answers_plausibly_when_elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            NvmeHealth.TryRead(0).ShouldBeNull(); // the drive cannot be opened for this without elevation
            return;
        }

        for (var disk = 0; disk < 4; disk++)
        {
            if (NvmeHealth.TryRead(disk) is { } health)
            {
                health.PercentageUsed.ShouldBeInRange(0, 255);
                health.PowerOnHours.ShouldBeGreaterThanOrEqualTo(0);
                if (health.TemperatureC is { } t)
                {
                    t.ShouldBeInRange(-20, 120);
                }
            }
        }
    }
}
