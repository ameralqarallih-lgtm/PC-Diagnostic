using System.Buffers.Binary;
using System.Text;
using PcDiag.Collectors.Stability;
using PcDiag.Core.Stability;
using PcDiag.Engine.Events;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>Crashes from Windows' own events, what their dumps point at, and the plain-words explanation.</summary>
public sealed class StabilityTests
{
    private static readonly DateTimeOffset Boot = new(2026, 11, 7, 17, 14, 24, TimeSpan.Zero);

    private static EventRecordData Event(string provider, int id, DateTimeOffset at, string? binary = null, params (string? Name, string Value)[] data) =>
        new("System", null, provider, id, 1, at, null, [.. data.Select(d => new EventDataItem(d.Name, d.Value))], binary);

    private static EventRecordData Power(DateTimeOffset at, string code = "0", string button = "0") =>
        Event("Microsoft-Windows-Kernel-Power", 41, at, null, ("BugcheckCode", code), ("BugcheckParameter1", "0x0"), ("BugcheckParameter2", "0x0"),
            ("BugcheckParameter3", "0x0"), ("BugcheckParameter4", "0x0"), ("PowerButtonTimestamp", button));

    private static EventRecordData Report(DateTimeOffset at, string bugcheck, string dump) =>
        Event("Microsoft-Windows-WER-SystemErrorReporting", 1001, at, null, ("param1", bugcheck), ("param2", dump), ("param3", "b345911c"));

