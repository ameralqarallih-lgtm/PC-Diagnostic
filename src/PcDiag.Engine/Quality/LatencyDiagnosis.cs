using System.Globalization;
using PcDiag.Core.Quality;

namespace PcDiag.Engine.Quality;

/// <summary>What a driver is, for the findings ("the NVIDIA graphics driver").</summary>
/// <param name="What">What it drives.</param>
/// <param name="Try">What could help when it holds the processors long.</param>
public sealed record DriverKind(string What, string Try);

/// <summary>
/// Driver latency findings: how long drivers held a processor (the busiest processor's DPC and interrupt time, each
/// minute) and, from the measurements, which driver ran longest at a time. Under 500 µs at a time is normal; from
/// 500 µs sound may crackle, and from 1 ms frames and input may come late.
/// </summary>
public static class LatencyDiagnosis
{
    /// <summary>A single DPC or ISR this long (µs) is worth a finding.</summary>
    public const double SlowUs = 500;

    /// <summary>A single DPC or ISR this long (µs) is serious.</summary>
    public const double VerySlowUs = 1000;

    /// <summary>
    /// A minute in which the busiest processor spent this share (%) or more in DPCs or interrupts (while gaming at a high
    /// frame rate 20-24 % is normal: many short GPU and USB routines).
    /// </summary>
    public const double BusyPercent = 30;

    /// <summary>A share (%) of a processor in drivers that is serious.</summary>
    public const double VeryBusyPercent = 50;

    /// <summary>What a driver is, by its file name (null when not known).</summary>
    public static DriverKind? Describe(string driver)
    {
        var name = driver.ToLowerInvariant();
        return name switch
        {
            "nvlddmkm.sys" => new("the NVIDIA graphics driver", "a clean install of the newest (or the previous) NVIDIA driver"),
            "amdkmdag.sys" or "atikmdag.sys" or "atikmpag.sys" => new("the AMD graphics driver", "a clean install of the newest (or the previous) AMD driver"),
            "igdkmd64.sys" or "igdkmdn64.sys" or "igdkmdnd64.sys" => new("the Intel graphics driver", "the newest Intel graphics driver"),
            "dxgkrnl.sys" or "dxgmms2.sys" => new("Windows' graphics kernel (on behalf of the graphics driver)", "updating the graphics driver"),
            "ndis.sys" or "tcpip.sys" or "netio.sys" => new("Windows' network stack (on behalf of a network adapter)", "updating the network adapter's driver, or turning off its power saving"),
            "wdf01000.sys" => new("the Windows driver framework (on behalf of a device's driver)", "checking the devices' drivers one by one (unplugging USB devices helps narrow it down)"),
            "acpi.sys" => new("power management (ACPI, the firmware's power control)", "a BIOS update, or the Balanced or High performance power plan"),
            "storport.sys" or "stornvme.sys" or "storahci.sys" or "iastora.sys" or "iastorac.sys" or "rstmwv.sys" => new("the storage driver", "the newest chipset or storage (RST) driver"),
            "usbxhci.sys" or "usbport.sys" or "ucx01000.sys" or "usbhub3.sys" => new("the USB driver", "unplugging USB devices one at a time, or the newest chipset driver"),
            "hdaudbus.sys" or "portcls.sys" or "hdaudio.sys" => new("the audio driver", "the newest audio driver from the PC's or motherboard's maker"),
            "ntoskrnl.exe" => new("the Windows kernel itself", "updating Windows; the kernel's own DPCs are timers and are seldom the problem"),
            _ when name.StartsWith("netwtw", StringComparison.Ordinal) || name.StartsWith("netwbw", StringComparison.Ordinal) => new("the Intel Wi-Fi driver", "the newest Intel Wi-Fi driver, or a cable"),
            _ when name.StartsWith("rtwlan", StringComparison.Ordinal) || name.StartsWith("rtkwlan", StringComparison.Ordinal) => new("the Realtek Wi-Fi driver", "the newest Wi-Fi driver, or a cable"),
            _ when name.StartsWith("rt640", StringComparison.Ordinal) || name.StartsWith("rtux", StringComparison.Ordinal) => new("the Realtek network driver", "the newest Realtek network driver, or turning off its power saving (Energy-Efficient Ethernet)"),
            _ when name.StartsWith("e1", StringComparison.Ordinal) || name.StartsWith("e2f", StringComparison.Ordinal) => new("the Intel network driver", "the newest Intel network driver"),
            _ when name.StartsWith("bth", StringComparison.Ordinal) || name.StartsWith("ibtusb", StringComparison.Ordinal) => new("the Bluetooth driver", "the newest Bluetooth driver"),
            _ => null,
        };
    }

