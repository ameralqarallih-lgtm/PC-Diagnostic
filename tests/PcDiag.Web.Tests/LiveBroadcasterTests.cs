using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Ipc;
using PcDiag.Web.Live;

namespace PcDiag.Web.Tests;

public sealed class LiveBroadcasterTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeAgentClient _agent = new() { Connected = true };
    private readonly Lifetime _lifetime = new();
    private readonly LiveBroadcaster _broadcaster;

    public LiveBroadcasterTests() =>
        _broadcaster = new LiveBroadcaster(_agent, _time, _lifetime, NullLogger<LiveBroadcaster>.Instance);

    public void Dispose()
    {
        _broadcaster.Dispose();
        _lifetime.Dispose();
    }

    [Fact]
    public async Task Several_subscribers_share_one_poll_and_polling_stops_when_they_leave()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = _broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await first.MoveNextAsync()).ShouldBeTrue();
        first.Current.Connected.ShouldBeTrue();

        var second = _broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await second.MoveNextAsync()).ShouldBeTrue(); // the latest update, without a new poll
        _agent.Count(AgentCommands.Live).ShouldBe(1);
        _broadcaster.SubscriberCount.ShouldBe(2);

        var nextFirst = first.MoveNextAsync().AsTask();
        var nextSecond = second.MoveNextAsync().AsTask();
        _time.Advance(LiveBroadcaster.Interval);
        (await nextFirst).ShouldBeTrue();
        (await nextSecond).ShouldBeTrue();
        _agent.Count(AgentCommands.Live).ShouldBe(2);

        await first.DisposeAsync();
        await second.DisposeAsync();
        _broadcaster.SubscriberCount.ShouldBe(0);

        _time.Advance(LiveBroadcaster.Interval * 5);
        await Task.Delay(100);
        _agent.Count(AgentCommands.Live).ShouldBe(2);
    }

    [Fact]
    public async Task A_stopped_agent_is_reported_instead_of_ending_the_stream()
    {
        _agent.Connected = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = _broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        (await stream.MoveNextAsync()).ShouldBeTrue();

        stream.Current.Connected.ShouldBeFalse();
        stream.Current.Detail!.ShouldContain("not running");
        stream.Current.Live.ShouldBeNull();
    }

    [Fact]
    public async Task Streams_end_when_the_host_stops()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = _broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await stream.MoveNextAsync()).ShouldBeTrue();

        var next = stream.MoveNextAsync().AsTask();
        _lifetime.Stop();

        (await next).ShouldBeFalse();
        _broadcaster.SubscriberCount.ShouldBe(0);
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();

        public void Stop() => _stopping.Cancel();

        public void Dispose() => _stopping.Dispose();
    }
}
