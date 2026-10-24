using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PcDiag.Core.Configuration;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Core.Recording;
using PcDiag.Storage.Sqlite;
using PcDiag.Web.Agent;
using PcDiag.Web.Endpoints;
using PcDiag.Web.Live;
using PcDiag.Web.Security;
using PcDiag.Web.Settings;

namespace PcDiag.Web.Tests;

public sealed class ApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static readonly string[] CleanUserTemp = ["user-temp"];

    private static readonly string[] KeepsBackups = ["backups"];

    private static readonly string[] KeepsExports = ["exports"];

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAgentClient _agent = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests()
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
            // Log files may still be flushing; best effort.
        }
    }

    [Fact]
    public async Task Status_includes_the_agent_status_when_it_is_running()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        var status = await client.GetFromJsonAsync<StatusResponse>("/api/v1/status", Json);

        status!.Agent.Connected.ShouldBeTrue();
        status.Agent.Status!.State.ShouldBe(Core.Recording.RecorderState.Recording);
        status.Agent.Detail.ShouldBeNull();
    }

    /// <summary>Processes, services and the tune-up go to the recorder, which checks and carries them out.</summary>
    [Fact]
    public async Task Process_service_and_tune_up_actions_are_forwarded_to_the_recorder()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        var processes = await client.GetFromJsonAsync<List<PcDiag.Core.Control.ControlProcess>>("/api/v1/control/processes", Json);
        processes!.ShouldHaveSingleItem().Name.ShouldBe("game.exe");

        var end = await PostAsync(client, "/api/v1/control/processes/1234/end", new { start = 638_000_000_000_000_000, tree = true, confirmed = true });
        end.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await end.Content.ReadFromJsonAsync<PcDiag.Core.Control.ControlResult>(Json))!.Ok.ShouldBeTrue();
        var sent = _agent.Requests.Last(r => r.Command == AgentCommands.ProcessAction).Args!;
        (sent.Pid, sent.ProcessStart, sent.ControlAction, sent.Confirmed).ShouldBe((1234, 638_000_000_000_000_000L, "end-tree", true));

        (await PostAsync(client, "/api/v1/control/services/Spooler/start-type", new { startType = "Manual" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        _agent.Requests.Last(r => r.Command == AgentCommands.ServiceAction).Args!.ShouldSatisfyAllConditions(a => a.ServiceName.ShouldBe("Spooler"), a => a.StartType.ShouldBe("Manual"));

        (await PostAsync(client, "/api/v1/tuneup/clean", new { items = CleanUserTemp })).StatusCode.ShouldBe(HttpStatusCode.OK);
        _agent.Requests.Last(r => r.Command == AgentCommands.TuneUpApply).Args!.ShouldSatisfyAllConditions(a => a.ControlAction.ShouldBe("clean"), a => a.Items!.Single().ShouldBe("user-temp"));

        (await PostAsync(client, "/api/v1/tuneup/format-disk", new { })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _agent.Count(AgentCommands.TuneUpApply).ShouldBe(1);
    }

    [Fact]
    public async Task Status_explains_when_the_agent_is_not_running()
    {
        using var client = await SignedInClientAsync();

        var status = await client.GetFromJsonAsync<StatusResponse>("/api/v1/status", Json);

        status!.Agent.Connected.ShouldBeFalse();
        status.Agent.Status.ShouldBeNull();
        status.Agent.Detail!.ShouldContain("not running");
    }

    [Theory]
    [InlineData("start", AgentCommands.Start)]
    [InlineData("stop", AgentCommands.Stop)]
    [InlineData("resume", AgentCommands.Resume)]
    public async Task Recorder_commands_are_forwarded_to_the_agent(string path, string command)
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        var response = await PostAsync(client, "/api/v1/recorder/" + path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _agent.Count(command).ShouldBe(1);
        (await response.Content.ReadFromJsonAsync<AgentStatus>(Json))!.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task Pause_takes_minutes_or_null_and_rejects_out_of_range_values()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        (await PostAsync(client, "/api/v1/recorder/pause", new { minutes = 15 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(client, "/api/v1/recorder/pause", new { minutes = (int?)null })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(client, "/api/v1/recorder/pause", new { minutes = 0 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(client, "/api/v1/recorder/pause", new { minutes = 1441 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        _agent.Requests.Where(r => r.Command == AgentCommands.Pause).Select(r => r.Args?.PauseMinutes).ShouldBe([15, null]);
    }

    private static readonly string[] Selected = ["cpu.usage", "sensor:lhm/intelcpu/0/temperature/0"];

    [Fact]
    public async Task A_custom_recording_sends_its_selection_and_is_remembered_and_the_catalog_comes_from_the_agent()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        (await PostAsync(client, "/api/v1/recorder/start")).StatusCode.ShouldBe(HttpStatusCode.OK); // record everything
        (await PostAsync(client, "/api/v1/recorder/start", new { selection = Selected })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(client, "/api/v1/recorder/start", new { selection = Array.Empty<string>() })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var catalog = await client.GetFromJsonAsync<RecordingCatalog>("/api/v1/recorder/catalog", Json);

        var starts = _agent.Requests.Where(r => r.Command == AgentCommands.Start).Select(r => r.Args?.Selection).ToList();
        starts.Count.ShouldBe(2);
        starts[0].ShouldBeNull();
        starts[1].ShouldBe(["cpu.usage", "sensor:lhm/intelcpu/0/temperature/0"]);
        _factory.Services.GetRequiredService<SettingsStore>().Load().Recorder.CustomSelection!.Items.ShouldBe(["cpu.usage", "sensor:lhm/intelcpu/0/temperature/0"]);
        catalog!.Sources.ShouldHaveSingleItem().Items.ShouldHaveSingleItem().Id.ShouldBe("cpu.usage");
    }

    [Fact]
    public async Task Commands_fail_with_503_when_the_agent_is_down_and_409_when_it_refuses()
    {
        using var client = await SignedInClientAsync();

        var down = await PostAsync(client, "/api/v1/recorder/start");
        down.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await down.Content.ReadAsStringAsync()).ShouldContain("not running");

        _agent.Connected = true;
        _agent.Refuse = "A pause must be 1–1440 minutes.";
        var refused = await PostAsync(client, "/api/v1/recorder/start");
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("1440");
    }

    [Fact]
    public async Task Capture_forwards_the_type_and_trimmed_note()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        var response = await PostAsync(client, "/api/v1/incidents/capture", new { type = "ScreenFlicker", note = "  flicker in game  " });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<CaptureResponse>(Json))!.Capture.IncidentKey.ShouldBe("INC-20261006-00001");
        var args = _agent.Requests.Single(r => r.Command == AgentCommands.Capture).Args!;
        args.IncidentType.ShouldBe(UserIncidentType.ScreenFlicker);
        args.Note.ShouldBe("flicker in game");
    }

    [Fact]
    public async Task Capture_rejects_long_notes_and_unknown_types_without_calling_the_agent()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        (await PostAsync(client, "/api/v1/incidents/capture", new { note = new string('x', 2001) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(client, "/api/v1/incidents/capture", new { type = "Explosion" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        _agent.Count(AgentCommands.Capture).ShouldBe(0);
    }

    [Fact]
    public async Task Capture_needs_the_dashboard_origin()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();
        using var content = JsonContent.Create(new { });

        (await client.PostAsync("/api/v1/incidents/capture", content)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _agent.Count(AgentCommands.Capture).ShouldBe(0);
    }

    [Fact]
    public async Task Live_returns_one_snapshot()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        var message = await client.GetFromJsonAsync<LiveMessage>("/api/v1/live", Json);

        message!.Connected.ShouldBeTrue();
        message.Live!.Cpu!.TotalPercent.Value.ShouldBe(42.5);
        message.Live.Cpu.PackageTemperatureC.Unavailable.ShouldBe("DriverMissing");
    }

    [Fact]
    public async Task The_trend_fills_the_charts_and_is_clamped_to_ten_minutes()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        var trend = await client.GetFromJsonAsync<LiveTrend>("/api/v1/live/trend?minutes=60", Json);

        trend!.Times.Count.ShouldBe(600);
        trend.Cpu[0].ShouldBe(40);
        trend.CpuTemperature[0].ShouldBeNull();
        _agent.Requests.Single(r => r.Command == AgentCommands.Trend).Args!.TrendMinutes.ShouldBe(10);
    }

    [Fact]
    public async Task The_live_stream_sends_server_sent_events_and_stops_polling_when_closed()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();
        var broadcaster = _factory.Services.GetRequiredService<LiveBroadcaster>();

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            using var response = await client.GetAsync("/api/v1/live/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            var eventLine = await reader.ReadLineAsync(cts.Token);
            var dataLine = await reader.ReadLineAsync(cts.Token);

            eventLine.ShouldBe("event: live");
            dataLine.ShouldStartWith("data: ");
            var message = JsonSerializer.Deserialize<LiveMessage>(dataLine!["data: ".Length..], Json)!;
            message.Live!.Processes!.ByCpu.ShouldHaveSingleItem().Name.ShouldBe("game.exe");
            broadcaster.SubscriberCount.ShouldBe(1);
        }

        await WaitUntilAsync(() => broadcaster.SubscriberCount == 0);
        var polls = _agent.Count(AgentCommands.Live);
        await Task.Delay(LiveBroadcaster.Interval * 2.5);
        _agent.Count(AgentCommands.Live).ShouldBe(polls); // nobody is watching: no polling
    }

    [Fact]
    public async Task The_incident_list_is_empty_on_a_new_install()
    {
        using var client = await SignedInClientAsync();

        var page = await client.GetFromJsonAsync<IncidentPage>("/api/v1/incidents?limit=5", Json);

        page!.Total.ShouldBe(0);
        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tray_launch_ticket_opens_a_session_once()
    {
        using var client = CreateClient();
        var token = LaunchTickets.Issue(_factory.Services.GetRequiredService<DataPaths>(), DateTimeOffset.UtcNow);

        (await client.GetAsync("/auth/exchange?t=" + token)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await client.GetAsync("/api/v1/status")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var other = CreateClient();
        (await other.GetAsync("/auth/exchange?t=" + token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_on_screen_readings_layout_is_validated_saved_for_the_recorder_and_applied()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        var empty = await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay");
        empty.GetProperty("config").GetProperty("enabled").GetBoolean().ShouldBeFalse();
        empty.GetProperty("state").GetProperty("displays")[0].GetProperty("name").GetString().ShouldBe("PG32UCDP");

        var layout = new
        {
            version = 1,
            enabled = true,
            hotkey = "Ctrl+Alt+Shift+O",
            intervalMs = 500,
            hideFromCapture = true,
            screens = new[]
            {
                new
                {
                    key = "AUS32F4/1",
                    name = "PG32UCDP",
                    panels = new[] { new { id = "panel-1", title = "System", x = 1.0, y = 0.0, layout = "rows", readings = new[] { new { source = "cpu.usage", warning = 80.0 } }, style = new PcDiag.Core.Overlay.OverlayStyle() } },
                },
            },
        };
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/overlay") { Content = JsonContent.Create(layout) };
        put.Headers.Add("Origin", "http://localhost");
        (await client.SendAsync(put)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _agent.Count(AgentCommands.OverlayReload).ShouldBe(1);

        // The recorder reads the same file.
        var stored = PcDiag.Core.Overlay.OverlayStyles.Read(Path.Combine(_dataDirectory, "overlay.json"), out var problem);
        problem.ShouldBeNull();
        stored.Enabled.ShouldBeTrue();
        stored.IntervalMs.ShouldBe(500);
        stored.PanelsOf("AUS32F4/1").ShouldHaveSingleItem().Readings.ShouldHaveSingleItem().Warning.ShouldBe(80);

        var bad = new HttpRequestMessage(HttpMethod.Put, "/api/v1/overlay") { Content = JsonContent.Create(layout with { intervalMs = 7 }) };
        bad.Headers.Add("Origin", "http://localhost");
        (await client.SendAsync(bad)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // A whole answer of GET sent back (the layout wrapped in "config") is refused, never saved as an empty layout.
        var wrapped = new HttpRequestMessage(HttpMethod.Put, "/api/v1/overlay") { Content = JsonContent.Create(new { config = layout, revision = "1" }) };
        wrapped.Headers.Add("Origin", "http://localhost");
        var refused = await client.SendAsync(wrapped);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().ShouldBe("\"config\" is not part of a layout.");
        PcDiag.Core.Overlay.OverlayStyles.Read(Path.Combine(_dataDirectory, "overlay.json"), out _).PanelsOf("AUS32F4/1").ShouldHaveSingleItem();

        (await PostAsync(client, "/api/v1/overlay/identify")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _agent.Count(AgentCommands.OverlayIdentify).ShouldBe(1);
        var state = await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/state");
        state.GetProperty("sources")[0].GetProperty("id").GetString().ShouldBe("cpu.usage");

        // The saved layout's revision comes with the values; it changes when the layout does (also on the screens).
        var revision = state.GetProperty("revision").GetString();
        revision.ShouldNotBeNullOrEmpty();
        PcDiag.Core.Overlay.OverlayStyles.Write(Path.Combine(_dataDirectory, "overlay.json"), stored with { IntervalMs = 2000 });
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/state")).GetProperty("revision").GetString().ShouldNotBe(revision);

        // While the layout cannot be read (the recorder replacing it), the editor is told to try again, never handed an
        // empty layout it could save over the real one.
        using (new FileStream(Path.Combine(_dataDirectory, "overlay.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (await client.GetAsync("/api/v1/overlay")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }

        (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay")).GetProperty("config").GetProperty("intervalMs").GetInt32().ShouldBe(2000);

        // Arranging on the screens is started and ended through the recorder.
        var arranging = await (await PostAsync(client, "/api/v1/overlay/arrange", new { on = true })).Content.ReadFromJsonAsync<JsonElement>();
        arranging.GetProperty("arranging").GetBoolean().ShouldBeTrue();
        _agent.Count(AgentCommands.OverlayArrange).ShouldBe(1);

        // The USB screens' driver packages: listed, and approved by their content on this PC.
        var drivers = await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/screen-drivers");
        drivers.GetArrayLength().ShouldBe(2);
        drivers[0].GetProperty("builtIn").GetBoolean().ShouldBeTrue();
        var approve = new HttpRequestMessage(HttpMethod.Post, "/api/v1/overlay/screen-drivers/approve") { Content = JsonContent.Create(new { id = "my-screen", sha256 = new string('a', 64), approve = true }) };
        approve.Headers.Add("Origin", "http://localhost");
        var approved = await (await client.SendAsync(approve)).Content.ReadFromJsonAsync<JsonElement>();
        approved[1].GetProperty("approved").GetBoolean().ShouldBeTrue();
        _agent.Count(AgentCommands.OverlayScreenDriverApprove).ShouldBe(1);
    }

    [Fact]
    public async Task Background_pictures_are_stored_by_content_served_and_those_made_for_panels_removed_when_no_panel_uses_them()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

        async Task<HttpResponseMessage> Upload(byte[] bytes, string type, string query = "")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/overlay/images" + query) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            request.Headers.Add("Origin", "http://localhost");
            return await client.SendAsync(request);
        }

        var uploaded = await (await Upload(png, "image/png")).Content.ReadFromJsonAsync<JsonElement>();
        var id = uploaded.GetProperty("id").GetString()!;
        id.ShouldMatch(PcDiag.Core.Overlay.OverlayStyles.ImagePattern);
        uploaded.GetProperty("width").GetInt32().ShouldBe(1);

        // The same picture again is the same id.
        (await (await Upload(png, "image/png")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString().ShouldBe(id);

        var served = await client.GetAsync($"/api/v1/overlay/images/{id}");
        served.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await served.Content.ReadAsByteArrayAsync()).ShouldBe(png);
        (await client.GetAsync("/api/v1/overlay/images/../overlay.json")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A JPEG's size is read from its frame header; anything else is refused.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x20, 0x00, 0x40, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xD9];
        var photo = await (await Upload(jpeg, "image/jpeg", "?generated=true")).Content.ReadFromJsonAsync<JsonElement>();
        photo.GetProperty("id").GetString()!.ShouldEndWith(".jpg");
        photo.GetProperty("width").GetInt32().ShouldBe(64);
        photo.GetProperty("height").GetInt32().ShouldBe(32);
        (await Upload("not a picture"u8.ToArray(), "image/png")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Saving a layout that uses the PNG removes the JPEG made for a panel once it is an hour old.
        var folder = Path.Combine(_dataDirectory, "overlay-images");
        File.SetLastWriteTimeUtc(Path.Combine(folder, photo.GetProperty("id").GetString()!), DateTime.UtcNow.AddHours(-2));
        var style = new PcDiag.Core.Overlay.OverlayStyle(Image: id, Pattern: "hexagons", PatternOpacity: 20);
        var layout = new PcDiag.Core.Overlay.OverlayConfig(Screens: [new("AUS32F4/1", "PG32UCDP", [new("p", "Pictured", 0, 0, "rows", [new("cpu.usage")], style)])]);
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/overlay") { Content = JsonContent.Create(layout) };
        put.Headers.Add("Origin", "http://localhost");
        (await client.SendAsync(put)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.GetFiles(folder).Select(Path.GetFileName).ShouldBe([id]);
    }

    [Fact]
    public async Task PC_Diagnostic_Storage_is_shown_set_up_on_request_and_any_location_can_be_made_the_default()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        // Not set up: its version, its default folder, and whether its program is here (not in tests: downloaded once).
        var local = await client.GetFromJsonAsync<JsonElement>("/api/v1/storage/local");
        local.GetProperty("name").GetString().ShouldBe("PC Diagnostic Storage");
        local.GetProperty("version").GetString().ShouldBe("4.48");
        local.GetProperty("locationId").ValueKind.ShouldBe(JsonValueKind.Null);
        local.GetProperty("running").GetBoolean().ShouldBeFalse();
        local.GetProperty("defaultFolder").GetString().ShouldBe(Path.Combine(_dataDirectory, "object-store"));
        local.GetProperty("setup").GetProperty("running").GetBoolean().ShouldBeFalse();

        // A folder it cannot use is refused at once; starting and stopping it before it is set up too.
        (await PostAsync(client, "/api/v1/storage/local", new { folder = "relative" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(client, "/api/v1/storage/local/stop")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Any location can be where files are kept by default: it keeps every kind, This PC what the recorder reads.
        var folder = Path.Combine(_dataDirectory, "drive");
        var added = await (await PostAsync(client, "/api/v1/storage/locations", new { kind = "folder", name = "Drive", keeps = KeepsBackups, path = folder })).Content.ReadFromJsonAsync<JsonElement>();
        var id = added.GetProperty("locations")[1].GetProperty("location").GetProperty("id").GetString()!;
        var made = await PostAsync(client, $"/api/v1/storage/locations/{id}/default", new { move = true });
        made.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("locations");
        after[0].GetProperty("location").GetProperty("default").GetBoolean().ShouldBeFalse();
        after[0].GetProperty("location").GetProperty("keeps").EnumerateArray().Select(k => k.GetString()).ShouldBe(["pictures", "characters", "drivers"]);
        after[1].GetProperty("location").GetProperty("default").GetBoolean().ShouldBeTrue();
        after[1].GetProperty("location").GetProperty("keeps").GetArrayLength().ShouldBe(PcDiag.Storage.Files.FileKinds.All.Count);
        (await PostAsync(client, "/api/v1/storage/locations/nope/default", new { move = true })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Removed: This PC is the default again, keeping everything.
        (await SendAsync(client, HttpMethod.Delete, $"/api/v1/storage/locations/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var back = await client.GetFromJsonAsync<JsonElement>("/api/v1/storage/locations");
        back.GetProperty("locations").EnumerateArray().ShouldHaveSingleItem().GetProperty("location").GetProperty("default").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Where_files_are_kept_is_set_on_this_pc_and_the_layout_has_backups_that_can_be_brought_back()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        var start = await client.GetFromJsonAsync<JsonElement>("/api/v1/storage/locations");
        start.GetProperty("locations").GetArrayLength().ShouldBe(1);
        start.GetProperty("locations")[0].GetProperty("location").GetProperty("id").GetString().ShouldBe("pc");
        start.GetProperty("onThisPc").EnumerateArray().Select(k => k.GetString()).ShouldBe(["pictures", "characters", "drivers"]);

        // A folder is tested, then added keeping the layout's backups.
        var folder = Path.Combine(_dataDirectory, "backup-drive");
        var test = await (await PostAsync(client, "/api/v1/storage/locations/test", new { kind = "folder", name = "Drive", keeps = KeepsBackups, path = "relative" })).Content.ReadFromJsonAsync<JsonElement>();
        test.GetProperty("problem").GetString()!.ShouldContain("full path");
        var added = await PostAsync(client, "/api/v1/storage/locations", new { kind = "folder", name = "Drive", keeps = KeepsBackups, path = folder });
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("locations")[1].GetProperty("location").GetProperty("id").GetString()!;

        // Saving keeps the layout as it was (none the first time), in every location that keeps backups.
        var layout = new
        {
            version = 1,
            enabled = true,
            hotkey = "Ctrl+Alt+Shift+O",
            intervalMs = 500,
            hideFromCapture = true,
            screens = new[] { new { key = "AUS32F4/1", name = "PG32UCDP", panels = new[] { new { id = "panel-1", title = "System", x = 1.0, y = 0.0, layout = "rows", readings = new[] { new { source = "cpu.usage" } }, style = new PcDiag.Core.Overlay.OverlayStyle() } } } },
        };
        (await SendAsync(client, HttpMethod.Put, "/api/v1/overlay", layout)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/backups")).GetArrayLength().ShouldBe(0);
        (await SendAsync(client, HttpMethod.Put, "/api/v1/overlay", layout with { intervalMs = 2000 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var backups = await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/backups");
        var name = backups.EnumerateArray().ShouldHaveSingleItem().GetProperty("name").GetString()!;
        name.ShouldStartWith(OverlayEndpoints.BackupPrefix);
        File.Exists(Path.Combine(folder, "PcDiagnostic", "backups", name)).ShouldBeTrue();

        // Bringing it back: the recorder reloads it, and the layout replaced is kept too (so it can be undone).
        var restored = await PostAsync(client, $"/api/v1/overlay/backups/{name}/restore");
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await restored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("config").GetProperty("intervalMs").GetInt32().ShouldBe(500);
        PcDiag.Core.Overlay.OverlayStyles.Read(Path.Combine(_dataDirectory, "overlay.json"), out _).IntervalMs.ShouldBe(500);
        _agent.Count(AgentCommands.OverlayReload).ShouldBe(3);
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/backups")).GetArrayLength().ShouldBe(2);
        (await PostAsync(client, "/api/v1/overlay/backups/settings.json/restore")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(client, "/api/v1/overlay/backups/overlay-19990101-000000-000.json/restore")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // This PC stays, and keeps every kind as the default (asking it to keep less changes nothing).
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/storage/locations/pc")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var kept = await SendAsync(client, HttpMethod.Patch, "/api/v1/storage/locations/pc", new { keeps = KeepsExports });
        kept.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await kept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("locations")[0].GetProperty("location").GetProperty("keeps").GetArrayLength().ShouldBe(PcDiag.Storage.Files.FileKinds.All.Count);
        var removed = await SendAsync(client, HttpMethod.Delete, $"/api/v1/storage/locations/{id}");
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("locations").GetArrayLength().ShouldBe(1);
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/storage/files/backups")).GetArrayLength().ShouldBe(2);
        (await client.GetAsync("/api/v1/storage/files/nothing")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_picture_library_keeps_uploads_and_animations_and_clears_pictures_made_for_panels_once_unused()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        // A minimal JPEG of 40 x 20 (its frame header is what is checked) and an animation of three of them.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x14, 0x00, 0x28, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xD9];
        var animation = PcDiag.Core.Overlay.OverlayAnimation.Write(12, 40, 20, [jpeg, jpeg, [.. jpeg, 0x00]]);

        async Task<JsonElement> Upload(byte[] bytes, string query)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/overlay/images" + query) { Content = new ByteArrayContent(bytes) };
            request.Headers.Add("Origin", "http://localhost");
            var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var still = await Upload(jpeg, "?name=Lake");
        still.GetProperty("id").GetString()!.ShouldEndWith(".jpg");
        var moving = await Upload(animation, "?name=Silk&generated=true");
        moving.GetProperty("id").GetString()!.ShouldEndWith(".anim");
        moving.GetProperty("frames").GetInt32().ShouldBe(3);

        // A cut animation: refused.
        var refused = new HttpRequestMessage(HttpMethod.Post, "/api/v1/overlay/images") { Content = new ByteArrayContent(animation[..^3]) };
        refused.Headers.Add("Origin", "http://localhost");
        (await client.SendAsync(refused)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The library: both, by name and kind, here, not used.
        var library = await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/images");
        var items = library.EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!);
        items[still.GetProperty("id").GetString()!].GetProperty("name").GetString().ShouldBe("Lake");
        items[still.GetProperty("id").GetString()!].GetProperty("kind").GetString().ShouldBe("upload");
        items[moving.GetProperty("id").GetString()!].GetProperty("kind").GetString().ShouldBe("generated");
        items.Values.ShouldAllBe(p => p.GetProperty("here").GetBoolean() && !p.GetProperty("used").GetBoolean());

        // A panel shows the upload: it cannot be deleted. Once old, the unused generated one goes on the next save, the upload stays.
        var folder = Path.Combine(_dataDirectory, "overlay-images");
        foreach (var file in Directory.GetFiles(folder))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));
        }

        var layout = new
        {
            version = 1,
            enabled = true,
            hotkey = "Ctrl+Alt+Shift+O",
            intervalMs = 1000,
            hideFromCapture = true,
            screens = new[] { new { key = "AUS32F4/1", name = "PG32UCDP", panels = new[] { new { id = "panel-1", title = "System", x = 1.0, y = 0.0, layout = "rows", readings = new[] { new { source = "cpu.usage" } }, style = new PcDiag.Core.Overlay.OverlayStyle(Image: still.GetProperty("id").GetString()) } } } },
        };
        (await SendAsync(client, HttpMethod.Put, "/api/v1/overlay", layout)).StatusCode.ShouldBe(HttpStatusCode.OK);
        File.Exists(Path.Combine(folder, still.GetProperty("id").GetString()!)).ShouldBeTrue();
        File.Exists(Path.Combine(folder, moving.GetProperty("id").GetString()!)).ShouldBeFalse();
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/overlay/images/" + still.GetProperty("id").GetString())).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // An upload no panel uses is deleted when asked, and a picture only in storage is brought back here.
        var spare = await Upload([.. jpeg, 0x01], "?name=Spare");
        var spareId = spare.GetProperty("id").GetString()!;
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/overlay/images/" + spareId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(folder, spareId)).ShouldBeFalse();
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/images")).EnumerateArray().Select(p => p.GetProperty("id").GetString()).ShouldNotContain(spareId);
        (await SendAsync(client, HttpMethod.Post, "/api/v1/overlay/images/" + spareId + "/fetch")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Characters_are_the_shipped_ones_and_those_imported_into_the_library()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();

        // The shipped ones: listed first, served whole.
        var list = (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/characters")).EnumerateArray().ToList();
        list.Take(2).Select(c => c.GetProperty("id").GetString()).ShouldBe(["mochi", "biscuit"]);
        list[0].GetProperty("shipped").GetBoolean().ShouldBeTrue();
        list[0].GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ShouldContain("play");
        var mochi = await client.GetAsync("/api/v1/overlay/characters/mochi");
        mochi.StatusCode.ShouldBe(HttpStatusCode.OK);
        PcDiag.Core.Overlay.CharacterPack.Read(await mochi.Content.ReadAsByteArrayAsync())!.Name.ShouldBe("Mochi");
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/overlay/characters/mochi")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The reactions' effects, for the editor to draw alike.
        PcDiag.Core.Overlay.CharacterPack.ReadEffects(await client.GetByteArrayAsync("/api/v1/overlay/effects"))!.Actions.ShouldContain("zzz");
        PcDiag.Core.Overlay.CharacterPack.ReadProps(await client.GetByteArrayAsync("/api/v1/overlay/props"))!.Actions.ShouldBe(PcDiag.Core.Overlay.OverlayReactions.Props.Keys, ignoreOrder: true);

        // One imported (the editor's file of frames): into the library, listed as a character, not as a picture.
        static byte[] Png(int w, int h)
        {
            var b = new byte[40];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 }.CopyTo(b, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
            return b;
        }

        var pack = PcDiag.Core.Overlay.CharacterPack.Write("Hero", 30, 40, 15, 38, 30, 1, [("idle", 12, [Png(30, 40), Png(30, 40)]), ("walk", 24, [Png(30, 40)])]);
        var upload = new HttpRequestMessage(HttpMethod.Post, "/api/v1/overlay/images") { Content = new ByteArrayContent(pack) };
        upload.Headers.Add("Origin", "http://localhost");
        var added = await client.SendAsync(upload);
        added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        id.ShouldEndWith(".char");
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/images")).EnumerateArray().Select(p => p.GetProperty("id").GetString()).ShouldNotContain(id);
        var hero = (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/characters")).EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        hero.GetProperty("name").GetString().ShouldBe("Hero");
        hero.GetProperty("shipped").GetBoolean().ShouldBeFalse();
        hero.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ShouldBe(["idle", "walk"]);
        (await client.GetByteArrayAsync($"/api/v1/overlay/characters/{id}")).ShouldBe(pack);
        (await client.GetAsync("/api/v1/overlay/characters/0123456789abcdef.char")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/overlay/characters/..%2Foverlay.json")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A cut one is refused.
        var cut = new HttpRequestMessage(HttpMethod.Post, "/api/v1/overlay/images") { Content = new ByteArrayContent(pack[..^5]) };
        cut.Headers.Add("Origin", "http://localhost");
        (await client.SendAsync(cut)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // On a panel it stays; off every panel it can be removed.
        var layout = new
        {
            version = 1,
            enabled = true,
            hotkey = "Ctrl+Alt+Shift+O",
            intervalMs = 1000,
            hideFromCapture = true,
            screens = new[] { new { key = "AUS32F4/1", name = "PG32UCDP", panels = new[] { new { id = "panel-1", title = "System", x = 1.0, y = 0.0, layout = "rows", readings = new[] { new { source = "cpu.usage" } }, style = new PcDiag.Core.Overlay.OverlayStyle(), characters = new[] { new PcDiag.Core.Overlay.OverlayCharacter("c1", id), new PcDiag.Core.Overlay.OverlayCharacter("c2", "biscuit") } } } } },
        };
        (await SendAsync(client, HttpMethod.Put, "/api/v1/overlay", layout)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var listed = (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/characters")).EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("used").GetBoolean());
        (listed[id], listed["biscuit"], listed["mochi"]).ShouldBe((true, true, false));
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/overlay/characters/" + id)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(client, HttpMethod.Put, "/api/v1/overlay", layout with { screens = new[] { layout.screens[0] with { panels = new[] { layout.screens[0].panels[0] with { characters = new[] { new PcDiag.Core.Overlay.OverlayCharacter("c2", "biscuit") } } } } } })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/overlay/characters/" + id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(_dataDirectory, "overlay-images", id)).ShouldBeFalse();
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/characters")).EnumerateArray().Select(c => c.GetProperty("id").GetString()).ShouldNotContain(id);
    }

    [Fact]
    public async Task The_character_generator_is_set_up_with_its_key_kept_hidden()
    {
        _agent.Connected = true;
        using var client = await SignedInClientAsync();
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/generator")).GetProperty("kind").GetString().ShouldBe("off");
        (await PostAsync(client, "/api/v1/overlay/generations", new { type = "stills", prompt = "silver hair" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "no generator yet");
        (await SendAsync(client, HttpMethod.Put, "/api/v1/overlay/generator", new { kind = "external", url = "ftp://x" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var saved = await SendAsync(client, HttpMethod.Put, "/api/v1/overlay/generator", new { kind = "external", url = "http://127.0.0.1:9", key = "secret" });
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await saved.Content.ReadAsStringAsync();
        text.ShouldNotContain("secret");
        var settings = JsonDocument.Parse(text).RootElement;
        (settings.GetProperty("url").GetString(), settings.GetProperty("hasKey").GetBoolean()).ShouldBe(("http://127.0.0.1:9", true));
        // Nobody answers there: the test says so, and nothing starts.
        (await (await PostAsync(client, "/api/v1/overlay/generator/test")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean().ShouldBeFalse();
        (await PostAsync(client, "/api/v1/overlay/generations", new { type = "stills", prompt = "silver hair" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/generations")).GetArrayLength().ShouldBe(0);
        (await SendAsync(client, HttpMethod.Put, "/api/v1/overlay/generator", new { kind = "internal" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/overlay/generator")).GetProperty("url").GetString().ShouldBe("http://127.0.0.1:7865");
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Origin", "http://localhost");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Add("Origin", "http://localhost");
        return client.SendAsync(request);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        condition().ShouldBeTrue();
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = CreateClient();
        var token = _factory.Services.GetRequiredService<LaunchTokenService>().IssueToken();
        (await client.GetAsync("/auth/exchange?t=" + token)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