    /// <summary>A 64-bit kernel dump's first bytes: the header's bug check and a triage section with two drivers.</summary>
    private static byte[] Dump(uint code, ulong address)
    {
        var b = new byte[0x4000];
        "PAGEDU64"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x38), code);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x40), 0xFFFFFFFFC0000005);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x48), address);
        const int Drivers = 0x2400, Entry = 0xB0, Names = 0x3000;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x2000 + 0x30), Drivers);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x2000 + 0x34), 2);
        var modules = new[] { ("ntoskrnl.exe", 0xFFFFF804A3400000UL, 0x1500000u), ("nvlddmkm.sys", 0xFFFFF80570000000UL, 0x6000000u) };
        for (var i = 0; i < modules.Length; i++)
        {
            var (name, start, size) = modules[i];
            var at = Drivers + (i * Entry);
            var nameAt = Names + (i * 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)nameAt);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + 8 + 0x30), start);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8 + 0x40), size);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(nameAt), (uint)name.Length);
            Encoding.Unicode.GetBytes(name).CopyTo(b, nameAt + 4);
        }

        return b;
    }

    [Fact]
    public void A_dump_says_its_bug_check_and_the_module_its_address_is_in()
    {
        var facts = Minidump.Parse(Dump(0x1E, 0xFFFFF804A36487A2)).ShouldNotBeNull();

        facts.BugCheck.ShouldBe(0x1Eu);
        facts.Parameters[1].ShouldBe(0xFFFFF804A36487A2);
        facts.Modules.Select(m => m.Name).ShouldBe(["ntoskrnl.exe", "nvlddmkm.sys"]);
        facts.ModuleAt(0xFFFFF804A36487A2).ShouldBe("ntoskrnl.exe");
        facts.ModuleAt(0xFFFFF80571234567).ShouldBe("nvlddmkm.sys");
        facts.ModuleAt(0x1234).ShouldBeNull();
        Minidump.Parse(new byte[0x3000]).ShouldBeNull();
    }

    [Fact]
    public void Each_crash_is_paired_from_windows_events_with_its_dump_and_meaning()
    {
        const string DumpFile = @"C:\WINDOWS\Minidump\110626-22375-01.dmp";
        var events = new[]
        {
            // 7 Nov: froze, power button; 6008 says it went down at 17:13:30 UTC.
            Power(Boot, button: "134385452142711921"),
            Event("EventLog", 6008, Boot.AddSeconds(14), "EA070B000600070015000D001E00AD01EA070B000600070011000D001E00AD013C0000003C000000000000000000000000000000000000000000000000000000"),
            // 6 Nov: a blue screen with its dump.
            Power(Boot.AddDays(-2), "126"),
            Report(Boot.AddDays(-2).AddSeconds(11), "0x0000007e (0xffffffffc0000420, 0xfffff804b6a6291c, 0xffffad823a85ea08, 0xffffc48054bf8900)", DumpFile),
            // 4 Nov: neither.
            Power(Boot.AddDays(-4)),
        };
        var dumps = new Dictionary<string, byte[]> { [DumpFile] = Dump(0x7E, 0xFFFFF80571234567) };

        var crashes = StabilityService.Crashes(events, path => Minidump.Parse(dumps[path]), dumps.ContainsKey, 5);

        crashes.Select(c => c.Kind).ShouldBe([CrashKind.PowerButton, CrashKind.BlueScreen, CrashKind.Unexpected]);
        crashes[0].WentDown.ShouldBe(new DateTimeOffset(2026, 11, 7, 17, 13, 30, 429, TimeSpan.Zero));
        crashes[0].Explanation.ShouldContain("power button");
        var blue = crashes[1];
        blue.ShouldSatisfyAllConditions(
            c => c.BugCheck.ShouldBe("0x0000007E"),
            c => c.BugCheckName.ShouldBe("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED"),
            c => c.Parameters.ShouldNotBeNull().ShouldBe(["0xffffffffc0000420", "0xfffff804b6a6291c", "0xffffad823a85ea08", "0xffffc48054bf8900"]),
            c => c.DumpFile.ShouldBe(DumpFile),
            c => c.DumpKept.ShouldBeTrue(),
            c => c.Module.ShouldBe("nvlddmkm.sys"),
            c => c.Explanation.ShouldContain("inside nvlddmkm.sys"));
        crashes[2].Explanation.ShouldContain("no power button");
    }

    [Fact]
    public void A_kernel_crash_is_explained_without_blaming_windows_and_a_lost_dump_is_said()
    {
        const string Kernel = @"C:\WINDOWS\Minidump\102526-22484-01.dmp";
        const string Gone = @"C:\WINDOWS\Minidump\100626-23250-01.dmp";
        var events = new[]
        {
            Power(Boot, "30"),
            Report(Boot.AddSeconds(10), "0x0000001e (0xffffffffc0000005, 0xfffff804a36487a2, 0x0000000000000000, 0x0000000000000478)", Kernel),
            // A report whose Kernel-Power event is not in the log any more is a crash too.
            Report(Boot.AddDays(-30), "0x00000050 (0xffffdf04f7f88000, 0x0, 0xfffff8058700e00e, 0x2)", Gone),
        };

        var crashes = StabilityService.Crashes(events, _ => Minidump.Parse(Dump(0x1E, 0xFFFFF804A36487A2)), path => path == Kernel, 5);

        crashes.Count.ShouldBe(2);
        crashes[0].Module.ShouldBe("ntoskrnl.exe");
        crashes[0].Explanation.ShouldContain("rarely means Windows is at fault");
        crashes[1].BugCheckName.ShouldBe("PAGE_FAULT_IN_NONPAGED_AREA");
        crashes[1].DumpKept.ShouldBeFalse();
        crashes[1].Explanation.ShouldContain("Windows keeps only the last 5");
    }

    [Theory]
    [InlineData("0x0000007e", 0x7Eu)]
    [InlineData("126", 0x7Eu)]
    [InlineData("268435582", 0x1000007Eu)]
    [InlineData("", null)]
    public void Bug_check_codes_are_read_from_events(string text, uint? code) => BugChecks.Parse(text).ShouldBe(code);

    [Fact]
    public void The_flag_some_crashes_add_does_not_hide_the_name() => BugChecks.Describe(0x1000007E)!.Value.Name.ShouldBe("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED");

    [Fact]
    public async Task This_pc_has_a_report_from_windows_own_log()
    {
        var service = new StabilityService(new PcDiag.Engine.Events.SystemEventBuffer(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<StabilityService>.Instance);

        var report = await service.ReportAsync(30, CancellationToken.None);

        report.Problem.ShouldBeNull();
        report.Crashes.ShouldAllBe(c => c.Restarted >= report.Since);
    }
}
