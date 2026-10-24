using System.Runtime.InteropServices;
using System.Windows.Forms;
using PcDiag.Core.Configuration;

namespace PcDiag.Agent.Tray;

/// <summary>
/// A hidden window that owns the global capture hotkey (RegisterHotKey) and raises <see cref="Pressed"/> on
/// WM_HOTKEY. Registration fails when another application already uses the same combination.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private bool _registered;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public event EventHandler? Pressed;

    /// <summary>Registers the hotkey; returns the Win32 error when it fails (0 on success).</summary>
    public int Register(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        Unregister();
        _registered = NativeMethods.RegisterHotKey(Handle, HotkeyId, gesture.Modifiers | NativeMethods.ModNoRepeat, gesture.VirtualKey);
        return _registered ? 0 : Marshal.GetLastPInvokeError();
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey && m.WParam == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.WndProc(ref m);
    }
}
