using PcDiag.Core.Reporting;

namespace PcDiag.Core.Tests.Reporting;

public sealed class ReportRedactorTests
{
    [Fact]
    public void Names_folders_and_addresses_are_removed()
    {
        var text = $@"Could not open C:\Users\jane.doe\AppData\Local\PcDiagnostic\telemetry.db on {Environment.MachineName} for {Environment.UserName}; mail jane@example.com, peer 192.168.1.42";

        var redacted = ReportRedactor.Redact(text);

        redacted.ShouldContain(@"C:\Users\<user>\AppData\Local\PcDiagnostic\telemetry.db");
        redacted.ShouldNotContain("jane");
        redacted.ShouldContain("<email>");
        redacted.ShouldContain("<ip>");
        if (Environment.MachineName.Length >= 3)
        {
            redacted.ShouldNotContain(Environment.MachineName, Case.Insensitive);
        }

        ReportRedactor.Redact("loopback 127.0.0.1 stays").ShouldContain("127.0.0.1");
    }

    [Fact]
    public void The_same_problem_has_the_same_fingerprint_whatever_its_numbers()
    {
        const string Stack = "   at PcDiag.Collectors.Gpu.GpuCollector.CollectAsync() in X:\\a.cs:line 10\n   at PcDiag.Engine.Telemetry.TelemetryScheduler.TickAsync()";
        var a = ReportRedactor.Fingerprint("agent/GpuCollector", "System.IO.IOException", "Adapter 3 failed after 1200 ms", Stack);
        var b = ReportRedactor.Fingerprint("agent/GpuCollector", "System.IO.IOException", "Adapter 7 failed after 15 ms", Stack.Replace("line 10", "line 12", StringComparison.Ordinal));
        var other = ReportRedactor.Fingerprint("agent/GpuCollector", "System.InvalidOperationException", "Adapter 3 failed after 1200 ms", Stack);

        a.ShouldBe(b);
        a.ShouldNotBe(other);
        a.Length.ShouldBe(16);
    }

    [Fact]
    public void An_exception_becomes_a_redacted_cause_chain()
    {
        Exception error;
        try
        {
            throw new InvalidOperationException($@"Saving C:\Users\{Environment.UserName}\x failed", new IOException("Disk full"));
        }
        catch (InvalidOperationException ex)
        {
            error = ex;
        }

        var cause = ReportRedactor.Cause(error)!;
        cause.Type.ShouldBe("System.InvalidOperationException");
        cause.Message.ShouldContain(@"C:\Users\<user>\x");
        cause.Stack.ShouldNotBeNullOrEmpty();
        cause.Inner!.Type.ShouldBe("System.IO.IOException");
        ReportRedactor.Summarize("web/Api", error, "ignored").ShouldStartWith("web/Api: InvalidOperationException: Saving");
    }
}

public sealed class ProblemSpoolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-spool-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static ProblemReport Report(string id, string kind = "automatic") => new(
        id, kind, DateTimeOffset.UtcNow, "agent/Test", "agent/Test: it broke", null, null, new ProblemCause("System.Exception", "it broke", null, null, null),
        new ProductFacts("PC Diagnostic", "1.0", "agent", true, "0123456789abcdef0123456789abcdef"), ProblemFacts.Environment(), [], new Dictionary<string, string>(), id, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void Repeats_of_an_automatic_report_count_up_and_delivery_moves_it_to_sent()
    {
        var spool = new ProblemSpool(_root);
        spool.Write(Report("00aa11bb22cc33dd"));
        spool.Write(Report("00aa11bb22cc33dd")).Occurrences.ShouldBe(2);

        spool.MarkDelivery("00aa11bb22cc33dd", new ProblemDelivery(null, null, 1, "offline", DateTimeOffset.UtcNow.AddMinutes(5)));
        spool.Get("00aa11bb22cc33dd")!.Value.Delivery.LastError.ShouldBe("offline");

        spool.MarkDelivery("00aa11bb22cc33dd", new ProblemDelivery(DateTimeOffset.UtcNow, "Bug 42", 2, null, null));
        var sent = spool.Get("00aa11bb22cc33dd")!.Value;
        sent.Delivery.Reference.ShouldBe("Bug 42");
        File.Exists(Path.Combine(spool.Sent, "00aa11bb22cc33dd.json")).ShouldBeTrue();
        Directory.EnumerateFiles(spool.Outbox, "*.json").ShouldBeEmpty();

        spool.Delete("00aa11bb22cc33dd").ShouldBeTrue();
        spool.List().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"..\..\settings")]
    [InlineData("C:/Windows")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789")]
    public void Report_ids_are_never_paths(string id)
    {
        ProblemSpool.IsId(id).ShouldBeFalse();
        new ProblemSpool(_root).Get(id).ShouldBeNull();
    }
}
