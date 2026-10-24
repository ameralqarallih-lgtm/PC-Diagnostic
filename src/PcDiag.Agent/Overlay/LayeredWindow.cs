using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PcDiag.Agent.Overlay;

/// <summary>What the mouse did over an interactive <see cref="LayeredWindow"/>.</summary>
internal enum PointerAction
{
    Move,
    LeftDown,
    LeftUp,
    RightUp,
    Leave,
}

/// <summary>A mouse event: where in the window (its bitmap's pixels) and where on the desktop.</summary>
internal readonly record struct PointerEvent(PointerAction Action, Point Client, Point Screen);

/// <summary>
/// A borderless, always-on-top window that shows a bitmap with per-pixel transparency (UpdateLayeredWindow) and lets
/// every click and key go through to what is underneath: it never takes the focus, is not in Alt+Tab or the taskbar,
/// and, when asked, stays out of screenshots, recordings and streams (WDA_EXCLUDEFROMCAPTURE). While
/// <see cref="Interactive"/> (the panels are being arranged) it takes the mouse instead, still without taking the
/// focus. Constants follow WinUser.h. Create, show and update it on its UI thread.
/// </summary>
internal sealed partial class LayeredWindow : NativeWindow, IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x0084;
    private const int WmSetCursor = 0x0020;
    private const int WmMouseActivate = 0x0021;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmMouseLeave = 0x02A3;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;
    private const int HtClient = 1;
    private const int GwlExStyle = -20;
    private const uint TmeLeave = 0x2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint GwHwndPrev = 3;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint UlwAlpha = 0x2;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;
    private static readonly nint HwndTopmost = -1;
    private bool _shown;
    private bool _interactive;
    private bool _tracking;

    public LayeredWindow(bool hideFromCapture)
    {
        CreateHandle(new CreateParams
        {
            Caption = "PC Diagnostic readings",
            Style = WsPopup,
            ExStyle = WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate,
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
        });
        HideFromCapture(hideFromCapture);
    }

    /// <summary>Where it is and how big, in desktop pixels.</summary>
    public Rectangle Bounds { get; private set; }

    /// <summary>The mouse over it (only while <see cref="Interactive"/>).</summary>
    public event Action<PointerEvent>? Pointer;

    /// <summary>The cursor to show at a point of the window (while <see cref="Interactive"/>).</summary>
    public Func<Point, Cursor>? CursorAt { get; set; }

    /// <summary>Takes the mouse (arranging) instead of letting clicks through; it never takes the focus either way.</summary>
    public bool Interactive
    {
        get => _interactive;
        set
        {
            if (_interactive == value)
            {
                return;
            }

            _interactive = value;
            var style = (long)GetWindowLongPtr(Handle, GwlExStyle);
            style = value ? style & ~WsExTransparent : style | WsExTransparent;
            SetWindowLongPtr(Handle, GwlExStyle, (nint)style);
        }
    }

    /// <summary>Keeps getting the mouse while a button is held (a drag that leaves the window).</summary>
    public void Capture(bool on)
    {
        if (on)
        {
            SetCapture(Handle);
        }
        else
        {
            ReleaseCapture();
        }
    }

    /// <summary>Moves it without drawing it again (a panel being dragged).</summary>
    public void MoveTo(Point position)
    {
        SetWindowPos(Handle, 0, position.X, position.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        Bounds = new Rectangle(position, Bounds.Size);
    }

    /// <summary>Keeps the window out of screenshots and recordings (or lets it in again).</summary>
    public void HideFromCapture(bool hide) => SetWindowDisplayAffinity(Handle, hide ? WdaExcludeFromCapture : WdaNone);

    /// <summary>Shows the bitmap at a desktop position with an overall opacity (0-255).</summary>
    public void Show(Bitmap bitmap, Point position, byte opacity)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var screen = GetDC(nint.Zero);
        var memory = CreateCompatibleDC(screen);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(memory, hBitmap);
        try
        {
            var size = new NativeSize(bitmap.Width, bitmap.Height);
            var source = new NativePoint(0, 0);
            var target = new NativePoint(position.X, position.Y);
            var blend = new BlendFunction(AcSrcOver, 0, opacity, AcSrcAlpha);
            UpdateLayeredWindow(Handle, screen, ref target, ref size, memory, ref source, 0, ref blend, UlwAlpha);
            Bounds = new Rectangle(position, bitmap.Size);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(hBitmap);
            DeleteDC(memory);
            _ = ReleaseDC(nint.Zero, screen);
        }

        if (!_shown)
        {
            ShowWindow(Handle, SwShowNoActivate);
            _shown = true;
        }
    }

    /// <summary>Puts it back above other always-on-top windows (a game or the taskbar may have covered it).</summary>
    public void KeepOnTop() => SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>The window just above it (0 at the very top).</summary>
    internal nint WindowAbove => GetWindow(Handle, GwHwndPrev);

    /// <summary>
    /// Puts windows back above other always-on-top windows in one go, in this order (the first in front), each just
    /// under the one before it. Raising each to the very top in turn let a panel under another jump in front of it for
    /// a moment at every raise, so two panels over each other flickered as if racing for the front.
    /// </summary>
    public static void Stack(IReadOnlyList<LayeredWindow> frontToBack)
    {
        ArgumentNullException.ThrowIfNull(frontToBack);
        if (frontToBack.Count == 0)
        {
            return;
        }

        var batch = BeginDeferWindowPos(frontToBack.Count);
        var after = HwndTopmost;
        foreach (var window in frontToBack)
        {
            if (batch != 0)
            {
                batch = DeferWindowPos(batch, window.Handle, after, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            }

            after = window.Handle;
        }

        if (batch != 0)
        {
            EndDeferWindowPos(batch);
            return;
        }

        // Not batched (out of memory): one at a time, still each under the one before it.
        after = HwndTopmost;
        foreach (var window in frontToBack)
        {
            SetWindowPos(window.Handle, after, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            after = window.Handle;
        }
    }

    /// <summary>Hides it until the next <see cref="Show"/>.</summary>
    public void Hide()
    {
        if (_shown)
        {
            ShowWindow(Handle, SwHide);
            _shown = false;
        }
    }

    public void Dispose() => DestroyHandle();

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmNcHitTest:
                // Clicks fall through to the window underneath, except while arranging.
                m.Result = _interactive ? HtClient : HtTransparent;
                return;
            case WmMouseActivate when _interactive:
                m.Result = MaNoActivate;
                return;
            case WmSetCursor when _interactive && CursorAt is { } cursorAt:
                Cursor.Current = cursorAt(PointToClient(Cursor.Position));
                m.Result = 1;
                return;
            case WmMouseMove when _interactive:
                if (!_tracking)
                {
                    var track = new TrackMouseEventInfo((uint)Marshal.SizeOf<TrackMouseEventInfo>(), TmeLeave, Handle, 0);
                    _tracking = TrackMouseEvent(ref track);
                }

                Raise(PointerAction.Move, m.LParam);
                return;
            case WmLButtonDown when _interactive:
                Raise(PointerAction.LeftDown, m.LParam);
                return;
            case WmLButtonUp when _interactive:
                Raise(PointerAction.LeftUp, m.LParam);
                return;
            case WmRButtonUp when _interactive:
                Raise(PointerAction.RightUp, m.LParam);
                return;
            case WmMouseLeave:
                _tracking = false;
                if (_interactive)
                {
                    Pointer?.Invoke(new PointerEvent(PointerAction.Leave, Point.Empty, Cursor.Position));
                }

                return;
        }

        base.WndProc(ref m);
    }

    private Point PointToClient(Point screen) => new(screen.X - Bounds.X, screen.Y - Bounds.Y);

    private void Raise(PointerAction action, nint lParam)
    {
        var client = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
        Pointer?.Invoke(new PointerEvent(action, client, new Point(Bounds.X + client.X, Bounds.Y + client.Y)));
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(nint hwnd, nint hdcDst, ref NativePoint pptDst, ref NativeSize psize, nint hdcSrc, ref NativePoint pptSrc, uint crKey, ref BlendFunction pblend, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    private static partial nint BeginDeferWindowPos(int count);

    [LibraryImport("user32.dll")]
    private static partial nint DeferWindowPos(nint batch, nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndDeferWindowPos(nint batch);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint SetCapture(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(ref TrackMouseEventInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private record struct TrackMouseEventInfo(uint Size, uint Flags, nint Window, uint HoverTime);

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativeSize(int Cx, int Cy);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private record struct BlendFunction(byte BlendOp, byte BlendFlags, byte SourceConstantAlpha, byte AlphaFormat);
}
