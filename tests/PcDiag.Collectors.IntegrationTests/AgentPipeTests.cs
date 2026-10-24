using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Ipc;
using PcDiag.Platform.Windows.Ipc;
using PcDiag.Web.Agent;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>The Agent pipe over a real named pipe (a unique test name, not the Agent's).</summary>
public sealed class AgentPipeTests : IAsyncDisposable
{
    private readonly string _name = "PcDiagnostic.Tests." + Guid.NewGuid().ToString("N");
    private CancellationTokenSource _stop = new();
    private Task? _server;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_server is not null)
        {
            await _server.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _stop.Dispose();
    }

    private async Task StartAsync()
    {
        var server = new NamedPipeServer(_name, (request, _) => Task.FromResult(new AgentResponse(request.Id, true, Status: new AgentStatus("test", false,
            Core.Recording.RecordingMode.Manual, Core.Recording.RecorderState.Idle, null, null, null))), NullLogger.Instance);
        var listening = new TaskCompletionSource();
        server.Listening += (_, _) => listening.TrySetResult();
        _server = Task.Run(() => server.RunAsync(_stop.Token));
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Requests_round_trip_through_the_secured_pipe()
    {
        await StartAsync();
        await using var client = await AgentIpcClient.ConnectAsync(_name, TimeSpan.FromSeconds(5), CancellationToken.None);

        var first = await client.SendAsync(AgentCommands.Status, null, CancellationToken.None);
        var second = await client.SendAsync(AgentCommands.Status, null, CancellationToken.None);

        first.Ok.ShouldBeTrue();
        first.Status!.Version.ShouldBe("test");
        second.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public async Task Clients_that_connect_and_leave_at_once_do_not_stop_the_pipe()
    {
        await StartAsync();
        // Probes that only check the recorder is up (on 16 Oct 2026 one left as its connection was taken, and the pipe
        // stopped listening until the recorder restarted: the dashboard showed it as not running).
        for (var i = 0; i < 20; i++)
        {
            using var probe = new System.IO.Pipes.NamedPipeClientStream(".", _name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await probe.ConnectAsync(5000);
        }

        await using var client = await AgentIpcClient.ConnectAsync(_name, TimeSpan.FromSeconds(5), CancellationToken.None);
        (await client.SendAsync(AgentCommands.Status, null, CancellationToken.None)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task The_web_client_fails_fast_while_the_agent_is_down_and_connects_once_it_is_up()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var client = new AgentPipeClient(_name, time, NullLogger<AgentPipeClient>.Instance);

        (await client.SendAsync(AgentCommands.Status, null, CancellationToken.None)).ShouldBeNull();
        client.UnavailableReason.ShouldContain("not running");

        await StartAsync();
        var started = System.Diagnostics.Stopwatch.StartNew();
        (await client.SendAsync(AgentCommands.Status, null, CancellationToken.None)).ShouldBeNull(); // still within the retry delay
        started.Elapsed.ShouldBeLessThan(AgentPipeClient.ConnectTimeout);

        time.Advance(AgentPipeClient.RetryDelay);
        var response = await client.SendAsync(AgentCommands.Status, null, CancellationToken.None);
        response!.Status!.Version.ShouldBe("test");
        (await client.SendAsync(AgentCommands.Live, null, CancellationToken.None))!.Ok.ShouldBeTrue(); // same connection
    }

    [Fact]
    public async Task The_web_client_reconnects_after_the_agent_restarts()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var client = new AgentPipeClient(_name, time, NullLogger<AgentPipeClient>.Instance);
        await StartAsync();
        (await client.SendAsync(AgentCommands.Status, null, CancellationToken.None)).ShouldNotBeNull();

        await _stop.CancelAsync();
        await _server!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        _stop = new CancellationTokenSource();
        await StartAsync();

        // The old connection is dead; a read is retried once on a fresh connection.
        (await client.SendAsync(AgentCommands.Status, null, CancellationToken.None))!.Ok.ShouldBeTrue();
    }

    [Fact]
    public void The_security_descriptor_allows_only_the_user_and_system_and_denies_network_logons()
    {
        var security = NamedPipeServer.CreateSecurity();
        using var identity = WindowsIdentity.GetCurrent();

        security.GetOwner(typeof(SecurityIdentifier)).ShouldBe(identity.User);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        rules.Select(r => r.IdentityReference).ShouldBe([identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.NetworkSid, null)], ignoreOrder: true);
        rules.Single(r => r.IdentityReference == identity.User).AccessControlType.ShouldBe(AccessControlType.Allow);
        rules.Single(r => r.IdentityReference.Value == "S-1-5-2").AccessControlType.ShouldBe(AccessControlType.Deny);
    }

    [Fact]
    public async Task A_malformed_message_is_answered_and_the_connection_closed_without_affecting_others()
    {
        await StartAsync();
        await using (var raw = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000);
            await raw.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }); // a 2 GB length
            var response = await AgentIpc.ReadAsync(raw, AgentIpcJsonContext.Default.AgentResponse, CancellationToken.None, AgentIpc.MaxResponseBytes);
            response!.Ok.ShouldBeFalse();
            (await raw.ReadAsync(new byte[1])).ShouldBe(0); // closed by the server
        }

        await using var client = await AgentIpcClient.ConnectAsync(_name, TimeSpan.FromSeconds(5), CancellationToken.None);
        (await client.SendAsync(AgentCommands.Status, null, CancellationToken.None)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_server_cannot_take_over_an_existing_pipe_name()
    {
        await StartAsync();
        var intruder = new NamedPipeServer(_name, (r, _) => Task.FromResult(new AgentResponse(r.Id, false, "intruder")), NullLogger.Instance);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => intruder.RunAsync(CancellationToken.None));
    }
}
