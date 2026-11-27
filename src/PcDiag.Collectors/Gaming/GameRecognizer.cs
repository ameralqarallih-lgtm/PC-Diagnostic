using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using PcDiag.Collectors.Gpu;
using PcDiag.Core.Configuration;

namespace PcDiag.Collectors.Gaming;

/// <summary>
/// Whether the app in front is a game, full screen or windowed. The user's choice first ("Not a game", "Count as a
/// game"), then the apps that are never games (browsers, video players, the shell); then any of: it covers its whole
/// monitor; Windows lists it as a game (the games Game Bar and Game Mode recognize, under
/// HKCU\System\GameConfigStore\Children, read every 10 minutes); it is installed in a game library folder (Steam, Epic,
/// Riot, GOG, Xbox, EA, Ubisoft, Rockstar, Amazon, itch); it keeps the GPU's 3D engine busy (<see cref="Min3dPercent"/>).
/// Process paths are looked up once per process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameRecognizer(IOptionsMonitor<RecorderOptions> options, ProcessGpuActivity gpu)
{
    /// <summary>A windowed app keeping the 3D engine this busy (%) is drawing like a game.</summary>
    public const double Min3dPercent = 25;

    /// <summary>Folders games are installed in by their stores and launchers.</summary>
    internal static readonly string[] LibraryFolders =
    [
        @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\GOG Galaxy\Games\", @"\GOG Games\", @"\XboxGames\", @"\ModifiableWindowsApps\",
        @"\EA Games\", @"\Electronic Arts\", @"\Ubisoft Game Launcher\games\", @"\Rockstar Games\", @"\Amazon Games\Library\", @"\itch\apps\",
    ];

    private static readonly TimeSpan WindowsListEvery = TimeSpan.FromMinutes(10);

    private readonly Dictionary<int, string?> _paths = [];
    private HashSet<string> _windowsGames = new(StringComparer.OrdinalIgnoreCase);
    private long _windowsListAt = long.MinValue;

    /// <summary>Why the app in front counts as a game ("user", "full screen", "windows", "library", "graphics"), or null when it does not.</summary>
    public string? Recognize(int processId, string? name, bool fullScreen)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var current = options.CurrentValue;
        if (current.NotGames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        if (current.Games.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return "user";
        }

        if (FrameCollector.NotGames.Contains(name))
        {
            return null;
        }

        if (fullScreen)
        {
            return "full screen";
        }

        var path = PathOf(processId);
        return Classify(path, WindowsGames(), gpu.ThreeDPercentOf(processId));
    }

    /// <summary>The windowed rules: Windows' list, a library folder, the 3D engine (exposed for tests).</summary>
    internal static string? Classify(string? path, IReadOnlySet<string> windowsGames, double threeDPercent)
    {
        if (path is not null && windowsGames.Contains(path))
        {
            return "windows";
        }

        if (path is not null && LibraryFolders.Any(folder => path.Contains(folder, StringComparison.OrdinalIgnoreCase)))
        {
            return "library";
        }

        return threeDPercent >= Min3dPercent ? "graphics" : null;
    }

    private string? PathOf(int processId)
    {
        lock (_paths)
        {
            if (_paths.TryGetValue(processId, out var known))
            {
                return known;
            }
        }

        string? path = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            path = process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // It ended, or cannot be read.
        }

        lock (_paths)
        {
            if (_paths.Count > 512)
            {
                _paths.Clear();
            }

            _paths[processId] = path;
        }

        return path;
    }

    /// <summary>The executables Windows recognizes as games (Game Bar's list), read again every 10 minutes.</summary>
    private HashSet<string> WindowsGames()
    {
        var now = Stopwatch.GetTimestamp();
        if (_windowsListAt != long.MinValue && Stopwatch.GetElapsedTime(_windowsListAt, now) < WindowsListEvery)
        {
            return _windowsGames;
        }

        _windowsListAt = now;
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var name in children?.GetSubKeyNames() ?? [])
            {
                using var child = children!.OpenSubKey(name);
                if (child?.GetValue("MatchedExeFullPath") is string path && path.Length > 0)
                {
                    games.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not readable: the other rules still apply.
        }

        _windowsGames = games;
        return games;
    }
}