    /// <summary>The findings for a period: its minutes and its measurements (newest last).</summary>
    public static List<QualityFinding> Analyze(IReadOnlyList<QualityMinute> minutes, IReadOnlyList<DriverLatencyMeasurement> measurements, Func<DateTimeOffset, string> clock)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(clock);
        var findings = new List<QualityFinding>();
        var busy = minutes.Where(m => Math.Max(m.DpcCoreMax ?? 0, m.IsrCoreMax ?? 0) >= BusyPercent).ToList();
        if (busy.Count > 0)
        {
            var peak = busy.MaxBy(m => Math.Max(m.DpcCoreMax ?? 0, m.IsrCoreMax ?? 0))!;
            var (what, value) = (peak.DpcCoreMax ?? 0) >= (peak.IsrCoreMax ?? 0) ? ("DPCs", peak.DpcCoreMax ?? 0) : ("interrupts", peak.IsrCoreMax ?? 0);
            findings.Add(new QualityFinding("driver", value >= VeryBusyPercent ? 3 : 2,
                $"In {busy.Count} minute{(busy.Count == 1 ? "" : "s")} drivers held one processor {N(BusyPercent)} % of the time or more, the most at {clock(peak.At)} ({N(value)} % in {what}): sound may crackle and frames come late while they do."));
        }

        foreach (var measurement in measurements.Where(m => m.Drivers.Count > 0).TakeLast(3).Reverse())
        {
            var slowest = measurement.Drivers.MaxBy(d => d.LongestUs)!;
            if (slowest.LongestUs < SlowUs)
            {
                findings.Add(new QualityFinding("driver", 0,
                    $"The measurement at {clock(measurement.From)} ({N(measurement.Seconds)} s) found no driver running 500 µs or more at a time (the longest: {slowest.Driver}, {N(slowest.LongestUs)} µs)."));
                continue;
            }

            var kind = Describe(slowest.Driver);
            var who = kind is null ? slowest.Driver : $"{slowest.Driver} ({kind.What})";
            var help = kind is null ? "" : $" What could help: {kind.Try}.";
            var often = slowest.Over1Ms > 0 ? $"; {slowest.Over1Ms} of its runs took 1 ms or more" : slowest.Over500Us > 0 ? $"; {slowest.Over500Us} took 500 µs or more" : "";
            findings.Add(new QualityFinding("driver", slowest.LongestUs >= VerySlowUs ? 3 : 2,
                $"The measurement at {clock(measurement.From)} found {who} running up to {N(slowest.LongestUs)} µs at a time{often}: while it runs nothing else gets that processor, so sound may crackle and frames or input come late.{help}"));
        }

        if (findings.Count == 0 && minutes.Any(m => m.DpcCoreMax is not null))
        {
            findings.Add(new QualityFinding("driver", 0,
                $"Drivers never held a processor {N(BusyPercent)} % of the time or more: the most was {N(minutes.Max(m => Math.Max(m.DpcCoreMax ?? 0, m.IsrCoreMax ?? 0)))} %."));
        }

        return findings;
    }

    private static string N(double value) => value.ToString(Math.Abs(value) < 10 && value != Math.Round(value) ? "0.#" : "0", CultureInfo.InvariantCulture);
}
