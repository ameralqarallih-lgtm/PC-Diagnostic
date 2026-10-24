using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// Handles on the screens themselves for the panels that allow them (<see cref="Core.Overlay.OverlayPanel.Movable"/>,
/// Resizable, Closable): when the mouse rests on such a panel, a window of its size shows a grip bar, a close button and
/// a resize corner, and only those take the mouse; the panel stays click-through, so a click on it still reaches the
/// game or the taskbar under it. The mouse is looked at a few times a second while a shown panel allows handles (no
/// mouse hook), and not at all otherwise. No handles while the cursor is hidden or held in a window (games), a
/// full-screen program is in front, or a button is already down. A closed panel stays hidden until the show/hide
/// hotkey shows the panels again or the tray's "Show closed panels".
/// </summary>
internal sealed partial class OverlayHost
{
    private const int CursorShowing = 0x1;
    private static readonly TimeSpan Rest = TimeSpan.FromMilliseconds(300);
    private readonly HashSet<(string Screen, string Panel)> _closed = [];
    private System.Windows.Forms.Timer? _hoverTimer;
    private LayeredWindow? _handles;
    private (string Screen, string Panel)? _handlesKey;
    private OverlayRenderer.HandleSpots _spots;
    private ArrangePart _handleHover;
    private ArrangePart _handlePressed;
    private (string Screen, string Panel)? _restKey;
    private DateTime _restSince;
    private bool _dragByHandles;
    private volatile int _closedCount;

    /// <summary>How many panels were closed on the screens (read from any thread).</summary>
    public int ClosedCount => _closedCount;

    /// <summary>Shows the panels closed on the screens again.</summary>
    public void ShowClosed()
    {
        if (_closed.Count == 0)
        {
            return;
        }

        _closed.Clear();
        _closedCount = 0;
        Sync();
        Tick();
    }

    /// <summary>The panel windows and the handles over one of them (for keeping them on top).</summary>
    private List<LayeredWindow> OnTop()
    {
        var windows = new List<LayeredWindow>(_windows.Values);
        if (_handles is { } handles && _handlesKey is not null)
        {
            windows.Add(handles);
        }

        return windows;
    }

    /// <summary>Watches the mouse only while a shown panel allows handles (and nothing is being arranged).</summary>
    private void WatchHandles()
    {
        var wanted = !_arranging && Visible && _windows.Keys.Any(k => PanelOf(k)?.Handles == true);
        if (wanted && _hoverTimer is null)
        {
            _hoverTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _hoverTimer.Tick += (_, _) => HoverTick();
            _hoverTimer.Start();
        }
        else if (!wanted && _hoverTimer is not null)
        {
            _hoverTimer.Dispose();
            _hoverTimer = null;
            _restKey = null;
        }

        if (!wanted || _handlesKey is { } key && !_windows.ContainsKey(key))
        {
            HideHandles();
        }
    }

    private void HoverTick()
    {
        if (_drag is not null)
        {
            return;
        }

        // The panel under the mouse that allows handles (the last drawn is on top).
        (string Screen, string Panel)? over = null;
        var cursor = Cursor.Position;
        foreach (var (key, window) in _windows)
        {
            if (PanelOf(key)?.Handles == true && Rectangle.Inflate(window.Bounds, 4, 4).Contains(cursor))
            {
                over = key;
            }
        }

        if (over is not { } found || !HandlesAllowed())
        {
            _restKey = null;
            HideHandles();
            Rate(fast: false);
            return;
        }

        Rate(fast: true);
        if (_handlesKey == found)
        {
            return;
        }

        if (_restKey != found)
        {
            // Resting starts now (crossing a panel shows nothing).
            _restKey = found;
            _restSince = DateTime.UtcNow;
            HideHandles();
            return;
        }

        if (DateTime.UtcNow - _restSince >= Rest && !ButtonDown())
        {
            ShowHandles(found);
        }
    }

    private void Rate(bool fast)
    {
        if (_hoverTimer is { } timer)
        {
            timer.Interval = fast ? 33 : 100;
        }
    }

    private void ShowHandles((string Screen, string Panel) key)
    {
        if (!_windows.TryGetValue(key, out var window) || !_hits.TryGetValue(key, out var hits) || PanelOf(key) is not { } panel
            || _displays.FirstOrDefault(d => d.Key == key.Screen) is not { } display)
        {
            HideHandles();
            return;
        }

        var (bitmap, spots) = OverlayRenderer.Handles(window.Bounds.Size, hits, panel.Style, display.Scale, panel.Movable, panel.Resizable, panel.Closable, _handleHover);
        using (bitmap)
        {
            if (_handles is null)
            {
                _handles = new LayeredWindow(hideFromCapture: true) { Interactive = true };
                _handles.Pointer += OnHandlePointer;
                _handles.CursorAt = HandleCursor;
            }

            _spots = spots;
            _handles.Show(bitmap, window.Bounds.Location, 255);
            _handles.KeepOnTop();
        }

        _handlesKey = key;
    }

