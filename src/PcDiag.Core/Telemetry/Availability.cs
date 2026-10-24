namespace PcDiag.Core.Telemetry;

/// <summary>
/// Explicit availability of a metric. A missing metric is never represented as a silent zero.
/// </summary>
public enum Availability : byte
{
    /// <summary>The value was read successfully.</summary>
    Available = 0,

    /// <summary>The hardware, driver or Windows version does not expose this metric.</summary>
    Unsupported = 1,

    /// <summary>The metric exists but can only be read by an elevated (administrator) process.</summary>
    RequiresElevation = 2,

    /// <summary>The device the metric belongs to is not present (for example, no discrete GPU).</summary>
    NotPresent = 3,

    /// <summary>Reading the metric failed at runtime; see logs for details.</summary>
    Error = 4,

    /// <summary>The metric needs a previous sample (a rate or delta) and has not been computed yet.</summary>
    Pending = 5,

    /// <summary>The user disabled collection of this metric (privacy or provider settings).</summary>
    Disabled = 6,

    /// <summary>A Windows privacy setting blocks it (e.g. Wi-Fi details need location permission on Windows 11 24H2+).</summary>
    PermissionDenied = 7,

    /// <summary>A required driver is not installed (e.g. the PawnIO driver for CPU and motherboard sensors).</summary>
    DriverMissing = 8,

    /// <summary>Left out of a custom recording: sampled for the live dashboard, not stored.</summary>
    NotSelected = 9,
}

/// <summary>A metric value paired with its availability.</summary>
/// <typeparam name="T">The numeric value type.</typeparam>
/// <param name="Value">The value; only meaningful when <paramref name="Availability"/> is <see cref="Telemetry.Availability.Available"/>.</param>
/// <param name="Availability">Whether the value could be read, and if not, why.</param>
public readonly record struct Reading<T>(T? Value, Availability Availability)
    where T : struct
{
    /// <summary>A reading that has not been computed yet (for example, the first sample of a rate).</summary>
    public static Reading<T> Pending { get; } = new(null, Availability.Pending);

    /// <summary>True when a value was read successfully.</summary>
    public bool HasValue => Availability == Availability.Available && Value.HasValue;

    /// <summary>Creates an available reading.</summary>
    public static Reading<T> Of(T value) => new(value, Availability.Available);

    /// <summary>Creates an unavailable reading with the reason it is unavailable.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reason"/> is <see cref="Availability.Available"/>.</exception>
    public static Reading<T> Unavailable(Availability reason)
    {
        if (reason == Availability.Available)
        {
            throw new ArgumentException("An unavailable reading needs a reason other than Available.", nameof(reason));
        }

        return new(null, reason);
    }

    /// <summary>Returns the value, or <paramref name="fallback"/> when unavailable.</summary>
    public T GetValueOrDefault(T fallback) => HasValue ? Value!.Value : fallback;

    /// <summary>Maps an available value; unavailable readings keep their reason.</summary>
    public Reading<TResult> Map<TResult>(Func<T, TResult> map)
        where TResult : struct
    {
        ArgumentNullException.ThrowIfNull(map);
        return HasValue ? Reading<TResult>.Of(map(Value!.Value)) : new Reading<TResult>(null, Availability);
    }

    /// <inheritdoc />
    public override string ToString() => HasValue ? $"{Value}" : Availability.ToString();
}
