namespace PcDiag.Agent.Overlay;

/// <summary>
/// The recent values of the readings shown as graphs, kept by the on-screen display while it shows them (the most a
/// graph can show, <see cref="MaxSeconds"/>). Used on the overlay's UI thread only.
/// </summary>
internal sealed class OverlayHistory
{
    /// <summary>The longest history kept.</summary>
    public const int MaxSeconds = 300;

    private readonly Dictionary<string, List<(DateTime At, double Value)>> _points = new(StringComparer.Ordinal);

    /// <summary>Adds a value (a missing one leaves a gap).</summary>
    public void Add(string source, DateTime at, double? value)
    {
        if (!_points.TryGetValue(source, out var points))
        {
            _points[source] = points = [];
        }

        points.Add((at, value is { } v && double.IsFinite(v) ? v : double.NaN));
        var oldest = at.AddSeconds(-MaxSeconds - 5);
        var drop = points.FindIndex(p => p.At >= oldest);
        if (drop > 0)
        {
            points.RemoveRange(0, drop);
        }
    }

    /// <summary>A source's values, oldest first.</summary>
    public IReadOnlyList<(DateTime At, double Value)> Of(string source) => _points.TryGetValue(source, out var points) ? points : [];

    /// <summary>Forgets the sources no graph shows any more.</summary>
    public void KeepOnly(IReadOnlySet<string> sources)
    {
        foreach (var key in _points.Keys.Where(k => !sources.Contains(k)).ToList())
        {
            _points.Remove(key);
        }
    }
}
