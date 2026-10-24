using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>
/// What the collectors send only once, kept from every frame (stored or not): a process's identity when it is first
/// seen, and the service list on the first poll and then hourly (with the changes between). Frames that are not stored
/// (the live view, watching while not recording) dropped them, so a recording that started later had processes
/// without names (left out of every moment) and no service list until the next hour. Now a process's identity is
/// written before its first stored figures, and the service list (with the changes since) with the first stored frame.
/// </summary>
internal sealed class SentOnce
{
    private readonly Dictionary<ProcessKey, ProcessIdentityDetails> _identities = [];
    private readonly HashSet<ProcessKey> _identitiesStored = [];
    private readonly HashSet<ProcessKey> _ended = [];
    private (DateTimeOffset At, IReadOnlyList<ServiceInfo> List)? _services;
    private bool _servicesStored;
    private readonly List<ServiceChange> _changesNotStored = [];

    /// <summary>Notes what a frame sends, as it arrives; <paramref name="stored"/>: whether it will be stored.</summary>
    public void Note(SampleFrame frame, bool stored)
    {
        if (frame.Processes is { } processes)
        {
            foreach (var identity in processes.Identities)
            {
                _identities[identity.Key] = identity;
            }

            foreach (var e in processes.Events)
            {
                if (e.Kind == Core.Events.ProcessEventKind.Stopped)
                {
                    _ended.Add(e.Key);
                }
            }
        }

        if (frame.Services is { } services)
        {
            if (services.Snapshot is { } list)
            {
                _services = (services.Timestamp, list);
                _servicesStored = stored;
                _changesNotStored.Clear();
            }
            else if (!stored)
            {
                _changesNotStored.AddRange(services.Changes);
            }
        }
    }

    /// <summary>A stored frame wrote this process's identity.</summary>
    public void IdentityStored(ProcessKey key) => _identitiesStored.Add(key);

    /// <summary>The identity to write before a process's figures (null: written already, or not known).</summary>
    public ProcessIdentityDetails? IdentityToStore(ProcessKey key)
    {
        if (_identitiesStored.Contains(key) || !_identities.TryGetValue(key, out var identity))
        {
            return null;
        }

        _identitiesStored.Add(key);
        return identity;
    }

    /// <summary>
    /// The service list and changes to write before a stored frame that has no list of its own (null: nothing): the list
    /// kept at the time it was taken, and the changes since that were not stored, so a moment replays them as usual.
    /// </summary>
    public (DateTimeOffset At, IReadOnlyList<ServiceInfo>? List, IReadOnlyList<ServiceChange> Changes)? ServicesToStore(SampleFrame frame)
    {
        if (frame.Services?.Snapshot is not null || _services is not { } kept || (_servicesStored && _changesNotStored.Count == 0))
        {
            return null;
        }

        var result = (kept.At, _servicesStored ? null : kept.List, (IReadOnlyList<ServiceChange>)[.. _changesNotStored]);
        _servicesStored = true;
        _changesNotStored.Clear();
        return result;
    }

    /// <summary>After a flush: processes that ended are let go (their figures are stored).</summary>
    public void Flushed()
    {
        foreach (var key in _ended)
        {
            _identities.Remove(key);
            _identitiesStored.Remove(key);
        }

        _ended.Clear();
    }

    /// <summary>A flush rolled back: what it wrote is written again.</summary>
    public void RolledBack()
    {
        _identitiesStored.Clear();
        _servicesStored = false;
    }
}
