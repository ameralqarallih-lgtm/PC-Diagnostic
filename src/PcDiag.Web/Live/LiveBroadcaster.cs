using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PcDiag.Core.Ipc;
using PcDiag.Web.Agent;

namespace PcDiag.Web.Live;

/// <summary>One live update for the dashboard.</summary>
/// <param name="Connected">Whether the Agent answered.</param>
/// <param name="Detail">Why it did not, or why the command failed.</param>
/// <param name="Status">Recorder status.</param>
/// <param name="Live">Live values.</param>
public sealed record LiveMessage(bool Connected, string? Detail, AgentStatus? Status, LiveSnapshot? Live)
{
    /// <summary>Builds a message from an Agent answer.</summary>
    public static LiveMessage From(AgentResponse? response, IAgentClient agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return response switch
        {
            null => new LiveMessage(false, agent.UnavailableReason, null, null),
            { Ok: true } => new LiveMessage(true, null, response.Status, response.Live),
            _ => new LiveMessage(true, response.Error, response.Status, null),
        };
    }
}

/// <summary>
/// Polls the Agent's live values once a second while at least one browser is subscribed and fans them out to
/// every subscriber, so several open tabs cost one poll. Nothing runs when nobody is watching.
/// </summary>
public sealed partial class LiveBroadcaster(IAgentClient agent, TimeProvider time, IHostApplicationLifetime lifetime, ILogger<LiveBroadcaster> logger) : IDisposable
{
    /// <summary>Update interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>Most concurrent subscribers (browser tabs).</summary>
    public const int MaxSubscribers = 16;

    private readonly Lock _gate = new();
    private readonly HashSet<Channel<LiveMessage>> _subscribers = [];
    private CancellationTokenSource? _loop;
    private LiveMessage? _last;

    /// <summary>Current subscribers.</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>Whether another subscriber is accepted.</summary>
    public bool HasCapacity => SubscriberCount < MaxSubscribers;

    /// <summary>Live updates until the caller cancels or the host stops. The latest known update comes first.</summary>
    public async IAsyncEnumerable<LiveMessage> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Slow readers only ever get the newest update.
        var channel = Channel.CreateBounded<LiveMessage>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        LiveMessage? last;
        lock (_gate)
        {
            _subscribers.Add(channel);
            last = _last;
            if (_loop is null)
            {
                _loop = new CancellationTokenSource();
                _ = RunAsync(_loop);
            }
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        try
        {
            if (last is not null)
            {
                yield return last;
            }

            while (true)
            {
                bool more;
                try
                {
                    more = await channel.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    more = false;
                }

                if (!more)
                {
                    break;
                }

                while (channel.Reader.TryRead(out var message))
                {
                    yield return message;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
                if (_subscribers.Count == 0 && _loop is { } loop)
                {
                    _loop = null;
                    _last = null;
                    loop.Cancel();
                }
            }
        }
    }

    /// <summary>Stops polling (the loop disposes its own token source when it ends).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _loop?.Cancel();
            _loop = null;
        }
    }

    private async Task RunAsync(CancellationTokenSource loop)
    {
        var token = loop.Token;
        try
        {
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                var message = LiveMessage.From(await agent.SendAsync(AgentCommands.Live, null, token).ConfigureAwait(false), agent);
                lock (_gate)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    _last = message;
                    foreach (var subscriber in _subscribers)
                    {
                        subscriber.Writer.TryWrite(message);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The last subscriber left.
        }
        catch (Exception ex)
        {
            LogLoopFailed(logger, ex);
            lock (_gate)
            {
                // End the streams; browsers reconnect after a few seconds and start a new loop.
                if (_loop == loop)
                {
                    _loop = null;
                    _last = null;
                }

                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryComplete();
                }
            }
        }
        finally
        {
            loop.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The live feed stopped unexpectedly")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);
}
