using System.Text.Json;
using PcDiag.Core.Overlay;

namespace PcDiag.Engine.Overlay;

/// <summary>
/// The values' names, groups, units and totals as last seen, kept in the data folder, so a panel shown before its
/// values arrive (the hardware sensors start a few seconds after the recorder) reads their names with a loading mark,
/// never their ids. A value not seen yet is named from its id.
/// </summary>
public sealed class OverlayKnownSources
{
    /// <summary>How long after the start a missing value is still loading (then it is unavailable: "—").</summary>
    public static readonly TimeSpan LoadingTime = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, OverlaySource> _known;
    private bool _changed;
    private long _saved;

    /// <param name="path">The file they are kept in (null: kept in memory only).</param>
    /// <param name="time">The clock.</param>
    public OverlayKnownSources(string? path, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _path = path;
        _time = time;
        _started = time.GetTimestamp();
        _saved = _started;
        _known = Read(path);
    }

    /// <summary>
    /// The values with a stand-in for each one <paramref name="shown"/> that is missing: its name as last seen, no value,
    /// and <see cref="OverlaySource.Loading"/> for the first <see cref="LoadingTime"/>.
    /// </summary>
    public List<OverlaySource> Complete(List<OverlaySource> sources, IEnumerable<string> shown)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(shown);
        lock (_lock)
        {
            foreach (var s in sources)
            {
                var kept = s with { Value = null, Text = null, Loading = false };
                if (!_known.TryGetValue(s.Id, out var was) || was.Label != kept.Label || was.Group != kept.Group || was.Unit != kept.Unit || was.Max != kept.Max)
                {
                    _known[s.Id] = kept;
                    _changed = true;
                }
            }

            var have = sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var loading = _time.GetElapsedTime(_started) < LoadingTime;
            var result = sources;
            foreach (var id in shown.Distinct(StringComparer.Ordinal).Where(id => !have.Contains(id)))
            {
                if (ReferenceEquals(result, sources))
                {
                    result = [.. sources];
                }

                result.Add(_known.TryGetValue(id, out var known)
                    ? known with { Loading = loading }
                    : new OverlaySource(id, OverlayStyles.LabelOf(id), string.Empty, string.Empty, null, Loading: loading));
            }

            if (_changed && _time.GetElapsedTime(_saved) >= SaveEvery)
            {
                Save();
            }

            return result;
        }
    }

    private void Save()
    {
        _changed = false;
        _saved = _time.GetTimestamp();
        if (_path is null)
        {
            return;
        }

        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_known.Values.OrderBy(s => s.Id, StringComparer.Ordinal), Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; written again with the next change.
            _changed = true;
        }
    }

    private static Dictionary<string, OverlaySource> Read(string? path)
    {
        var known = new Dictionary<string, OverlaySource>(StringComparer.Ordinal);
        try
        {
            if (path is not null && File.Exists(path) && new FileInfo(path).Length < 4 << 20
                && JsonSerializer.Deserialize<List<OverlaySource>>(File.ReadAllText(path), Json) is { } list)
            {
                foreach (var s in list.Where(s => s is { Id.Length: > 0, Label: not null }))
                {
                    known[s.Id] = s with { Value = null, Text = null, Loading = false, Group = s.Group ?? string.Empty, Unit = s.Unit ?? string.Empty };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Started afresh: the names come back as the values do.
        }

        return known;
    }
}
