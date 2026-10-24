using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Collectors.Recording;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Recording;
using PcDiag.Engine.Events;
using PcDiag.Engine.Recording;
using PcDiag.Platform.Windows.Power;
using Xunit.Abstractions;

namespace PcDiag.Collectors.IntegrationTests;

public sealed class RecorderHealthTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-health-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Real_shutdown_evidence_is_read_and_classified()
    {
        var source = new WindowsBootEvidenceSource(new EventRuleProvider(new DataPaths(_dataDirectory), NullLogger<EventRuleProvider>.Instance), TimeProvider.System);
        var lastBeat = DateTimeOffset.UtcNow.AddDays(-7);

        var events = await source.ReadSinceAsync(lastBeat, CancellationToken.None);

        source.BootTime.ShouldBeLessThan(DateTimeOffset.UtcNow);
        events.ShouldAllBe(e => e.Category == EventCategory.UnexpectedShutdown || e.Category == EventCategory.BugCheck || e.Category == EventCategory.Power);
        events.ShouldAllBe(e => e.Timestamp >= lastBeat);
        foreach (var e in events.Where(e => e.Category != EventCategory.Power))
        {
            output.WriteLine($"{e.Timestamp:u} {e.RuleId}: {e.Title}");
        }

        // As if a recording run had last been alive a week ago and never stopped cleanly.
        var recovery = BootRecoveryClassifier.Classify(new AgentHeartbeatState(lastBeat, "b", false, true), source.BootTime, events, DateTimeOffset.UtcNow);
        recovery.ShouldNotBeNull();
        output.WriteLine($"{recovery.Kind}: {recovery.Summary}");
        if (events.Any(e => e.Category == EventCategory.BugCheck))
        {
            recovery.Kind.ShouldBe(UncleanStopKind.BugCheck);
            recovery.Details.ShouldContainKey("bugcheck");
        }
    }

    [Fact]
    public void Sleep_and_session_notifications_are_available_in_the_interactive_session()
    {
        using var power = new SystemPowerEvents();

        power.UnavailableReason.ShouldBeNull();
    }
}
