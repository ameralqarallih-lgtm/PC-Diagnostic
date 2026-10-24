using System.Globalization;
using System.Text.RegularExpressions;

namespace PcDiag.Engine.Events;

/// <summary>
/// A parsed field source from a rule, e.g. <c>"ProcessId"</c>, <c>"#1"</c>, <c>"$binaryText"</c> or
/// <c>"EventName|#2"</c> (first non-empty alternative), optionally with a transform: <c>"BugcheckCode:hex"</c>.
/// </summary>
internal sealed partial class EventFieldSpec
{
    private readonly (Source Source, Transform Transform)[] _alternatives;

    private EventFieldSpec((Source, Transform)[] alternatives) => _alternatives = alternatives;

    private enum SourceKind
    {
        Named,
        Positional,
        ExecutionProcessId,
        BinaryText,
        Provider,
        EventId,
    }

    private enum Transform
    {
        None,
        Hex,
        Prefix0x,
        File,
        FirstLineValue,
    }

    /// <summary>Parses a spec. Returns null and an error when it is invalid.</summary>
    public static EventFieldSpec? TryParse(string? spec, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(spec) || spec.Length > 200)
        {
            error = "a field source must be 1–200 characters";
            return null;
        }

        var alternatives = new List<(Source, Transform)>();
        foreach (var part in spec.Split('|'))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            var sourceText = (colon < 0 ? part : part[..colon]).Trim();
            var transformText = colon < 0 ? string.Empty : part[(colon + 1)..].Trim();

            Source source;
            if (sourceText.StartsWith('#'))
            {
                if (!int.TryParse(sourceText.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index > 99)
                {
                    error = $"'{sourceText}' is not a valid position (#0–#99)";
                    return null;
                }

                source = new Source(SourceKind.Positional, null, index);
            }
            else if (sourceText.StartsWith('$'))
            {
                SourceKind? kind = sourceText switch
                {
                    "$pid" => SourceKind.ExecutionProcessId,
                    "$binaryText" => SourceKind.BinaryText,
                    "$provider" => SourceKind.Provider,
                    "$eventId" => SourceKind.EventId,
                    _ => null,
                };
                if (kind is null)
                {
                    error = $"'{sourceText}' is not a known source ($pid, $binaryText, $provider, $eventId)";
                    return null;
                }

                source = new Source(kind.Value, null, 0);
            }
            else if (NamePattern().IsMatch(sourceText))
            {
                source = new Source(SourceKind.Named, sourceText, 0);
            }
            else
            {
                error = $"'{sourceText}' is not a valid field name";
                return null;
            }

            Transform? transform = transformText switch
            {
                "" => Transform.None,
                "hex" => Transform.Hex,
                "prefix0x" => Transform.Prefix0x,
                "file" => Transform.File,
                "firstLineValue" => Transform.FirstLineValue,
                _ => null,
            };
            if (transform is null)
            {
                error = $"'{transformText}' is not a known transform (hex, prefix0x, file, firstLineValue)";
                return null;
            }

            alternatives.Add((source, transform.Value));
        }

        return new EventFieldSpec([.. alternatives]);
    }

    /// <summary>The first non-empty alternative, transformed; null when none has a value.</summary>
    public string? Evaluate(EventRecordData record)
    {
        foreach (var (source, transform) in _alternatives)
        {
            var raw = source.Kind switch
            {
                SourceKind.Named => record.Get(source.Name!),
                SourceKind.Positional => record.At(source.Index),
                SourceKind.ExecutionProcessId => record.ExecutionProcessId?.ToString(CultureInfo.InvariantCulture),
                SourceKind.BinaryText => record.BinaryText(),
                SourceKind.Provider => record.Provider,
                SourceKind.EventId => record.EventId.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };

            var value = Apply(transform, EventValues.Clean(raw));
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? Apply(Transform transform, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        switch (transform)
        {
            case Transform.Hex:
                return EventValues.TryParseInteger(value, out var number)
                    ? "0x" + number.ToString("X", CultureInfo.InvariantCulture)
                    : value;
            case Transform.Prefix0x:
                return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? "0x" + value[2..].ToUpperInvariant() : "0x" + value.ToUpperInvariant();
            case Transform.File:
                // User32 1074 logs "C:\...\App.exe (COMPUTER)": drop such a suffix along with the folder.
                var paren = value.LastIndexOf(" (", StringComparison.Ordinal);
                if (value.EndsWith(')') && paren > 0)
                {
                    value = value[..paren];
                }

                var slash = value.LastIndexOfAny(['\\', '/']);
                return slash >= 0 && slash < value.Length - 1 ? value[(slash + 1)..] : value;
            case Transform.FirstLineValue:
                var line = value.Split('\n', 2)[0].Trim();
                var separator = line.IndexOf(": ", StringComparison.Ordinal);
                return separator >= 0 ? line[(separator + 2)..].Trim() : line;
            default:
                return value;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex NamePattern();

    private readonly record struct Source(SourceKind Kind, string? Name, int Index);
}

/// <summary>Parsing helpers for values as the Event Log renders them.</summary>
public static class EventValues
{
    /// <summary>
    /// Trims a value and removes the invisible left-to-right/right-to-left marks Windows puts into rendered dates
    /// and times (e.g. EventLog 6008 logs the date 9/23/2026 with a mark before each part).
    /// </summary>
    public static string? Clean(string? text) =>
        text?.Replace("\u200E", string.Empty, StringComparison.Ordinal).Replace("\u200F", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>Parses a decimal or "0x"-prefixed hexadecimal integer.</summary>
    public static bool TryParseInteger(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
            {
                return false;
            }

            value = unchecked((long)hex);
            return true;
        }

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        // Unsigned 64-bit values (e.g. bug check parameters) are kept bit-for-bit.
        if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned))
        {
            value = unchecked((long)unsigned);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a process ID logged as decimal or as hex ("0x9b4", as Application Error 1000 does).
    /// Returns null for anything that is not a plausible process ID.
    /// </summary>
    public static int? ParseProcessId(string? text) =>
        TryParseInteger(text, out var value) && value is > 0 and <= int.MaxValue ? (int)value : null;

    /// <summary>
    /// Parses a timestamp logged as a FILETIME (hex "0x1dd524fd8764b1c" or decimal) or as ISO 8601, and returns UTC
    /// <see cref="DateTime.Ticks"/> (the unit of <see cref="Core.Telemetry.ProcessKey.CreateTimeUtcTicks"/>).
    /// </summary>
    public static long? ParseTimestampTicks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (TryParseInteger(text, out var fileTime))
        {
            // Plausible FILETIMEs only: after 2000-01-01 and before 2200-01-01.
            const long min = 125_911_584_000_000_000L;
            const long max = 189_027_504_000_000_000L;
            return fileTime is > min and < max ? DateTime.FromFileTimeUtc(fileTime).Ticks : null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.UtcTicks
            : null;
    }
}
