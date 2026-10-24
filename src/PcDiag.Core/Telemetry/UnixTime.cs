namespace PcDiag.Core.Telemetry;

/// <summary>
/// Conversions between <see cref="DateTimeOffset"/> and the storage timestamp format
/// (Unix epoch milliseconds, UTC).
/// </summary>
public static class UnixTime
{
    /// <summary>Converts a timestamp to Unix epoch milliseconds (UTC).</summary>
    public static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    /// <summary>Converts Unix epoch milliseconds (UTC) to a timestamp.</summary>
    public static DateTimeOffset FromUnixMs(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
}
