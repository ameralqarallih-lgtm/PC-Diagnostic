using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Storage.Sqlite.Incidents;
using PcDiag.Storage.Sqlite;
using PcDiag.Web.Agent;
using PcDiag.Web.Security;

namespace PcDiag.Web.Tests;

public sealed class ExportTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public ExportTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Storage:DataDirectory", _dataDirectory);
            builder.ConfigureTestServices(services => services.AddSingleton<IAgentClient>(new FakeAgentClient { Connected = true }));
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
            await ExecAsync("INSERT INTO SystemSnapshots (ts, seq, cpu_pct, gpu_pct) VALUES ($p0, $p1, $p2, $p3);", start + (s * 1000L), s, 25.0 + (s % 10), 50.0);
        }

        await ExecAsync("INSERT INTO WindowsEvents (ts, channel, record_id, provider, event_id, level, category, severity, title, message) VALUES ($p0, 'System', 1, 'Display', 4101, 3, 1, 3, $p1, 'Display driver nvlddmkm stopped responding, and has successfully recovered.');",
            start + 5000, "=HYPERLINK(\"http://x\") <b>driver</b>");
        await ExecAsync("INSERT INTO ProcessIdentity (proc_key, pid, name, path, start_ts) VALUES (7, 4242, 'game.exe', 'C:\\Games\\game.exe', $p0);", start);
        await ExecAsync("INSERT INTO ProcessSnapshots (ts, proc_key, cpu_pct, private_b, state, disk_read_bps, disk_write_bps, net_rx_bps, net_tx_bps) VALUES ($p0, 7, 55.5, 2147483648, 0, 1000000, 0, 2000000, 500000);", start + 290_000);
        await ExecAsync("INSERT INTO SystemInformation (key, value, updated_ts) VALUES ('cpu.name', 'Intel Core Ultra 9 285K', $p0);", start);

        var services = _factory.Services;
        var store = new IncidentStore(Paths, services.GetRequiredService<SqliteConnectionFactory>(), services.GetRequiredService<DatabaseInitializer>(), TimeProvider.System);
        var incident = await store.CreateAsync(
            new IncidentDraft(_now, _now, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), IncidentSource.User, UserIncidentType.ScreenFlicker, "Flicker <script>alert(1)</script>",
                "left monitor", Severity.Medium, false, new IncidentTrigger(_now, IncidentSource.User, UserIncidentType.ScreenFlicker), "1.0.0"),
            CancellationToken.None);
        await store.CaptureWindowAsync(incident, _now.AddMinutes(-10), _now, [], CancellationToken.None);
        await store.FinalizeAsync(incident, IncidentState.Complete, null, CancellationToken.None);
        await store.SaveFindingsAsync(incident.Key,
        [
            new Finding(EvidenceLevel.ConfirmedEvent, "Windows logged \"Display driver stopped responding\" (Event 4101, Display).", []),
            new Finding(EvidenceLevel.Correlation, "GPU and display activity coincided with the reported screen flicker.", []),
            new Finding(EvidenceLevel.NotProven, "The collected telemetry does not establish that the GPU caused the reported screen flicker.", []),
        ], 2, CancellationToken.None);
        return incident.Key.ToString();
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var token = _factory.Services.GetRequiredService<LaunchTokenService>().IssueToken();
        (await client.GetAsync("/auth/exchange?t=" + token)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }

    [Fact]
    public async Task Reports_are_self_contained_html_with_everything_encoded_and_the_summary_last()
    {
        var key = await SeedAsync();
        using var client = await SignedInClientAsync();

        var quick = await client.GetAsync($"/api/v1/incidents/{key}/export?format=html");
        quick.StatusCode.ShouldBe(HttpStatusCode.OK);
        quick.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe($"{key}-quick.html");
        var html = await quick.Content.ReadAsStringAsync();
        html.ShouldContain("Content-Security-Policy");
        html.ShouldNotContain("<script");
        html.ShouldContain("Flicker &lt;script&gt;alert(1)&lt;/script&gt;");
        html.ShouldContain("<svg");
        html.ShouldContain("Quick report");
        html.IndexOf("Diagnostic summary", StringComparison.Ordinal).ShouldBeGreaterThan(html.IndexOf("Timeline", StringComparison.Ordinal));
        html.ShouldContain("NOT PROVEN");
        html.ShouldContain("does not establish that the GPU caused");

        var technical = await (await client.GetAsync($"/api/v1/incidents/{key}/export?format=html&report=technical")).Content.ReadAsStringAsync();
        technical.ShouldContain("Technical report");
        technical.ShouldContain("Processes at the incident");
        technical.ShouldContain("game.exe");
        technical.ShouldContain("Intel Core Ultra 9 285K");
        technical.ShouldContain("&lt;b&gt;driver&lt;/b&gt;");
        technical.ShouldContain("<th>Network</th>", Case.Sensitive, "Enhanced mode measured the network per process");
    }

    [Fact]
    public async Task The_zip_has_the_specified_layout_and_csv_cannot_run_formulas()
    {
        var key = await SeedAsync();
        using var client = await SignedInClientAsync();

        var response = await client.GetAsync($"/api/v1/incidents/{key}/export?format=zip");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).ShouldBe(
            ["incident.json", "summary.html", "metrics.csv", "processes.csv", "services.csv", "events.csv", "hardware.json", "display.json", "drivers.json", "system.json"]);

        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        var incident = JsonNode.Parse(Read("incident.json"))!;
        incident["format"]!.GetValue<string>().ShouldBe("pcdiagnostic-incident/1");
        incident["incident"]!["key"]!.GetValue<string>().ShouldBe(key);
        incident["findings"]!.AsArray().Count.ShouldBe(3);

        var metrics = Read("metrics.csv").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        metrics[0].TrimEnd('\r').ShouldBe("time_utc,series,label,unit,device,min,avg,max");
        metrics.ShouldContain(l => l.Contains(",cpu,CPU,%,,", StringComparison.Ordinal));

        Read("processes.csv").ShouldContain("4242,game.exe,C:\\Games\\game.exe,55.5,,2147483648");
        var events = Read("events.csv");
        events.ShouldContain("\"'=HYPERLINK(\"\"http://x\"\") <b>driver</b>\"", Case.Sensitive, "a formula is turned into text");
        events.ShouldContain("Display driver nvlddmkm stopped responding");
        JsonNode.Parse(Read("system.json"))!["information"]!["cpu.name"]!.GetValue<string>().ShouldBe("Intel Core Ultra 9 285K");
        Read("summary.html").ShouldContain("Technical report");
    }

    [Fact]
    public async Task The_zip_ends_with_the_flight_recorder_trace_when_one_was_saved()
    {
        var key = await SeedAsync();
        var trace = Path.Combine(Paths.GetIncidentDirectory(IncidentKey.Parse(key, null)), "traces", "incident.etl");
        Directory.CreateDirectory(Path.GetDirectoryName(trace)!);
        var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(trace, bytes);
        using var client = await SignedInClientAsync();

        var response = await client.GetAsync($"/api/v1/incidents/{key}/export?format=zip");

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        zip.Entries[^1].FullName.ShouldBe("traces/incident.etl");
        using var copy = new MemoryStream();
        await using (var entry = zip.Entries[^1].Open())
        {
            await entry.CopyToAsync(copy);
        }

        copy.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task Single_files_and_bad_requests()
    {
        var key = await SeedAsync();
        using var client = await SignedInClientAsync();

        var csv = await client.GetAsync($"/api/v1/incidents/{key}/export?format=csv&file=events");
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe($"{key}-events.csv");
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF }, "a BOM, so spreadsheets read UTF-8");
        System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).ShouldStartWith("time_utc,kind,importance,title");

        var json = JsonNode.Parse(await client.GetStringAsync($"/api/v1/incidents/{key}/export?format=json"))!;
        json["timeline"]!.AsArray().Count.ShouldBeGreaterThan(0);

        (await client.GetAsync($"/api/v1/incidents/{key}/export?format=exe")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync($"/api/v1/incidents/{key}/export?format=csv&file=..%2Fsettings")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/v1/incidents/INC-20990101-00001/export")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _factory.CreateClient().GetAsync($"/api/v1/incidents/{key}/export")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pdf_is_printed_with_edge_when_it_is_installed()
    {
        var key = await SeedAsync();
        using var client = await SignedInClientAsync();

        var response = await client.GetAsync($"/api/v1/incidents/{key}/export?format=pdf");
        if (Reports.PdfPrinter.EdgePath() is null)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
            return;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).ShouldBe("%PDF-");
        bytes.Length.ShouldBeGreaterThan(5_000);
    }
}
