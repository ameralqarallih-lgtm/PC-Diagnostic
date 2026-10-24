using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.History;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Storage.Sqlite.Incidents;
using PcDiag.Storage.Sqlite;
using PcDiag.Web.Agent;
using PcDiag.Web.Diagnostics;
using PcDiag.Web.Endpoints;
using PcDiag.Web.Security;

namespace PcDiag.Web.Tests;

public sealed class HistoryApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAgentClient _agent = new() { Connected = true };
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private static readonly string[] NotAKey = ["../etc"];
    private static readonly string[] UnknownIncident = ["INC-20200101-00001"];

    public HistoryApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Storage:DataDirectory", _dataDirectory);
            builder.ConfigureTestServices(services => services.AddSingleton<IAgentClient>(_agent));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        new SqliteConnectionFactory().ReleasePooledConnectionsUnder(_dataDirectory);
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private DataPaths Paths => _factory.Services.GetRequiredService<DataPaths>();

    private async Task ExecAsync(string sql, params object[] parameters)
    {
        await using var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenReadWriteAsync(Paths.TelemetryDatabase, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"$p{i}", parameters[i]);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> SeedAsync()
    {
        var start = _now.AddMinutes(-5).ToUnixTimeMilliseconds();
        for (var s = 0; s < 300; s++)
        {
            await ExecAsync("INSERT INTO SystemSnapshots (ts, seq, cpu_pct) VALUES ($p0, $p1, $p2);", start + (s * 1000L), s, 25.0);
        }

        await ExecAsync("INSERT INTO WindowsEvents (ts, channel, record_id, provider, event_id, level, category, severity, title) VALUES ($p0, 'System', 1, 'Kernel-Power', 41, 1, 12, 4, 'Unexpected restart');", start + 5000);
        await ExecAsync("INSERT INTO SystemInformation (key, value, updated_ts) VALUES ('cpu.name', 'AMD Ryzen 9 7950X', $p0);", start);

        var services = _factory.Services;
        var store = new IncidentStore(Paths, services.GetRequiredService<SqliteConnectionFactory>(), services.GetRequiredService<DatabaseInitializer>(), TimeProvider.System);
        var incident = await store.CreateAsync(
            new IncidentDraft(_now, _now, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), IncidentSource.User, UserIncidentType.ScreenFlicker, "Screen Flicker", null, Severity.Medium,
                false, new IncidentTrigger(_now, IncidentSource.User, UserIncidentType.ScreenFlicker), "1.0.0"),
            CancellationToken.None);
        await store.CaptureWindowAsync(incident, _now.AddMinutes(-10), _now, [], CancellationToken.None);
        await store.FinalizeAsync(incident, IncidentState.Complete, null, CancellationToken.None);
        return incident.Key.ToString();
    }

    [Fact]
    public async Task History_has_a_catalog_series_in_buckets_and_events()
    {
        await SeedAsync();
        using var client = await SignedInClientAsync();
        var from = _now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var to = _now.ToUnixTimeMilliseconds();

        var catalog = await client.GetFromJsonAsync<List<SeriesInfo>>("/api/v1/history/catalog", Json);
        var series = await client.GetFromJsonAsync<HistoryResult>($"/api/v1/history/series?ids=cpu,bogus&from={from}&to={to}&points=30", Json);
        var events = await client.GetFromJsonAsync<List<TimelineEntry>>($"/api/v1/history/events?from={from}&to={to}", Json);

        catalog!.ShouldContain(s => s.Id == "cpu");
        series!.Series.ShouldHaveSingleItem().Id.ShouldBe("cpu");
        series.Series[0].Points.Count.ShouldBeInRange(29, 31);
        series.Series[0].Points[0][2].ShouldBe(25);
        events!.ShouldHaveSingleItem().Title.ShouldBe("Unexpected restart");
    }

    /// <summary>
    /// A moment with no incident: its values and the processes running then. A process with no end time counts only
    /// while it is still running on this PC (this test host does; process ID 4_000_000_000 does not exist).
    /// </summary>
    [Fact]
    public async Task A_moment_lists_its_values_and_the_processes_still_running()
    {
        await SeedAsync();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var selfStart = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();
        var at = Math.Max(_now.AddSeconds(-1).ToUnixTimeMilliseconds(), selfStart + 100); // this test host was running then
        await ExecAsync("INSERT INTO RecorderSessions (start_ts, start_reason, elevated, app_version) VALUES ($p0, 0, 0, '1.0');", _now.AddMinutes(-5).ToUnixTimeMilliseconds());
        await ExecAsync("INSERT INTO ProcessIdentity (proc_key, pid, name, start_ts) VALUES (1, $p0, 'testhost.exe', $p1), (2, 2147483000, 'ended-unseen.exe', $p1);", Environment.ProcessId, selfStart);
        using var client = await SignedInClientAsync();

        var moment = await client.GetFromJsonAsync<HistoryMoment>($"/api/v1/history/moment?t={at}", Json);

        moment!.Recorded.ShouldBeTrue();
        moment.Values.ShouldContain(v => v.Id == "cpu" && v.Value == 25);
        moment.Processes.ShouldHaveSingleItem().ShouldSatisfyAllConditions(p => p.Name.ShouldBe("testhost.exe"), p => p.Measured.ShouldBeFalse());
    }

    [Fact]
    public async Task A_diagnostic_runs_in_the_background_is_kept_and_survives_a_restart()
    {
        await SeedAsync();
        await ExecAsync("INSERT INTO RecorderSessions (start_ts, start_reason, elevated, app_version) VALUES ($p0, 0, 0, '1.0');", _now.AddMinutes(-5).ToUnixTimeMilliseconds());
        using var client = await SignedInClientAsync();
        var period = new { from = _now.AddMinutes(-5).ToUnixTimeMilliseconds(), to = _now.ToUnixTimeMilliseconds() };

        using var started = await SendAsync(client, HttpMethod.Post, "/api/v1/diagnostics/jobs", period);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = (await started.Content.ReadFromJsonAsync<DiagnosticJob>(Json))!;
        await _factory.Services.GetRequiredService<DiagnosticJobs>().WaitAsync(job.Id);

        var detail = await client.GetFromJsonAsync<DiagnosticJobDetail>($"/api/v1/diagnostics/jobs/{job.Id}", Json);
        detail!.Job.State.ShouldBe(DiagnosticJobState.Completed);
        detail.Job.FindingsBySeverity.ShouldNotBeNull()["High"].ShouldBeGreaterThanOrEqualTo(1);
        detail.Report.ShouldNotBeNull().Sections.ShouldContain(s => s.Id == "system");
        File.Exists(Path.Combine(_dataDirectory, "diagnostics", job.Id + ".json")).ShouldBeTrue();

        // The same period again: the report just made, not a second run.
        using var again = await SendAsync(client, HttpMethod.Post, "/api/v1/diagnostics/jobs", period);
        (await again.Content.ReadFromJsonAsync<DiagnosticJob>(Json))!.Id.ShouldBe(job.Id);
        (await client.GetFromJsonAsync<List<DiagnosticJob>>("/api/v1/diagnostics/jobs", Json))!.ShouldHaveSingleItem().Id.ShouldBe(job.Id);
        using var html = await client.GetAsync($"/api/v1/diagnostics/jobs/{job.Id}/export?format=html");
        (await html.Content.ReadAsStringAsync()).ShouldContain("Unexpected restart");

        // Another Web host on the same data finds it.
        using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Storage:DataDirectory", _dataDirectory);
            builder.ConfigureTestServices(services => services.AddSingleton<IAgentClient>(_agent));
        });
        using var other = restarted.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        (await other.GetAsync("/auth/exchange?t=" + restarted.Services.GetRequiredService<LaunchTokenService>().IssueToken())).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var kept = await other.GetFromJsonAsync<DiagnosticJobDetail>($"/api/v1/diagnostics/jobs/{job.Id}", Json);
        kept!.Job.State.ShouldBe(DiagnosticJobState.Completed);
        kept.Report.ShouldNotBeNull().Summary.Top.ShouldContain(f => f.Title == "Unexpected restart");

        // Forgetting it removes the saved report.
        (await SendAsync(other, HttpMethod.Delete, $"/api/v1/diagnostics/jobs/{job.Id}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(_dataDirectory, "diagnostics", job.Id + ".json")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_diagnostic_of_chosen_incidents_covers_their_recorded_windows()
    {
        var key = await SeedAsync();
        using var client = await SignedInClientAsync();
        var detail = (await client.GetFromJsonAsync<IncidentDetail>($"/api/v1/incidents/{key}", Json))!;

        using var started = await SendAsync(client, HttpMethod.Post, "/api/v1/diagnostics/jobs", new { incidents = new[] { key } });
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = (await started.Content.ReadFromJsonAsync<DiagnosticJob>(Json))!;
        job.Incidents.ShouldBe([key]);
        job.From.ToUnixTimeMilliseconds().ShouldBe(detail.From);
        // Its window still runs on (it records 2 minutes after): the diagnostic ends now, not in the future.
        ((double)job.To.ToUnixTimeMilliseconds()).ShouldBe(Math.Min(detail.To, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), 5_000);
        await _factory.Services.GetRequiredService<DiagnosticJobs>().WaitAsync(job.Id);

        // Unknown or malformed incidents are refused.
        (await SendAsync(client, HttpMethod.Post, "/api/v1/diagnostics/jobs", new { incidents = UnknownIncident })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Post, "/api/v1/diagnostics/jobs", new { incidents = NotAKey })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_waiting_diagnostic_can_be_cancelled()
    {
        using var client = await SignedInClientAsync();
        var jobs = _factory.Services.GetRequiredService<DiagnosticJobs>();
        await DiagnosticJobs.Gate.WaitAsync(); // another diagnostic is running
        DiagnosticJob job;
        try
        {
            using var started = await SendAsync(client, HttpMethod.Post, "/api/v1/diagnostics/jobs",
                new { from = _now.AddHours(-2).ToUnixTimeMilliseconds(), to = _now.AddHours(-1).ToUnixTimeMilliseconds() });
            job = (await started.Content.ReadFromJsonAsync<DiagnosticJob>(Json))!;
            job.State.ShouldBe(DiagnosticJobState.Queued);

            (await SendAsync(client, HttpMethod.Delete, $"/api/v1/diagnostics/jobs/{job.Id}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            await jobs.WaitAsync(job.Id);
        }
        finally
        {
            DiagnosticJobs.Gate.Release();
        }

        var detail = await client.GetFromJsonAsync<DiagnosticJobDetail>($"/api/v1/diagnostics/jobs/{job.Id}", Json);
        (detail!.Job.State, detail.Job.Error, detail.Report).ShouldBe((DiagnosticJobState.Cancelled, "Cancelled.", (PeriodReport?)null));
    }

    [Theory]
    [InlineData("20261008-120000-abcdef")]
    [InlineData("..%2F..%2Fsettings")]
    public async Task Unknown_diagnostics_are_404(string id)
    {
        using var client = await SignedInClientAsync();

        (await client.GetAsync($"/api/v1/diagnostics/jobs/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Delete, $"/api/v1/diagnostics/jobs/{id}", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_period_is_diagnosed_and_the_report_downloaded_as_shown()
    {
        await SeedAsync();
        await ExecAsync("INSERT INTO RecorderSessions (start_ts, start_reason, elevated, app_version) VALUES ($p0, 0, 0, '1.0');", _now.AddMinutes(-5).ToUnixTimeMilliseconds());
        using var client = await SignedInClientAsync();
        var from = _now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var to = _now.ToUnixTimeMilliseconds();

        var report = await client.GetFromJsonAsync<PeriodReport>($"/api/v1/diagnostics/period?from={from}&to={to}", Json);

        report!.Sections.Select(s => s.Id).ShouldBe(["coverage", "incidents", "cpu", "memory", "gpu", "storage", "network", "processes", "services", "display", "system", "sensors", "anomalies"]);
        report.Sections.Single(s => s.Id == "cpu").Stats.ShouldContain(s => s.Label == "Utilization" && s.Value.StartsWith("average 25 %", StringComparison.Ordinal));
        var restart = report.Sections.Single(s => s.Id == "system").Findings.ShouldHaveSingleItem();
        (restart.Title, restart.Severity, restart.Level).ShouldBe(("Unexpected restart", Severity.High, EvidenceLevel.ConfirmedEvent));
        report.Summary.Top.ShouldContain(f => f.Title == "Unexpected restart" && f.Section == "system");

        // The downloads are the report on screen (kept a few minutes), as a self-contained page or JSON.
        using var html = await client.GetAsync($"/api/v1/diagnostics/period/export?from={from}&to={to}&format=html");
        html.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        html.Content.Headers.ContentDisposition!.FileName.ShouldNotBeNull().ShouldStartWith("period-");
        var page = await html.Content.ReadAsStringAsync();
        page.ShouldContain("Content-Security-Policy");
        page.ShouldContain("Unexpected restart");
        page.ShouldNotContain("<script", Case.Insensitive);
        var json = await client.GetFromJsonAsync<PeriodReport>($"/api/v1/diagnostics/period/export?from={from}&to={to}&format=json", Json);
        json!.GeneratedAt.ShouldBe(report.GeneratedAt);

        // Run again: analyzed afresh.
        var fresh = await client.GetFromJsonAsync<PeriodReport>($"/api/v1/diagnostics/period?from={from}&to={to}&fresh=true", Json);
        fresh!.GeneratedAt.ShouldBeGreaterThanOrEqualTo(report.GeneratedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?from=2000000&to=1000000")]
    [InlineData("?from=1000000&to=1030000")]
    [InlineData("?from=1000000&to=2780000000")]
    public async Task Invalid_periods_are_refused(string query)
    {
        using var client = await SignedInClientAsync();

        using var response = await client.GetAsync($"/api/v1/diagnostics/period{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["title"]!.GetValue<string>().ShouldBe("Invalid period");
    }

    [Fact]
    public async Task Unknown_report_formats_are_refused()
    {
        using var client = await SignedInClientAsync();

        using var response = await client.GetAsync("/api/v1/diagnostics/period/export?from=1000000&to=2000000&format=docx");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_incident_can_be_investigated()
    {
        var key = await SeedAsync();
        using var client = await SignedInClientAsync();

        var list = await client.GetFromJsonAsync<IncidentPage>("/api/v1/incidents?type=ScreenFlicker", Json);
        var detail = await client.GetFromJsonAsync<IncidentDetail>($"/api/v1/incidents/{key}", Json);
        var timeline = await client.GetFromJsonAsync<List<TimelineEntry>>($"/api/v1/incidents/{key}/timeline", Json);
        var series = await client.GetFromJsonAsync<HistoryResult>($"/api/v1/incidents/{key}/series?ids=cpu", Json);
        var processes = await client.GetFromJsonAsync<ProcessesAtTime>($"/api/v1/incidents/{key}/processes", Json);
        var findings = await client.GetFromJsonAsync<List<FindingEntry>>($"/api/v1/incidents/{key}/findings", Json);
        var system = await client.GetFromJsonAsync<SystemOverview>($"/api/v1/incidents/{key}/system", Json);

        list!.Items.ShouldHaveSingleItem().Key.ShouldBe(key);
        detail!.Summary.Title.ShouldBe("Screen Flicker");
        detail.HasData.ShouldBeTrue();
        timeline!.ShouldContain(e => e.Title == "Unexpected restart");
        series!.Series.ShouldHaveSingleItem().Points.ShouldNotBeEmpty(); // the copied window
        processes!.Processes.ShouldBeEmpty();
        findings!.ShouldBeEmpty();
        system!.Information["cpu.name"].ShouldBe("AMD Ryzen 9 7950X");
    }

    [Theory]
    [InlineData("INC-20990101-00001")]
    [InlineData("not-a-key")]
    public async Task Unknown_incidents_are_404(string key)
    {
        using var client = await SignedInClientAsync();

        (await client.GetAsync($"/api/v1/incidents/{key}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/v1/incidents/{key}/timeline")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Edits_and_deletes_are_carried_out_by_the_agent()
    {
        using var client = await SignedInClientAsync();

        (await SendAsync(client, HttpMethod.Patch, "/api/v1/incidents/INC-20261006-00001", new { notes = "after the driver update", pinned = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var args = _agent.Requests.Single(r => r.Command == AgentCommands.UpdateIncident).Args!;
        (args.IncidentKey, args.Note, args.Pinned).ShouldBe(("INC-20261006-00001", "after the driver update", (bool?)true));

        (await SendAsync(client, HttpMethod.Patch, "/api/v1/incidents/INC-20261006-00001", new { })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(client, HttpMethod.Patch, "/api/v1/incidents/INC-20261006-00001", new { title = new string('x', 121) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await SendAsync(client, HttpMethod.Delete, "/api/v1/incidents/INC-20261006-00001", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _agent.Refuse = "No such incident.";
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/incidents/INC-20261006-00001", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _agent.Refuse = "The incident is still recording; delete it when it is complete.";
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/incidents/INC-20261006-00001", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        _agent.Connected = false;
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/incidents/INC-20261006-00001", null)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task The_system_page_reads_the_live_database()
    {
        await SeedAsync();
        using var client = await SignedInClientAsync();

        var system = await client.GetFromJsonAsync<SystemOverview>("/api/v1/system", Json);

        system!.Information["cpu.name"].ShouldBe("AMD Ryzen 9 7950X");
        system.IoDevices.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Fact]
    public async Task Board_sensors_and_device_changes_have_their_own_light_reads()
    {
        await SeedAsync();
        var ts = _now.ToUnixTimeMilliseconds();
        await ExecAsync("INSERT INTO SensorCatalog (sensor_id, provider, kind, name, unit, logical_key) VALUES (1, 'lhm', 'Voltage', 'Nuvoton NCT6701D / CMOS Battery', 'V', 'lhm/lpc/nct6701d/0/voltage/8'), (2, 'lhm', 'Temperature', 'CPU / Package', 'C', 'lhm/intelcpu/0/temperature/0');");
        await ExecAsync("INSERT INTO SensorReadings (ts, sensor_id, value) VALUES ($p0, 1, 3.07), ($p0, 2, 55);", ts);
        await ExecAsync(
            "INSERT INTO WindowsEvents (ts, channel, provider, event_id, level, category, severity, title, related_device, data_json) VALUES ($p0, 'pcdiag.devices', 'PcDiagnostic', 1, 4, 10, 0, 'Device connected: USB Receiver', 'USB Receiver', '{\"vendorId\":\"046D\"}');",
            ts);
        using var client = await SignedInClientAsync();

        var board = await client.GetFromJsonAsync<SensorInfo[]>("/api/v1/system/sensors?prefix=lhm/lpc/", Json);
        board.ShouldHaveSingleItem().Value.ShouldBe(3.07);
        (await client.GetAsync("/api/v1/system/sensors?prefix=lhm/lpc/%27%20OR%201")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var changes = await client.GetFromJsonAsync<DeviceEventInfo[]>("/api/v1/system/device-events", Json);
        var connected = changes.ShouldHaveSingleItem();
        connected.Title.ShouldBe("Device connected: USB Receiver");
        connected.Data!.Value.GetProperty("vendorId").GetString().ShouldBe("046D");
    }

    [Fact]
    public async Task Settings_are_validated_and_written_without_losing_other_sections()
    {
        Directory.CreateDirectory(_dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(_dataDirectory, "settings.json"),
            """
            {
              // user comment
              "Storage": { "DataDirectory": null, "TelemetryRetentionHours": 48 },
              "Custom": { "Keep": true }
            }
            """);
        using var client = await SignedInClientAsync();

        var current = await client.GetFromJsonAsync<JsonObject>("/api/v1/settings", Json);
        current!["settings"]!["storage"]!["telemetryRetentionHours"]!.GetValue<int>().ShouldBe(48);
        current["settings"]!["recorder"]!["preIncidentMinutes"]!.GetValue<int>().ShouldBe(10);

        var settings = current["settings"]!.AsObject();
        settings["recorder"]!["mode"] = "Always";
        settings["privacy"]!["captureCommandLines"] = true;
        var saved = await SendAsync(client, HttpMethod.Put, "/api/v1/settings", settings);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);

        var file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dataDirectory, "settings.json")))!;
        file["Recorder"]!["Mode"]!.GetValue<string>().ShouldBe("Always");
        file["Privacy"]!["CaptureCommandLines"]!.GetValue<bool>().ShouldBeTrue();
        file["Storage"]!["TelemetryRetentionHours"]!.GetValue<int>().ShouldBe(48);
        file["Custom"]!["Keep"]!.GetValue<bool>().ShouldBeTrue();

        settings["recorder"]!["preIncidentMinutes"] = 7;
        var refused = await SendAsync(client, HttpMethod.Put, "/api/v1/settings", settings);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("PreIncidentMinutes");
        JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dataDirectory, "settings.json")))!["Recorder"]!["PreIncidentMinutes"]!.GetValue<int>().ShouldBe(10);
    }

    [Fact]
    public async Task Triggers_describe_the_rules_and_settings_change_only_what_differs()
    {
        using var client = await SignedInClientAsync();

        var rules = (await client.GetFromJsonAsync<JsonArray>("/api/v1/triggers", Json))!;
        rules.Count.ShouldBeGreaterThan(15);
        var cpu = rules.Single(r => r!["id"]!.GetValue<string>() == "cpu-high")!;
        cpu["description"]!.GetValue<string>().ShouldBe("CPU utilization above 95 % for at least 30 s");
        cpu["unit"]!.GetValue<string>().ShouldBe("%");
        cpu["defaults"]!["createIncident"]!.GetValue<bool>().ShouldBeFalse();
        cpu["usesDuration"]!.GetValue<bool>().ShouldBeTrue();

        var settings = (await client.GetFromJsonAsync<JsonObject>("/api/v1/settings", Json))!["settings"]!.AsObject();
        settings["triggers"]!["maxIncidentsPerHour"] = 6;
        settings["triggers"]!["rules"] = new JsonObject
        {
            ["cpu-high"] = new JsonObject { ["threshold"] = 90, ["createIncident"] = false, ["durationSeconds"] = 30 },
            ["gpu-reset"] = new JsonObject { ["enabled"] = false },
        };
        (await SendAsync(client, HttpMethod.Put, "/api/v1/settings", settings)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dataDirectory, "settings.json")))!["Triggers"]!;
        file["MaxIncidentsPerHour"]!.GetValue<int>().ShouldBe(6);
        file["Rules"]!["cpu-high"]!.ToJsonString().ShouldBe("""{"Threshold":90}""", "values equal to the shipped ones are not stored");
        file["Rules"]!["gpu-reset"]!["Enabled"]!.GetValue<bool>().ShouldBeFalse();
        (await client.GetFromJsonAsync<JsonArray>("/api/v1/triggers", Json))!.Single(r => r!["id"]!.GetValue<string>() == "cpu-high")!["description"]!.GetValue<string>()
            .ShouldBe("CPU utilization above 90 % for at least 30 s");

        settings["triggers"]!["rules"] = new JsonObject { ["no-such-rule"] = new JsonObject { ["enabled"] = true }, ["memory-low"] = new JsonObject { ["threshold"] = 900 } };
        var refused = await SendAsync(client, HttpMethod.Put, "/api/v1/settings", settings);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await refused.Content.ReadAsStringAsync();
        problem.ShouldContain("no-such-rule is not a known rule");
        problem.ShouldContain("memory-low:Threshold must be between 0.1 GB and 512 GB");
    }

    [Fact]
    public async Task Users_add_their_own_rules_and_delete_shipped_ones()
    {
        using var client = await SignedInClientAsync();
        var choices = (await client.GetFromJsonAsync<JsonObject>("/api/v1/triggers/choices", Json))!;
        choices["metrics"]!.AsArray().ShouldContain(m => m!["id"]!.GetValue<string>() == "gpu.vram");
        choices["windowsEvents"]!.AsArray().ShouldContain(e => e!["id"]!.GetValue<string>() == "gpu.tdr");
        choices["categories"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()).ShouldContain("graphics");

        // Every reading the recorder has seen, numbers only (texts such as the clock are left out).
        await File.WriteAllTextAsync(Path.Combine(_dataDirectory, "overlay-sources.json"), """
            [{"id":"sensor/lpc/fan/1","label":"CPU fan","group":"Nuvoton NCT6701D","unit":"RPM","value":null},
             {"id":"time.clock","label":"Time","group":"This PC","unit":"","value":null,"text":"12:00","room":["00:00"]}]
            """);
        var readings = (await client.GetFromJsonAsync<JsonArray>("/api/v1/triggers/readings", Json))!;
        readings.ShouldHaveSingleItem()!["label"]!.GetValue<string>().ShouldBe("CPU fan");
        var suggestions = (await client.GetFromJsonAsync<JsonObject>("/api/v1/triggers/suggestions", Json))!;
        suggestions["processes"]!.AsArray().Count.ShouldBeGreaterThan(0);
        suggestions["channels"]!.AsArray().Select(c => c!.GetValue<string>()).ShouldContain("System");

        var settings = (await client.GetFromJsonAsync<JsonObject>("/api/v1/settings", Json))!["settings"]!.AsObject();
        settings["triggers"]!["removed"] = new JsonArray("gpu-vram-full");
        settings["triggers"]!["custom"] = new JsonArray(new JsonObject
        {
            ["id"] = "my-ram", ["name"] = "RAM almost gone", ["kind"] = "ThresholdDuration", ["metric"] = "memory.available", ["direction"] = "Below",
            ["threshold"] = 2, ["durationSeconds"] = 120, ["windowSeconds"] = 10, ["eventRules"] = new JsonArray(), ["processEvents"] = new JsonArray(),
            ["displayChanges"] = new JsonArray(), ["severity"] = "High", ["enabled"] = true, ["createIncident"] = true, ["cooldownMinutes"] = 30,
        });
        (await SendAsync(client, HttpMethod.Put, "/api/v1/settings", settings)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var rules = (await client.GetFromJsonAsync<JsonArray>("/api/v1/triggers", Json))!;
        var mine = rules.Single(r => r!["id"]!.GetValue<string>() == "my-ram")!;
        mine["custom"]!.GetValue<bool>().ShouldBeTrue();
        mine["description"]!.GetValue<string>().ShouldBe("Available memory below 2.0 GB for at least 2 min");
        rules.Single(r => r!["id"]!.GetValue<string>() == "gpu-vram-full")!["removed"]!.GetValue<bool>().ShouldBeTrue();

        // A rule that cannot work is refused, saying why.
        settings["triggers"]!["custom"]![0]!["threshold"] = 9999;
        var refused = await SendAsync(client, HttpMethod.Put, "/api/v1/settings", settings);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("RAM almost gone");
    }

    [Fact]
    public async Task Anomalies_are_listed_with_their_reasoning()
    {
        using var client = await SignedInClientAsync();
        var store = new PcDiag.Storage.Sqlite.Telemetry.AnomalyStore(Paths, _factory.Services.GetRequiredService<SqliteConnectionFactory>());
        await store.AddAsync(new PcDiag.Core.Anomalies.Anomaly(0, "memory-low", "memory.available", _now.AddMinutes(-5), null, 0.6, PcDiag.Core.Events.Severity.High,
            new PcDiag.Core.Anomalies.AnomalyReasoning("Memory pressure", "Available memory below 1.0 GB for at least 5 s", null, null, null, "0.6 GB", TimeSpan.FromSeconds(12))),
            CancellationToken.None);

        var anomalies = (await client.GetFromJsonAsync<JsonArray>("/api/v1/anomalies", Json))!;

        var anomaly = anomalies.ShouldHaveSingleItem()!;
        anomaly["classification"]!.GetValue<string>().ShouldBe("Memory pressure");
        anomaly["unit"]!.GetValue<string>().ShouldBe("GB");
        anomaly["severity"]!.GetValue<string>().ShouldBe("High");
        anomaly["summary"]!.GetValue<string>().ShouldBe("Current 0.6 GB; for 12 s. Rule: Available memory below 1.0 GB for at least 5 s. Classification: Memory pressure.");
        (await client.GetAsync("/api/v1/incidents/INC-20990101-00001/anomalies")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        request.Headers.Add("Origin", "http://localhost");
        return client.SendAsync(request);
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var token = _factory.Services.GetRequiredService<LaunchTokenService>().IssueToken();
        (await client.GetAsync("/auth/exchange?t=" + token)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }

    [Fact]
    public async Task The_storage_page_measures_the_data_and_cleans_up_and_deletes_through_the_recorder()
    {
        var key = await SeedAsync();
        using var client = await SignedInClientAsync();

        var storage = await client.GetFromJsonAsync<JsonElement>("/api/v1/storage", Json);

        storage.GetProperty("folder").GetString().ShouldBe(Paths.Root);
        storage.GetProperty("drive").GetProperty("freeBytes").GetInt64().ShouldBeGreaterThan(0);
        var kinds = storage.GetProperty("kinds").EnumerateArray().ToDictionary(k => k.GetProperty("id").GetString()!, k => k.GetProperty("bytes").GetInt64());
        kinds["telemetry"].ShouldBeGreaterThan(0);
        kinds["incidents"].ShouldBeGreaterThan(0);
        storage.GetProperty("totalBytes").GetInt64().ShouldBe(kinds.Values.Sum());
        var incident = storage.GetProperty("incidents").EnumerateArray().ShouldHaveSingleItem();
        incident.GetProperty("key").GetString().ShouldBe(key);
        incident.GetProperty("bytes").GetInt64().ShouldBe(kinds["incidents"]);
        incident.GetProperty("state").GetString().ShouldBe("Complete");
        storage.GetProperty("telemetry").GetProperty("fullDetailFrom").GetDateTimeOffset().ShouldBeLessThan(_now);
        var limits = storage.GetProperty("limits");
        (limits.GetProperty("dataLimitGb").GetInt32(), limits.GetProperty("keepFreeGb").GetInt32(), limits.GetProperty("fullDetailFloorHours").GetDouble()).ShouldBe((10, 5, 2.0));
        storage.GetProperty("recorder").GetProperty("condition").GetString().ShouldBe("Normal");
        storage.GetProperty("recent")[0].GetProperty("kind").GetString().ShouldBe("full-detail-shortened");

        var clean = new HttpRequestMessage(HttpMethod.Post, "/api/v1/storage/clean") { Content = JsonContent.Create(new { compact = true }) };
        clean.Headers.Add("Origin", "http://localhost");
        var cleaned = await (await client.SendAsync(clean)).Content.ReadFromJsonAsync<JsonElement>(Json);
        cleaned.GetProperty("freedBytes").GetInt64().ShouldBe(2L << 20);
        _agent.Requests.Last(r => r.Command == AgentCommands.StorageClean).Args!.Compact.ShouldBe(true);

        var bad = new HttpRequestMessage(HttpMethod.Post, "/api/v1/storage/incidents/delete") { Content = JsonContent.Create(new { keys = NotAKey }) };
        bad.Headers.Add("Origin", "http://localhost");
        (await client.SendAsync(bad)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var delete = new HttpRequestMessage(HttpMethod.Post, "/api/v1/storage/incidents/delete") { Content = JsonContent.Create(new { keys = new[] { key } }) };
        delete.Headers.Add("Origin", "http://localhost");
        var result = await (await client.SendAsync(delete)).Content.ReadFromJsonAsync<JsonElement>(Json);
        result.GetProperty("deleted").EnumerateArray().Select(k => k.GetString()).ShouldBe([key]);
        _agent.Requests.Last(r => r.Command == AgentCommands.DeleteIncident).Args!.IncidentKey.ShouldBe(key);
    }

    [Fact]
    public async Task Without_the_recorder_the_storage_page_still_measures_and_says_why()
    {
        _agent.Connected = false;
        using var client = await SignedInClientAsync();
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Paths.StorageLogFile, """[{"at":"2026-10-09T10:00:00+00:00","kind":"compacted","detail":"compacted","freedBytes":5,"automatic":true,"count":1}]""");

        var storage = await client.GetFromJsonAsync<JsonElement>("/api/v1/storage", Json);

        storage.GetProperty("recorder").ValueKind.ShouldBe(JsonValueKind.Null);
        storage.GetProperty("recorderProblem").GetString().ShouldBe(_agent.UnavailableReason);
        storage.GetProperty("recent")[0].GetProperty("detail").GetString().ShouldBe("compacted");
    }

    [Fact]
    public async Task The_expected_hardware_is_remembered_checked_and_forgotten_through_the_recorder()
    {
        using var client = await SignedInClientAsync();
        _agent.HardwareExpectedFile = Paths.ExpectedHardwareFile;

        var first = await client.GetFromJsonAsync<JsonElement>("/api/v1/hardware", Json);
        first.GetProperty("expected").ValueKind.ShouldBe(JsonValueKind.Null);
        first.GetProperty("status").GetProperty("current").GetArrayLength().ShouldBe(2);

        // Remember a setup with a second monitor the fake recorder does not have: it is missing.
        var body = new
        {
            items = new object[]
            {
                new { key = "monitor:AUS32F4", kind = "monitor", name = "PG32UCDP", detail = "3840 × 2160" },
                new { key = "monitor:AUS2721", kind = "monitor", name = "VG279QM", detail = "1920 × 1080" },
            },
            incidentOnMissing = true,
        };
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/hardware/expected") { Content = JsonContent.Create(body) };
        put.Headers.Add("Origin", "http://localhost");
        var saved = await (await client.SendAsync(put)).Content.ReadFromJsonAsync<JsonElement>(Json);
        saved.GetProperty("expected").GetProperty("incidentOnMissing").GetBoolean().ShouldBeTrue();
        saved.GetProperty("status").GetProperty("missing")[0].GetProperty("item").GetProperty("name").GetString().ShouldBe("VG279QM");
        saved.GetProperty("status").GetProperty("added")[0].GetProperty("key").GetString().ShouldBe("memory");

        var bad = new HttpRequestMessage(HttpMethod.Put, "/api/v1/hardware/expected") { Content = JsonContent.Create(new { items = new[] { new { key = "x", kind = "toaster", name = "Toaster" } } }) };
        bad.Headers.Add("Origin", "http://localhost");
        (await client.SendAsync(bad)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/hardware/expected");
        delete.Headers.Add("Origin", "http://localhost");
        (await client.SendAsync(delete)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(Paths.ExpectedHardwareFile).ShouldBeFalse();
    }
}
