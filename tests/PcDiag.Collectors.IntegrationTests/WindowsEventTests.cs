using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PcDiag.Collectors.Events;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Engine.Events;
using PcDiag.Platform.Windows.EventLog;
using Xunit.Abstractions;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>Runs the generated queries and the collector against this machine's real event logs.</summary>
public sealed partial class WindowsEventTests(ITestOutputHelper output) : IDisposable
{
    private static readonly EventRuleSet Rules = EventRuleSet.Load(EventRuleSet.LoadDefaultJson(), null);

    /// <summary>
    /// The channels this test run can read: some (the graphics kernel's Admin log) need administrator rights, which the
    /// recorder has and a test run usually not. The recorder logs a channel it cannot open and carries on.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<string>> Readable = new(() => [.. Rules.Channels.Where(channel =>
    {
        try
        {
            EventLogQueries.Read(channel, EventQueryBuilder.Build(Rules, channel, TimeSpan.FromMinutes(1))!, _ => { }, CancellationToken.None);
            return true;
        }
        catch (EventChannelException ex) when (ex.Problem == ChannelProblem.AccessDenied)
        {
            return false;
        }
    })]);
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-events-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Generated_queries_are_accepted_for_every_channel()
    {
        foreach (var channel in Readable.Value)
        {
            var live = EventQueryBuilder.Build(Rules, channel)!;
            var recent = EventQueryBuilder.Build(Rules, channel, TimeSpan.FromDays(7))!;
            var boot = EventQueryBuilder.Build(Rules, channel, TimeSpan.FromDays(7), sinceBootRulesOnly: true);

            var count = EventLogQueries.Read(channel, recent, _ => { }, CancellationToken.None);
            if (boot is not null)
            {
                EventLogQueries.Read(channel, boot, _ => { }, CancellationToken.None);
            }

            using var subscription = new EventLogSubscription(channel, live, _ => { }, ex => throw ex);
            subscription.Start();
            output.WriteLine($"{channel}: {count} candidate events in 7 days");
        }
    }

    [Fact]
    public void Suppressed_noise_is_filtered_by_windows()
    {
        const string dcom = "*[System[Provider[@Name='Microsoft-Windows-DistributedCOM'] and (EventID=10010 or EventID=10016) and TimeCreated[timediff(@SystemTime) &lt;= 2592000000]]]";
        var noise = EventLogQueries.Read("System", $"<QueryList><Query Id='0' Path='System'><Select Path='System'>{dcom}</Select></Query></QueryList>", _ => { }, CancellationToken.None);

        var leaked = 0;
        EventLogQueries.Read("System", EventQueryBuilder.Build(Rules, "System", TimeSpan.FromDays(30))!, view =>
        {
            var record = EventXml.Parse(view.ToXml())!;
            if (record.Provider == "Microsoft-Windows-DistributedCOM" && record.EventId is 10010 or 10016)
            {
                leaked++;
            }
        }, CancellationToken.None);

        output.WriteLine($"DCOM noise in 30 days: {noise}; delivered despite Suppress: {leaked}");
        leaked.ShouldBe(0);
    }

    [Fact]
    public void Windows_selection_and_the_classifier_agree_on_real_history()
    {
        var classifier = new EventClassifier(Rules, EventRedactor.None, 2000);
        var byRule = new Dictionary<string, int>();
        var unclassified = new List<EventRecordData>();
        var total = 0;

        foreach (var channel in Readable.Value)
        {
            total += EventLogQueries.Read(channel, EventQueryBuilder.Build(Rules, channel, TimeSpan.FromDays(30))!, view =>
            {
                var record = EventXml.Parse(view.ToXml()).ShouldNotBeNull();
                if (classifier.Classify(record, view.FormatMessage) is { } result)
                {
                    byRule[result.Rule.Id] = byRule.GetValueOrDefault(result.Rule.Id) + 1;
                }
                else
                {
                    unclassified.Add(record);
                }
            }, CancellationToken.None);
        }

        foreach (var (rule, count) in byRule.OrderByDescending(r => r.Value))
        {
            output.WriteLine($"{count,6}  {rule}");
        }

        output.WriteLine($"{total} selected by Windows, {unclassified.Count} dropped by field conditions");

        // Windows can only pre-filter by provider, ID and level; anything it selects but the classifier drops must
        // have been dropped by a field condition (e.g. 0 ms time adjustments).
        unclassified.ShouldAllBe(r => Rules.Rules.Any(rule => rule.Rule.When.Count > 0 && SelectedBy(rule.Rule, r)));
    }

    [Fact]
    public async Task Collector_backfills_recent_events_without_duplicates()
    {
        var collector = Collector(new EventOptions { BackfillMinutes = 1440 });
        (await collector.InitializeAsync(CancellationToken.None)).Availability.ShouldBe(Core.Telemetry.Availability.Available);
        var expected = Readable.Value.Sum(c => EventLogQueries.Read(c, EventQueryBuilder.Build(Rules, c, TimeSpan.FromDays(1))!, _ => { }, CancellationToken.None));

        var events = new List<SystemEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await foreach (var e in collector.ReadEventsAsync(cts.Token))
            {
                events.Add(e);
            }
        }
        catch (OperationCanceledException)
        {
        }

        output.WriteLine($"{expected} candidates in the last 24 h; {events.Count} recorded");
        events.Where(e => e.RecordId is not null).GroupBy(e => (e.Channel, e.RecordId)).ShouldAllBe(g => g.Count() == 1);
        events.ShouldAllBe(e => e.RuleId != null);
        if (expected > 0)
        {
            events.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public async Task Live_events_are_delivered_by_the_subscription()
    {
        // Writes one informational event from the source "PcDiagnostic.Tests" to the Application log, so it only
        // runs when asked: set PCDIAG_EVENTLOG_WRITE_TESTS=1.
        if (Environment.GetEnvironmentVariable("PCDIAG_EVENTLOG_WRITE_TESTS") != "1")
        {
            output.WriteLine("Skipped: set PCDIAG_EVENTLOG_WRITE_TESTS=1 to write a test event to the Application log.");
            return;
        }

        Directory.CreateDirectory(_dataDirectory);
        File.WriteAllText(Path.Combine(_dataDirectory, "event-rules.json"), """
            { "version": 1, "rules": [
              { "id": "test.live", "channel": "Application", "providers": ["PcDiagnostic.Tests"], "eventIds": [4242],
                "category": "Other", "severity": "Info", "title": "Live test {marker}", "fields": { "marker": "#0" } }
            ] }
            """);
        var collector = Collector(new EventOptions { BackfillMinutes = 0, BootBackfillMaxHours = 0 });
        var marker = Guid.NewGuid().ToString("N");

        var received = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await foreach (var e in collector.ReadEventsAsync(cts.Token))
            {
                if (e.RuleId == "test.live" && e.Title.EndsWith(marker, StringComparison.Ordinal))
                {
                    return e;
                }
            }

            return null;
        });

        await Task.Delay(1500); // let the subscription start
        WriteApplicationEvent("PcDiagnostic.Tests", 4242, marker);

        var e = (await received).ShouldNotBeNull();
        e.Channel.ShouldBe("Application");
        e.Level.ShouldBe(EventLevel.Information);
        e.RecordId.ShouldNotBeNull();
        output.WriteLine($"Received record {e.RecordId}: {e.Title}");
    }

    private static bool SelectedBy(EventRule rule, EventRecordData r) =>
        rule.Channel.Equals(r.Channel, StringComparison.OrdinalIgnoreCase)
        && (rule.Providers.Count == 0 || rule.Providers.Contains(r.Provider, StringComparer.OrdinalIgnoreCase))
        && (rule.EventIds.Count == 0 || rule.EventIds.Contains(r.EventId))
        && (rule.Levels.Count == 0 || rule.Levels.Contains(r.Level));

    private WindowsEventLogCollector Collector(EventOptions options) => new(
        new EventRuleProvider(new DataPaths(_dataDirectory), NullLogger<EventRuleProvider>.Instance),
        new Static<EventOptions>(options),
        new Static<PrivacyOptions>(new PrivacyOptions()),
        TimeProvider.System,
        NullLogger<WindowsEventLogCollector>.Instance);

    private static unsafe void WriteApplicationEvent(string source, uint eventId, string text)
    {
        const ushort informationType = 0x0004;
        var handle = RegisterEventSource(null, source);
        handle.ShouldNotBe(nint.Zero);
        try
        {
            fixed (char* p = text)
            {
                var strings = stackalloc nint[1];
                strings[0] = (nint)p;
                ReportEvent(handle, informationType, 0, eventId, nint.Zero, 1, 0, strings, nint.Zero).ShouldBeTrue();
            }
        }
        finally
        {
            DeregisterEventSource(handle);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegisterEventSourceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint RegisterEventSource(string? uncServerName, string sourceName);

    [LibraryImport("advapi32.dll", EntryPoint = "ReportEventW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReportEvent(nint eventLog, ushort type, ushort category, uint eventId, nint userSid, ushort numStrings, uint dataSize, nint* strings, nint rawData);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeregisterEventSource(nint eventLog);

    private sealed class Static<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
