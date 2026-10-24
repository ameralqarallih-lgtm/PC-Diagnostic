using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>
/// Remembers each series' availability and reports only changes, so SensorAvailability gets one row per series
/// (why it is unavailable, since when) rather than a reason on every sample.
/// </summary>
internal sealed class AvailabilityTracker
{
    private readonly Dictionary<string, Availability> _current = new(StringComparer.Ordinal);
    private readonly List<AvailabilityChange> _changes = [];

    public void Observe(string series, Availability availability, DateTimeOffset at)
    {
        // Pending is transient (a rate's first sample); it says nothing lasting about the series.
        if (availability == Availability.Pending)
        {
            return;
        }

        if (_current.TryGetValue(series, out var previous) && previous == availability)
        {
            return;
        }

        _current[series] = availability;
        _changes.Add(new AvailabilityChange(series, availability, Describe(availability), at));
    }

    public IReadOnlyList<AvailabilityChange> TakeChanges()
    {
        var changes = _changes.ToList();
        _changes.Clear();
        return changes;
    }

    internal static string? Describe(Availability availability) => availability switch
    {
        Availability.Available => null,
        Availability.Unsupported => "Not exposed by this hardware, driver or Windows version, or not provided by an installed sensor provider.",
        Availability.RequiresElevation => "Requires the recorder to run elevated (administrator).",
        Availability.NotPresent => "The device is not present.",
        Availability.Error => "Reading failed; see the Agent log.",
        Availability.Disabled => "Collection disabled in settings.",
        Availability.PermissionDenied => "Blocked by a Windows privacy setting (for Wi-Fi details: location permission).",
        Availability.DriverMissing => "A required driver is not installed (CPU and motherboard sensors need the PawnIO driver: winget install namazso.PawnIO).",
        Availability.NotSelected => "Not recorded: left out of a custom recording.",
        _ => null,
    };
}

/// <summary>A series changed availability.</summary>
internal sealed record AvailabilityChange(string Series, Availability Availability, string? Reason, DateTimeOffset Since);
