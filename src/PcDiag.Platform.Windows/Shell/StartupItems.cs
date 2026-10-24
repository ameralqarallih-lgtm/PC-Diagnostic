using System.Diagnostics;
using Microsoft.Win32;

namespace PcDiag.Platform.Windows.Shell;

/// <summary>A program that starts at sign-in, from the Run keys or a Startup folder.</summary>
/// <param name="Id">"{location}:{name}", stable while the entry exists.</param>
/// <param name="Program">The program file, when it can be told from the command.</param>
public sealed record StartupItem(string Id, string Name, string Command, string Location, string? Program, bool Enabled);

/// <summary>
/// Startup programs, enabled and disabled the way Task Manager does it: through the StartupApproved keys, so the
/// entry itself is kept and can be enabled again (Windows then starts it at the next sign-in).
/// </summary>
public static class StartupItems
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private sealed record Location(string Id, string Text, RegistryKey Hive, string? RunKey, string? Folder, string ApprovedKey);

    private static IEnumerable<Location> Locations()
    {
        yield return new("hkcu-run", "Your sign-in", Registry.CurrentUser, Run, null, Approved + "Run");
        yield return new("hklm-run", "Everyone's sign-in", Registry.LocalMachine, Run, null, Approved + "Run");
        yield return new("hklm-run32", "Everyone's sign-in (32-bit)", Registry.LocalMachine, Run32, null, Approved + "Run32");
        yield return new("user-folder", "Your Startup folder", Registry.CurrentUser, null, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Approved + "StartupFolder");
        yield return new("common-folder", "Everyone's Startup folder", Registry.LocalMachine, null, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Approved + "StartupFolder");
    }

    /// <summary>Every startup entry with whether it is enabled.</summary>
    public static IReadOnlyList<StartupItem> List()
    {
        var items = new List<StartupItem>();
        foreach (var location in Locations())
        {
            using var approved = location.Hive.OpenSubKey(location.ApprovedKey);
            foreach (var (name, command) in Entries(location))
            {
                items.Add(new StartupItem($"{location.Id}:{name}", name, command, location.Text, ProgramOf(command), IsEnabled(approved?.GetValue(name) as byte[])));
            }
        }

        return items;
    }

    /// <summary>Enables or disables an entry; false when there is no such entry.</summary>
    public static bool SetEnabled(string id, bool enabled, DateTimeOffset now)
    {
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        var location = colon > 0 ? Locations().FirstOrDefault(l => l.Id == id[..colon]) : null;
        var name = colon > 0 ? id[(colon + 1)..] : null;
        if (location is null || string.IsNullOrEmpty(name) || !Entries(location).Any(e => e.Name == name))
        {
            return false;
        }

        // 12 bytes: 02 (enabled) or 03 (disabled) followed by the time it was disabled (FILETIME), as Task Manager writes.
        var value = new byte[12];
        value[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled)
        {
            BitConverter.GetBytes(now.ToFileTime()).CopyTo(value, 4);
        }

        using var key = location.Hive.CreateSubKey(location.ApprovedKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.Binary);
        return true;
    }

    /// <summary>Enabled unless StartupApproved says otherwise (an odd first byte means disabled).</summary>
    public static bool IsEnabled(byte[]? approved) => approved is not { Length: > 0 } || (approved[0] & 1) == 0;

    private static IEnumerable<(string Name, string Command)> Entries(Location location)
    {
        if (location.RunKey is { } runKey)
        {
            using var key = location.Hive.OpenSubKey(runKey);
            if (key is null)
            {
                yield break;
            }

            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
            {
                if (key.GetValue(name) is string command)
                {
                    yield return (name, command);
                }
            }
        }
        else if (location.Folder is { Length: > 0 } folder && Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                yield return (Path.GetFileName(file), file);
            }
        }
    }

    /// <summary>The program in a command line (quoted, or up to ".exe"), or the file itself for a Startup folder entry.</summary>
    public static string? ProgramOf(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : text;
    }

    /// <summary>Who made a program (its version resource), or null.</summary>
    public static string? CompanyOf(string? program)
    {
        if (string.IsNullOrEmpty(program) || !File.Exists(program) || !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var company = FileVersionInfo.GetVersionInfo(program).CompanyName?.Trim();
            return string.IsNullOrEmpty(company) ? null : company;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }
}
