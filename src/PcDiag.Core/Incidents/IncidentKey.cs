using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace PcDiag.Core.Incidents;

/// <summary>
/// Human-readable incident identifier, e.g. <c>INC-20260928-00027</c>.
/// The date is the local date the incident was created; the sequence number is global and monotonic.
/// </summary>
/// <param name="Date">Local creation date.</param>
/// <param name="Sequence">Global sequence number (1–99999).</param>
public readonly record struct IncidentKey(DateOnly Date, int Sequence) : ISpanParsable<IncidentKey>
{
    /// <summary>Largest sequence number the format can hold.</summary>
    public const int MaxSequence = 99_999;

    private const string Prefix = "INC-";
    private const int FormattedLength = 18; // INC-yyyyMMdd-nnnnn

    /// <summary>Short display number, e.g. <c>#00027</c>.</summary>
    public string DisplayNumber => "#" + Sequence.ToString("D5", CultureInfo.InvariantCulture);

    /// <summary>Creates a key after validating the sequence.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Sequence is outside 1–99999.</exception>
    public static IncidentKey Create(DateOnly date, int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, MaxSequence);
        return new IncidentKey(date, sequence);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Date:yyyyMMdd}-{Sequence:D5}");

    /// <inheritdoc />
    public static IncidentKey Parse(string s, IFormatProvider? provider) => Parse(s.AsSpan(), provider);

    /// <inheritdoc />
    public static IncidentKey Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        TryParse(s, provider, out var key) ? key : throw new FormatException($"'{s}' is not a valid incident key (expected INC-yyyyMMdd-nnnnn).");

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out IncidentKey result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <summary>Parses an incident key using the invariant culture.</summary>
    public static bool TryParse([NotNullWhen(true)] string? s, out IncidentKey result) =>
        TryParse(s.AsSpan(), null, out result);

    /// <inheritdoc />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out IncidentKey result)
    {
        result = default;
        if (s.Length != FormattedLength || !s.StartsWith(Prefix, StringComparison.Ordinal) || s[12] != '-')
        {
            return false;
        }

        if (!DateOnly.TryParseExact(s.Slice(4, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return false;
        }

        var digits = s.Slice(13, 5);
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        var sequence = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (sequence < 1)
        {
            return false;
        }

        result = new IncidentKey(date, sequence);
        return true;
    }
}
