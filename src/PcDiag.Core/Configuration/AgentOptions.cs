using System.Globalization;
using Microsoft.Extensions.Options;

namespace PcDiag.Core.Configuration;

/// <summary>The Agent's user-facing parts ("Agent" section): tray icon, capture hotkey, notifications.</summary>
public sealed class AgentOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Agent";

    /// <summary>Show the notification-area icon with the capture and recording menu.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// Global hotkey that captures an incident, e.g. "Ctrl+Alt+Shift+I" (works in full-screen games). Empty disables it.
    /// Modifiers: Ctrl, Alt, Shift, Win; keys: A–Z, 0–9, F1–F24, Pause, Insert, Home, End, PageUp, PageDown.
    /// </summary>
    public string CaptureHotkey { get; set; } = "Ctrl+Alt+Shift+I";

    /// <summary>Show a notification when an incident is captured and when it is complete.</summary>
    public bool NotifyOnCapture { get; set; } = true;

    /// <summary>
    /// Full path of PcDiag.Web.exe, started by "Open dashboard" when the dashboard is not running. Empty looks next
    /// to the Agent (the installed layout).
    /// </summary>
    public string? WebHostPath { get; set; }

    /// <summary>
    /// Starts the storage host with the recorder (without administrator rights): it sets the file storage up, keeps the
    /// storage locations up to date and keeps backups of the on-screen layout, with or without the dashboard open.
    /// </summary>
    public bool StorageHost { get; set; } = true;
}

/// <summary>A parsed global hotkey: RegisterHotKey modifier flags and virtual-key code (WinUser.h values).</summary>
/// <param name="Modifiers">MOD_ALT (1), MOD_CONTROL (2), MOD_SHIFT (4), MOD_WIN (8).</param>
/// <param name="VirtualKey">VK code.</param>
/// <param name="Text">Normalized text, e.g. "Ctrl+Alt+Shift+I".</param>
public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey, string Text)
{
    /// <summary>MOD_ALT.</summary>
    public const uint Alt = 0x1;

    /// <summary>MOD_CONTROL.</summary>
    public const uint Control = 0x2;

    /// <summary>MOD_SHIFT.</summary>
    public const uint Shift = 0x4;

    /// <summary>MOD_WIN.</summary>
    public const uint Win = 0x8;

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pause"] = 0x13,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["Insert"] = 0x2D,
    };

    /// <summary>Parses "Ctrl+Alt+Shift+I". Returns null and the problem when invalid.</summary>
    public static HotkeyGesture? TryParse(string? text, out string? error)
    {
        error = null;
        var parts = (text ?? string.Empty).Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(p => p.Length == 0))
        {
            error = "a hotkey needs at least one modifier and a key, e.g. \"Ctrl+Alt+Shift+I\"";
            return null;
        }

        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => Control,
                "ALT" => Alt,
                "SHIFT" => Shift,
                "WIN" or "WINDOWS" => Win,
                _ => 0u,
            };
            if (flag == 0 || (modifiers & flag) != 0)
            {
                error = $"'{part}' is not a modifier (Ctrl, Alt, Shift, Win) or is repeated";
                return null;
            }

            modifiers |= flag;
        }

        // Shift alone would steal ordinary typing (Shift+I is a capital I).
        if ((modifiers & (Control | Alt | Win)) == 0)
        {
            error = "a hotkey needs Ctrl, Alt or Win";
            return null;
        }

        var key = parts[^1];
        uint vk;
        if (key.Length == 1 && char.IsAsciiLetter(key[0]))
        {
            vk = char.ToUpperInvariant(key[0]); // VK_A–VK_Z are 0x41–0x5A
        }
        else if (key.Length == 1 && char.IsAsciiDigit(key[0]))
        {
            vk = key[0]; // VK_0–VK_9 are 0x30–0x39
        }
        else if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
        {
            vk = 0x70u + (uint)(f - 1); // VK_F1 = 0x70 … VK_F24 = 0x87
        }
        else if (!NamedKeys.TryGetValue(key, out vk))
        {
            error = $"'{key}' is not a supported key (A–Z, 0–9, F1–F24, Pause, Insert, Home, End, PageUp, PageDown)";
            return null;
        }

        var names = new List<string>();
        if ((modifiers & Control) != 0)
        {
            names.Add("Ctrl");
        }

        if ((modifiers & Alt) != 0)
        {
            names.Add("Alt");
        }

        if ((modifiers & Shift) != 0)
        {
            names.Add("Shift");
        }

        if ((modifiers & Win) != 0)
        {
            names.Add("Win");
        }

        names.Add(key.Length == 1 ? key.ToUpperInvariant() : NamedKeys.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key.ToUpperInvariant());
        return new HotkeyGesture(modifiers, vk, string.Join("+", names));
    }
}

/// <summary>Validates <see cref="AgentOptions"/>.</summary>
public sealed class AgentOptionsValidator : IValidateOptions<AgentOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.CaptureHotkey) && HotkeyGesture.TryParse(options.CaptureHotkey, out var error) is null)
        {
            return ValidateOptionsResult.Fail($"Agent:CaptureHotkey '{options.CaptureHotkey}': {error}.");
        }

        if (!string.IsNullOrWhiteSpace(options.WebHostPath)
            && (!Path.IsPathFullyQualified(options.WebHostPath) || options.WebHostPath.StartsWith(@"\\", StringComparison.Ordinal)
                || !options.WebHostPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            return ValidateOptionsResult.Fail($"Agent:WebHostPath must be a full local path to PcDiag.Web.exe (was '{options.WebHostPath}').");
        }

        return ValidateOptionsResult.Success;
    }
}