    private void HideHandles()
    {
        if (_handlesKey is null)
        {
            return;
        }

        _handles?.Hide();
        _handlesKey = null;
        _handleHover = ArrangePart.None;
        _handlePressed = ArrangePart.None;
    }

    private Cursor HandleCursor(Point point)
    {
        if (_drag is { } drag && _dragByHandles)
        {
            return drag.Kind == DragKind.Resize ? Cursors.SizeNWSE : Cursors.SizeAll;
        }

        return _spots.At(point) switch
        {
            ArrangePart.Close => Cursors.Hand,
            ArrangePart.Grip => Cursors.SizeNWSE,
            ArrangePart.Panel => Cursors.SizeAll,
            _ => Cursors.Default,
        };
    }

    private void OnHandlePointer(PointerEvent e)
    {
        if (_handlesKey is not { } key || !_windows.TryGetValue(key, out var window))
        {
            return;
        }

        switch (e.Action)
        {
            case PointerAction.Move when _drag is { } drag && _dragByHandles:
                DragTo(drag, window, e.Screen);
                if (drag.Kind == DragKind.Move)
                {
                    _handles?.MoveTo(window.Bounds.Location);
                }
                else
                {
                    // The preview at its new size: the handles follow its corner.
                    ShowHandles(key);
                }

                break;
            case PointerAction.Move:
                var part = _spots.At(e.Client);
                if (part != _handleHover)
                {
                    _handleHover = part;
                    ShowHandles(key);
                }

                break;
            case PointerAction.LeftDown:
                _handlePressed = _spots.At(e.Client);
                if (_handlePressed is ArrangePart.Panel or ArrangePart.Grip && PanelOf(key) is { } panel && _displays.FirstOrDefault(d => d.Key == key.Screen) is { } display)
                {
                    // A resize starts from the panel's size now, at 100 % scaling.
                    var box = _hits.TryGetValue(key, out var hits) ? hits.Box : RectangleF.Empty;
                    var size = new SizeF(panel.Width is { } w ? (float)w : box.Width / (float)display.Scale, panel.Height is { } h ? (float)h : box.Height / (float)display.Scale);
                    _drag = new Drag(_handlePressed == ArrangePart.Grip ? DragKind.Resize : DragKind.Move, key, e.Screen, window.Bounds.Location, size, -1);
                    _dragByHandles = true;
                    _handles?.Capture(true);
                }

                break;
            case PointerAction.LeftUp when _drag is { } done && _dragByHandles:
                _handles?.Capture(false);
                _drag = null;
                _dragByHandles = false;
                Finish(done, window);

                // Still resting on it: the handles stay (at its new place, or gone with it to another screen).
                if (_windows.ContainsKey(key))
                {
                    _restKey = key;
                    ShowHandles(key);
                }
                else
                {
                    HideHandles();
                }

                break;
            case PointerAction.LeftUp when _handlePressed == ArrangePart.Close && _spots.At(e.Client) == ArrangePart.Close:
                ClosePanel(key);
                break;
            case PointerAction.Leave when _drag is null && _handleHover != ArrangePart.None:
                _handleHover = ArrangePart.None;
                ShowHandles(key);
                break;
        }
    }

    /// <summary>Closes a panel on the screens: hidden until the panels are shown again (the layout is not changed).</summary>
    private void ClosePanel((string Screen, string Panel) key)
    {
        _closed.Add(key);
        _closedCount = _closed.Count;
        HideHandles();
        Sync();
        Tick();
    }

    /// <summary>Handles may show: the cursor is visible and free (not held by a game), and no full-screen program is in front.</summary>
    internal static bool HandlesAllowed()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0)
        {
            return false;
        }

        if (GetClipCursor(out var clip) && !Rectangle.FromLTRB(clip.Left, clip.Top, clip.Right, clip.Bottom).Contains(SystemInformation.VirtualScreen))
        {
            return false;
        }

        // Busy (a full-screen program), Direct3D full screen or presenting.
        return SHQueryUserNotificationState(out var state) != 0 || state is not (2 or 3 or 4);
    }

    private static bool ButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClipCursor(out ClipRect rect);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public nint Cursor;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct ClipRect(int Left, int Top, int Right, int Bottom);
}
