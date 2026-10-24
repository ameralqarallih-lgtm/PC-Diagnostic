using System.Text.RegularExpressions;

namespace PcDiag.Engine.Events;

/// <summary>
/// Removes user names from event text when user-name capture is off (the default). Event messages often contain
/// profile paths ("C:\Users\name\..."), "COMPUTER\user" account names (e.g. User32 1074) or the computer name.
/// Best effort: it replaces profile folder names, the given account names and the computer name.
/// </summary>
public sealed partial class EventRedactor
{
    /// <summary>Replacement for user names.</summary>
    public const string UserPlaceholder = "<user>";

    /// <summary>Replacement for the computer name.</summary>
    public const string ComputerPlaceholder = "<computer>";

    private readonly Regex? _names;
    private readonly string? _computerName;
    private readonly bool _enabled;

    private EventRedactor(bool enabled, IEnumerable<string> userNames, string? computerName)
    {
        _enabled = enabled;
        if (!enabled)
        {
            return;
        }

        // Very short names would match inside ordinary words.
        var names = userNames.Where(n => n.Trim().Length >= 3).Select(n => Regex.Escape(n.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 0)
        {
            _names = new Regex(@"(?<![\p{L}\p{N}_])(" + string.Join("|", names) + @")(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }

        _computerName = computerName is { Length: >= 2 } ? computerName : null;
    }

    /// <summary>A redactor that changes nothing (user-name capture is on).</summary>
    public static EventRedactor None { get; } = new(false, [], null);

    /// <summary>Creates a redactor for the given account names and computer name.</summary>
    public static EventRedactor For(IEnumerable<string> userNames, string? computerName) => new(true, userNames, computerName);

    /// <summary>Redacts one text.</summary>
    public string? Redact(string? text)
    {
        if (!_enabled || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = ProfilePath().Replace(text, "$1" + UserPlaceholder);
        if (_computerName is not null)
        {
            result = Regex.Replace(result, @"(?<![\p{L}\p{N}_-])" + Regex.Escape(_computerName) + @"(?![\p{L}\p{N}_-])", ComputerPlaceholder, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }

        if (_names is not null)
        {
            result = _names.Replace(result, UserPlaceholder);
        }

        return result;
    }

    // "\Users\<name>" in any path, except the shared profiles. Also matches forward slashes.
    [GeneratedRegex(@"([\\/]Users[\\/])(?!(?:Public|Default|All Users|Default User)(?:[\\/]|$))[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfilePath();
}
