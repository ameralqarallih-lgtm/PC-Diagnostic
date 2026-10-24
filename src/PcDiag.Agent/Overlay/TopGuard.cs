using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// Takes the top back for the panels as soon as the taskbar (or another always-on-top window) is brought over them.
/// Clicking the taskbar raises it above every always-on-top window, through a click-through panel too; Windows says so
/// with a foreground change only the first time (the taskbar already in front, only explorer's mouse capture tells),
/// so a foreground change to the taskbar or to an always-on-top window over a panel, a mouse capture in explorer, and a
/// change of the windows' order that leaves something over a panel each raise the panels at once and again a moment
/// later. Raises are at most 10 a second; a window that keeps taking the top back is left to the regular raise for a
/// while. Start, Search and the notification flyouts are above every application window: the panels return when
/// they close. Out-of-context WinEvents, on the overlay's UI thread (which must pump messages); constants follow
/// WinUser.h.
/// </summary>
internal sealed partial class TopGuard : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemCaptureStart = 0x0008;
    private const uint EventObjectReorder = 0x8004;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const uint GwHwndPrev = 3;
    private const int Fighting = 20;

    /// <summary>When the panels are raised again after a trigger (ms after it): explorer reorders in steps.</summary>
    private static readonly int[] Again = [100, 300, 1000];

    private readonly Func<IReadOnlyCollection<LayeredWindow>> _windows;
    private readonly Action _raise;
    private readonly Func<bool> _paused;
    private readonly ILogger _logger;
    private readonly WinEventProc _proc;
    private readonly List<nint> _hooks = [];
    private readonly System.Windows.Forms.Timer _again = new();
    private readonly Queue<long> _triggers = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastRaise = -1000;
    private long _restUntil;
    private int _step;
    private nint _tray;
    private uint _shell;

    /// <param name="windows">The panel windows shown now.</param>
    /// <param name="raise">Raises the panels (and the windows that go above them).</param>
    /// <param name="paused">Not now (a menu of the panels is open).</param>
    /// <param name="logger">For a window that keeps taking the top back.</param>
    public TopGuard(Func<IReadOnlyCollection<LayeredWindow>> windows, Action raise, Func<bool> paused, ILogger logger)
    {
        _windows = windows;
        _raise = raise;
        _paused = paused;
        _logger = logger;
        _proc = OnEvent;
        var callback = Marshal.GetFunctionPointerForDelegate(_proc);
        foreach (var e in new[] { EventSystemForeground, EventSystemCaptureStart, EventObjectReorder })
        {
            var hook = SetWinEventHook(e, e, 0, callback, 0, 0, WineventSkipOwnProcess);
            if (hook != 0)
            {
                _hooks.Add(hook);
            }
        }

        _again.Tick += (_, _) => RaiseAgain();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void WinEventProc(nint hook, uint e, nint hwnd, int idObject, int idChild, uint thread, uint time);

    public void Dispose()
    {
        _again.Dispose();
        foreach (var hook in _hooks)
        {
            UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    private void OnEvent(nint hook, uint e, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == 0 || _windows().Count == 0 || _paused())
        {
            return;
        }

        var covered = e switch
        {
            EventSystemForeground => IsTaskbar(hwnd) || (IsTopmost(hwnd) && OverPanel(hwnd)),
            EventSystemCaptureStart => ProcessOf(hwnd) == ShellProcess(),
            EventObjectReorder => hwnd == GetDesktopWindow() && SomethingAbove(),
            _ => false,
        };
        if (covered)
        {
            Start();
        }
    }

    /// <summary>Raises at once and starts the raises a moment later; rests while one window keeps fighting for the top.</summary>
    private void Start()
    {
        var now = _clock.ElapsedMilliseconds;
        if (now < _restUntil)
        {
            // Another window keeps taking the top: not a raise for each of its moves, but still twice a second (the
            // panels stay on top of everything, only a moment under it at most).
            if (now - _lastRaise >= 500)
            {
                Raise(now);
            }

            return;
        }

        _triggers.Enqueue(now);
        while (_triggers.Peek() < now - 5000)
        {
            _triggers.Dequeue();
        }

        if (_triggers.Count > Fighting)
        {
            _restUntil = now + 30_000;
            _triggers.Clear();
            _again.Stop();
            LogFighting(_logger);
            return;
        }

        Raise(now);
        _step = 0;
        _again.Stop();
        _again.Interval = Again[0];
        _again.Start();
    }

    private void RaiseAgain()
    {
        _again.Stop();
        Raise(_clock.ElapsedMilliseconds);
        _step++;
        if (_step < Again.Length)
        {
            _again.Interval = Again[_step] - Again[_step - 1];
            _again.Start();
        }
    }

    private void Raise(long now)
    {
        if (now - _lastRaise < 100 || _paused())
        {
            return;
        }

        _lastRaise = now;
        _raise();
    }

    /// <summary>An always-on-top window of another program over one of the panels.</summary>
    private bool SomethingAbove()
    {
        var windows = _windows();
        var ours = windows.Select(w => w.Handle).ToHashSet();
        foreach (var window in windows)
        {
            for (var h = GetWindow(window.Handle, GwHwndPrev); h != 0; h = GetWindow(h, GwHwndPrev))
            {
                if (!ours.Contains(h) && IsWindowVisible(h) && IsTopmost(h) && Overlaps(h, window.Bounds))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool OverPanel(nint hwnd) => _windows().Any(w => Overlaps(hwnd, w.Bounds));

    private static bool Overlaps(nint hwnd, Rectangle bounds) =>
        GetWindowRect(hwnd, out var r) && Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).IntersectsWith(bounds);

    private static bool IsTopmost(nint hwnd) => ((long)GetWindowLongPtr(hwnd, GwlExStyle) & WsExTopmost) != 0;

    private static bool IsTaskbar(nint hwnd)
    {
        var name = new char[32];
        var length = GetClassName(hwnd, name, name.Length);
        return new string(name, 0, Math.Max(0, length)) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static uint ProcessOf(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var process);
        return process;
    }

    /// <summary>Explorer's process (the taskbar's), found again when explorer restarts.</summary>
    private uint ShellProcess()
    {
        if (_tray == 0 || !IsWindow(_tray))
        {
            _tray = FindWindow("Shell_TrayWnd", null);
            _shell = _tray == 0 ? 0 : ProcessOf(_tray);
        }

        return _shell;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Another always-on-top window keeps covering the on-screen readings; for 30 s they take the top back every 2 seconds only")]
    private static partial void LogFighting(ILogger logger);

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint min, uint max, nint module, nint callback, uint process, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out NativeRect rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(nint hwnd, [Out] char[] name, int max);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint process);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string className, string? windowName);

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativeRect(int Left, int Top, int Right, int Bottom);
}
