using System.Diagnostics.Eventing.Reader;

namespace PcDiag.Platform.Windows.EventLog;

/// <summary>
/// One event record, valid only inside the callback that receives it (the record is disposed afterwards).
/// </summary>
public readonly struct EventRecordView
{
    private readonly EventRecord _record;

    internal EventRecordView(EventRecord record) => _record = record;

    /// <summary>The event as XML (System section, EventData/UserData).</summary>
    public string ToXml() => _record.ToXml();

    /// <summary>
    /// The formatted message from the provider's message resources, or null when the provider has none
    /// (some drivers, e.g. nvlddmkm 153) or its metadata cannot be loaded.
    /// </summary>
    public string? FormatMessage()
    {
        try
        {
            return _record.FormatDescription();
        }
        catch (EventLogException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Why a channel cannot be read.</summary>
public enum ChannelProblem
{
    /// <summary>The channel does not exist on this system.</summary>
    NotFound,

    /// <summary>Reading the channel needs rights the process does not have.</summary>
    AccessDenied,

    /// <summary>The query was rejected or the Event Log service reported another error.</summary>
    Failed,
}

/// <summary>An event log channel could not be read or subscribed.</summary>
public sealed class EventChannelException(string channel, ChannelProblem problem, Exception inner)
    : Exception($"Event log channel '{channel}': {problem} ({inner.Message})", inner)
{
    /// <summary>Channel name.</summary>
    public string Channel { get; } = channel;

    /// <summary>What went wrong.</summary>
    public ChannelProblem Problem { get; } = problem;
}

/// <summary>Reads existing events with a structured XML query (EvtQuery through <see cref="EventLogReader"/>).</summary>
public static class EventLogQueries
{
    /// <summary>
    /// Visits every event the query selects, oldest first. Returns the number visited.
    /// </summary>
    /// <exception cref="EventChannelException">The channel is missing, access is denied or the query is invalid.</exception>
    public static int Read(string channel, string structuredQuery, Action<EventRecordView> visit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(visit);
        var count = 0;
        try
        {
            // For a structured query, Windows takes the channels from the query's Select paths.
            var query = new EventLogQuery(channel, PathType.LogName, structuredQuery);
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    visit(new EventRecordView(record));
                    count++;
                }
            }
        }
        catch (Exception ex) when (Classify(ex) is { } problem)
        {
            throw new EventChannelException(channel, problem, ex);
        }

        return count;
    }

    internal static ChannelProblem? Classify(Exception ex) => ex switch
    {
        EventLogNotFoundException => ChannelProblem.NotFound,
        UnauthorizedAccessException => ChannelProblem.AccessDenied,
        EventLogException => ChannelProblem.Failed,
        _ => null,
    };
}

/// <summary>
/// A live subscription to new events (EvtSubscribe through <see cref="EventLogWatcher"/>). Only events written after
/// <see cref="Start"/> are delivered; read older ones with <see cref="EventLogQueries.Read"/>. Callbacks arrive on a
/// thread-pool thread, one at a time per subscription.
/// </summary>
public sealed class EventLogSubscription : IDisposable
{
    private readonly string _channel;
    private readonly EventLogWatcher _watcher;
    private readonly Action<EventRecordView> _onEvent;
    private readonly Action<Exception> _onError;

    /// <summary>Creates a subscription. Nothing is delivered until <see cref="Start"/>.</summary>
    public EventLogSubscription(string channel, string structuredQuery, Action<EventRecordView> onEvent, Action<Exception> onError)
    {
        _channel = channel;
        _onEvent = onEvent;
        _onError = onError;
        _watcher = new EventLogWatcher(new EventLogQuery(channel, PathType.LogName, structuredQuery), bookmark: null, readExistingEvents: false);
        _watcher.EventRecordWritten += OnEventRecordWritten;
    }

    /// <summary>Starts delivering events.</summary>
    /// <exception cref="EventChannelException">The channel is missing, access is denied or the query is invalid.</exception>
    public void Start()
    {
        try
        {
            _watcher.Enabled = true;
        }
        catch (Exception ex) when (EventLogQueries.Classify(ex) is { } problem)
        {
            throw new EventChannelException(_channel, problem, ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _watcher.EventRecordWritten -= OnEventRecordWritten;
        _watcher.Dispose();
    }

    private void OnEventRecordWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventException is { } error)
        {
            _onError(error);
            return;
        }

        using var record = e.EventRecord;
        if (record is null)
        {
            return;
        }

        try
        {
            _onEvent(new EventRecordView(record));
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the Event Log callback thread.
            _onError(ex);
        }
    }
}
