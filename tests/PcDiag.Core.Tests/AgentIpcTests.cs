using System.Buffers.Binary;
using System.Text;
using PcDiag.Core.Configuration;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;

namespace PcDiag.Core.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Shift+I", HotkeyGesture.Control | HotkeyGesture.Alt | HotkeyGesture.Shift, 0x49u, "Ctrl+Alt+Shift+I")]
    [InlineData("shift + ctrl + f12", HotkeyGesture.Control | HotkeyGesture.Shift, 0x7Bu, "Ctrl+Shift+F12")]
    [InlineData("Win+Alt+9", HotkeyGesture.Alt | HotkeyGesture.Win, 0x39u, "Alt+Win+9")]
    [InlineData("Ctrl+Pause", HotkeyGesture.Control, 0x13u, "Ctrl+Pause")]
    [InlineData("Ctrl+Alt+F24", HotkeyGesture.Control | HotkeyGesture.Alt, 0x87u, "Ctrl+Alt+F24")]
    public void Valid_hotkeys_map_to_register_hotkey_values(string text, uint modifiers, uint vk, string normalized)
    {
        var gesture = HotkeyGesture.TryParse(text, out var error).ShouldNotBeNull(error);

        gesture.Modifiers.ShouldBe(modifiers);
        gesture.VirtualKey.ShouldBe(vk);
        gesture.Text.ShouldBe(normalized);
    }

    [Theory]
    [InlineData("I")]                 // no modifier
    [InlineData("Shift+I")]           // would steal typing
    [InlineData("Ctrl+Ctrl+I")]       // repeated
    [InlineData("Ctrl+Hyper+I")]      // unknown modifier
    [InlineData("Ctrl+F25")]          // no such key
    [InlineData("Ctrl+Enter")]        // not supported
    [InlineData("")]
    public void Invalid_hotkeys_are_rejected_with_a_reason(string text)
    {
        HotkeyGesture.TryParse(text, out var error).ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_options_validator_reports_a_bad_hotkey_and_accepts_an_empty_one()
    {
        new AgentOptionsValidator().Validate(null, new AgentOptions { CaptureHotkey = "Shift+I" }).Failed.ShouldBeTrue();
        new AgentOptionsValidator().Validate(null, new AgentOptions { CaptureHotkey = "" }).Succeeded.ShouldBeTrue();
        new AgentOptionsValidator().Validate(null, new AgentOptions()).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"C:\Program Files\PC Diagnostic\PcDiag.Web.exe", true)]
    [InlineData("", true)]
    [InlineData(@"PcDiag.Web.exe", false)]
    [InlineData(@"\\server\share\PcDiag.Web.exe", false)]
    [InlineData(@"C:\Tools\web.cmd", false)]
    public void The_web_host_path_must_be_a_full_local_exe_path(string path, bool valid) =>
        new AgentOptionsValidator().Validate(null, new AgentOptions { WebHostPath = path }).Succeeded.ShouldBe(valid);
}

public class AgentIpcTests
{
    [Fact]
    public async Task Messages_round_trip_with_string_enums_and_camel_case()
    {
        using var stream = new MemoryStream();
        var request = new AgentRequest(AgentIpc.ProtocolVersion, "7", AgentCommands.Capture, new AgentRequestArgs(IncidentType: UserIncidentType.ScreenFlicker, Note: "flicker in game"));

        await AgentIpc.WriteAsync(stream, request, AgentIpcJsonContext.Default.AgentRequest, CancellationToken.None);
        var json = Encoding.UTF8.GetString(stream.ToArray(), 4, (int)stream.Length - 4);
        stream.Position = 0;
        var read = await AgentIpc.ReadAsync(stream, AgentIpcJsonContext.Default.AgentRequest, CancellationToken.None);

        read.ShouldBe(request);
        json.ShouldContain("\"incidentType\":\"ScreenFlicker\"");
        json.ShouldContain("\"command\":\"capture\"");
        json.ShouldNotContain("pauseMinutes"); // nulls are omitted
    }

    [Fact]
    public async Task A_clean_end_of_stream_reads_as_null()
    {
        (await AgentIpc.ReadAsync(new MemoryStream(), AgentIpcJsonContext.Default.AgentRequest, CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(AgentIpc.MaxMessageBytes + 1)]
    public async Task Out_of_range_lengths_are_rejected_before_reading_the_body(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);

        await Should.ThrowAsync<InvalidDataException>(() => AgentIpc.ReadAsync(new MemoryStream(header), AgentIpcJsonContext.Default.AgentRequest, CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_and_malformed_messages_are_rejected()
    {
        var truncated = new byte[] { 10, 0, 0, 0, (byte)'{' };
        await Should.ThrowAsync<InvalidDataException>(() => AgentIpc.ReadAsync(new MemoryStream(truncated), AgentIpcJsonContext.Default.AgentRequest, CancellationToken.None));

        var body = Encoding.UTF8.GetBytes("{\"version\":1,\"id\":\"1\",\"command\":\"capture\",\"args\":{\"incidentType\":\"$type:System.IO.File\"}}");
        var malformed = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(malformed, body.Length);
        body.CopyTo(malformed, 4);
        await Should.ThrowAsync<InvalidDataException>(() => AgentIpc.ReadAsync(new MemoryStream(malformed), AgentIpcJsonContext.Default.AgentRequest, CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_messages_are_not_sent()
    {
        var huge = new AgentRequest(1, "1", AgentCommands.Capture, new AgentRequestArgs(Note: new string('x', AgentIpc.MaxMessageBytes)));

        await Should.ThrowAsync<InvalidDataException>(() => AgentIpc.WriteAsync(new MemoryStream(), huge, AgentIpcJsonContext.Default.AgentRequest, CancellationToken.None));
    }

    [Fact]
    public void Pipe_names_are_per_user_and_only_from_sids()
    {
        AgentIpc.PipeName("S-1-5-21-1000-1001").ShouldBe("PcDiagnostic.Agent.S-1-5-21-1000-1001");
        Should.Throw<ArgumentException>(() => AgentIpc.PipeName(@"..\other"));
    }
}
