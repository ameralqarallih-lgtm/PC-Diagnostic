using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Events;

namespace PcDiag.Engine.Tests.Events;

/// <summary>
/// Replays real events (recorded on a development PC, personal data replaced) through the shipped rules: the
/// same XML parser and classifier the live collector uses.
/// </summary>
public class EventReplayTests
{
    private static readonly EventRuleSet Rules = EventRuleSet.Load(EventRuleSet.LoadDefaultJson(), null);

    internal static EventRecordData Fixture(string name)
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Events", name + ".xml"));
        return EventXml.Parse(xml) ?? throw new InvalidOperationException($"Fixture {name} did not parse.");
    }

    private static EventClassification? Classify(string fixture, string? message = null, EventRedactor? redactor = null) =>
        new EventClassifier(Rules, redactor ?? EventRedactor.None, 2000).Classify(Fixture(fixture), () => message);

    [Fact]
    public void Shipped_rules_are_valid_and_cover_the_expected_channels()
    {
        Rules.Problems.ShouldBeEmpty();
        Rules.Channels.ShouldBe(["System", "Microsoft-Windows-DxgKrnl-Admin", "Microsoft-Windows-WLAN-AutoConfig/Operational", "Application"], ignoreOrder: true);
        Rules.Rules.Select(r => r.Id).ShouldBeUnique();
    }

    [Theory]
    [InlineData("app-error-1000", "app.crash", EventCategory.ApplicationCrash, Severity.High)]
    [InlineData("dotnet-runtime-1026", "app.dotnet-unhandled-exception", EventCategory.ApplicationCrash, Severity.High)]
    [InlineData("app-hang-1002", "app.hang", EventCategory.ApplicationHang, Severity.High)]
    [InlineData("scm-7034", "service.crashed", EventCategory.Service, Severity.Medium)]
    [InlineData("scm-7000", "service.start-failed", EventCategory.Service, Severity.Medium)]
    [InlineData("nvlddmkm-153", "gpu.nvidia.error", EventCategory.Gpu, Severity.High)]
    [InlineData("kernel-power-41-bugcheck", "shutdown.unexpected.bugcheck", EventCategory.UnexpectedShutdown, Severity.Critical)]
    [InlineData("kernel-power-41", "shutdown.unexpected", EventCategory.UnexpectedShutdown, Severity.Critical)]
    [InlineData("eventlog-6008", "shutdown.unexpected.previous", EventCategory.UnexpectedShutdown, Severity.High)]
    [InlineData("bugcheck-1001", "bugcheck", EventCategory.BugCheck, Severity.Critical)]
    [InlineData("whea-3-info", "hardware.whea.info", EventCategory.HardwareError, Severity.Info)]
    [InlineData("e2fnexpress-27", "network.ethernet.link-down.intel", EventCategory.Network, Severity.Medium)]
    [InlineData("tcpip-4266", "network.port-exhaustion", EventCategory.Network, Severity.Medium)]
    [InlineData("wlan-8003", "network.wifi.disconnected", EventCategory.Network, Severity.Medium)]
    [InlineData("kernel-general-1-jump", "time.changed", EventCategory.TimeChange, Severity.Low)]
    [InlineData("kernel-general-12", "power.os-started", EventCategory.Power, Severity.Info)]
    [InlineData("kernel-power-42", "power.sleep", EventCategory.Power, Severity.Info)]
    [InlineData("kernel-power-107", "power.resume", EventCategory.Power, Severity.Info)]
    [InlineData("user32-1074", "power.shutdown-requested", EventCategory.Power, Severity.Info)]
    [InlineData("userpnp-20003", "device.driver-installed", EventCategory.Device, Severity.Info)]
    public void Real_events_are_classified_by_the_expected_rule(string fixture, string rule, EventCategory category, Severity severity)
    {
        var result = Classify(fixture).ShouldNotBeNull();

        result.Rule.Id.ShouldBe(rule);
        result.Event.Category.ShouldBe(category);
        result.Event.Severity.ShouldBe(severity);
        result.Event.RecordId.ShouldNotBeNull();

        // Every title placeholder resolved ("unknown" marks a missing field), unless Windows itself logged "unknown".
        if (!Fixture(fixture).Data.Any(d => d.Value == "unknown"))
        {
            result.Event.Title.ShouldNotContain("unknown", Case.Sensitive);
        }
    }

    /// <summary>
    /// Events built from the providers' own manifests (no such event has happened on the development PC;
    /// WER is turned off there): the field names and positions are the manifest's, the values made up.
    /// </summary>
    [Theory]
    [InlineData("dxgkrnl-admin-457", "gpu.dxgkrnl.adapter-start", Severity.High, "0xC00000E5")]
    [InlineData("dxgkrnl-admin-549", "gpu.dxgkrnl.adapter-start", Severity.High, "0xC0000001")]
    [InlineData("dxgkrnl-admin-445", "gpu.dxgkrnl.admin", Severity.Medium, null)]
    [InlineData("wer-1001-livekernel-141", "wer.live-kernel.video-timeout", Severity.High, null)]
    public void Events_laid_out_as_their_manifests_say_are_classified(string fixture, string rule, Severity severity, string? status)
    {
        var result = Classify(fixture).ShouldNotBeNull();

        result.Rule.Id.ShouldBe(rule);
        result.Event.Category.ShouldBe(EventCategory.Gpu);
        result.Event.Severity.ShouldBe(severity);
        result.Event.Title.ShouldNotContain("unknown", Case.Sensitive);
        if (status is not null)
        {
            result.Event.Data.ShouldNotBeNull()["status"].ShouldBe(status);
        }
    }

    [Theory]
    [InlineData("brother-1002")]               // event ID 1002 from a printer driver, not an application hang
    [InlineData("dotnet-runtime-1000-log")]    // an ASP.NET Core app's own log line, not a crash
    [InlineData("dcom-10016")]                 // known noise: ignored even though it is an error
    [InlineData("ntfs-98-healthy")]            // Ntfs 98 at information level: "volume is healthy"
    [InlineData("kernel-general-1-small")]     // a 0 ms time sync adjustment
    [InlineData("acpi-15-warning")]            // warnings are not collected wholesale
    [InlineData("dxgkrnl-operational-446")]    // routine virtual GPU progress: its channel is not subscribed
    public void Noise_and_unrelated_events_are_not_recorded(string fixture)
    {
        Classify(fixture).ShouldBeNull();
    }

    [Fact]
    public void Application_crash_identifies_the_process_and_its_start_time()
    {
        var e = Classify("app-error-1000")!.Event;

        e.Title.ShouldBe("IAStorDataMgrSvc.exe crashed (0xC0000005 in unknown)");
        e.ProcessId.ShouldBe(0x9b4);
        e.ProcessName.ShouldBe("IAStorDataMgrSvc.exe");
        e.Process.ShouldBe(new ProcessKey(0x9b4, DateTime.FromFileTimeUtc(0x1dd524fd8764b1cL).Ticks));
        new DateTime(e.Process!.Value.CreateTimeUtcTicks, DateTimeKind.Utc).Date.ShouldBe(new DateTime(2026, 10, 2));
        e.Data!["exceptionCode"].ShouldBe("0xC0000005");
        e.Data["faultOffset"].ShouldBe("0x0442D251");
        e.Data["appVersion"].ShouldBe("15.5.0.1051");
        e.Timestamp.ShouldBe(DateTimeOffset.Parse("2026-10-02T09:25:21.8239517Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void One_failure_reported_three_ways_points_at_the_same_process_and_service()
    {
        // The Intel RST service crashed once: Application Error 1000, .NET Runtime 1026 and SCM 7034.
        var crash = Classify("app-error-1000")!.Event;
        var dotnet = Classify("dotnet-runtime-1026")!.Event;
        var service = Classify("scm-7034")!.Event;

        dotnet.ProcessId.ShouldBe(crash.ProcessId);
        dotnet.ProcessName.ShouldBe("IAStorDataMgrSvc.exe");
        dotnet.Process.ShouldBeNull(); // .NET does not log the start time, so it is not linked to process history
        service.ServiceName.ShouldBe("IAStorDataMgrSvc"); // decoded from the binary data
        service.Title.ShouldBe("Service terminated unexpectedly: Intel(R) Rapid Storage Technology");
        (service.Timestamp - crash.Timestamp).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Events_without_message_resources_keep_their_raw_values()
    {
        var e = Classify("nvlddmkm-153", message: string.Empty)!.Event;

        e.RelatedDevice.ShouldBe(@"\Device\Video7");
        e.Data!["detail"].ShouldBe("Error occurred on GPUID: 100");
        e.Message.ShouldBe(@"\Device\Video7; Error occurred on GPUID: 100");
        e.Title.ShouldBe("NVIDIA display driver error (nvlddmkm 153)");
    }

    [Fact]
    public void Formatted_messages_are_stored_when_the_rule_allows()
    {
        Classify("kernel-power-41", message: "The system has rebooted without cleanly shutting down first.")!.Event
            .Message.ShouldBe("The system has rebooted without cleanly shutting down first.");
    }

    [Fact]
    public void Bug_checks_are_shown_in_hex()
    {
        Classify("kernel-power-41-bugcheck")!.Event.Title.ShouldBe("Unexpected restart after bug check 0x1000007E (Kernel-Power 41)");
        Classify("bugcheck-1001")!.Event.Title.ShouldStartWith("The computer restarted after a bug check (blue screen): 0x0000007e (");
        Classify("bugcheck-1001")!.Event.Data!["dumpFile"].ShouldBe(@"C:\WINDOWS\Minidump\090826-25640-01.dmp");
    }

    [Fact]
    public void Invisible_direction_marks_are_removed_from_dates()
    {
        Classify("eventlog-6008")!.Event.Title.ShouldBe("The previous shutdown at 7:59:56 AM on 9/23/2026 was unexpected");
    }

    [Fact]
    public void Wifi_network_names_and_account_names_are_never_stored()
    {
        var wifi = Classify("wlan-8003", message: "... SSID: Test Network ...")!.Event;
        wifi.Message.ShouldBeEmpty();
        wifi.Title.ShouldNotContain("Test Network");
        wifi.Data!.Values.ShouldNotContain(v => v.Contains("Test Network", StringComparison.Ordinal));
        wifi.RelatedDevice.ShouldBe("MediaTek Wi-Fi 7 MT7925 Wireless LAN Card");

        var shutdown = Classify("user32-1074", message: @"... on behalf of user TEST-PC\testuser ...")!.Event;
        shutdown.Message.ShouldBeEmpty();
        shutdown.Title.ShouldBe("Shutdown or restart requested by StartMenuExperienceHost.exe (power off)");
        shutdown.ProcessName.ShouldBe("StartMenuExperienceHost.exe");
    }

    [Fact]
    public void User_and_computer_names_are_redacted_when_user_name_capture_is_off()
    {
        var redactor = EventRedactor.For(["testuser"], "TEST-PC");
        var crash = new EventClassifier(Rules, redactor, 2000).Classify(
            Fixture("app-error-1000") with
            {
                Data = [.. Fixture("app-error-1000").Data.Select(d => d.Name == "AppPath" ? d with { Value = @"C:\Users\testuser\AppData\Local\Tool\tool.exe" } : d)],
            },
            () => @"Faulting application path: C:\Users\testuser\AppData\Local\Tool\tool.exe on TEST-PC")!.Event;

        crash.Data!["appPath"].ShouldBe(@"C:\Users\<user>\AppData\Local\Tool\tool.exe");
        crash.Message.ShouldBe(@"Faulting application path: C:\Users\<user>\AppData\Local\Tool\tool.exe on <computer>");
    }

    [Fact]
    public void Driver_installation_reads_user_data()
    {
        var e = Classify("userpnp-20003")!.Event;

        e.RelatedDevice.ShouldBe(@"USB\VID_04E8&PID_686C\R52YA0AVCQB");
        e.Data!["driverService"].ShouldBe("dg_ssudbus");
        e.Data["driverFile"].ShouldBe("ssudbus2.sys");
    }
}
