using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcDiag.Platform.Windows.Shell;

/// <summary>Starts programs and opens links without administrator rights, whatever the caller has.</summary>
public interface IUnelevatedLauncher
{
    /// <summary>
    /// Starts <paramref name="file"/> (a program or a URL) as the signed-in user without administrator rights.
    /// False when it could not be handed over (e.g. Explorer is not running).
    /// </summary>
    bool TryStart(string file, string? arguments, string? workingDirectory, bool hidden);
}

/// <summary>
/// Asks the desktop's Explorer to start the program, the documented way for an elevated process to start one that
/// is not elevated ("How can I launch an unelevated process from my elevated process", The Old New Thing): the
/// desktop folder view's <c>Shell.Application.ShellExecute</c> runs inside Explorer, so the new process gets
/// Explorer's ordinary token and is Explorer's child. The recorder runs as administrator; the dashboard host and the
/// browser must not inherit that.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopShell : IUnelevatedLauncher
{
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioBackground = 0;
    private const int SwHide = 0;
    private const int SwShowNormal = 1;

    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    /// <inheritdoc />
    public bool TryStart(string file, string? arguments, string? workingDirectory, bool hidden)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        // One start at a time, on a thread of its own (single-threaded, as Explorer's objects expect), given at most
        // 15 s: two helpers asking Explorer at once, or Explorer busy (a service it hosts restarting), could leave a
        // call waiting for ever, and the helper was then never started nor tried again. Past the limit it counts as
        // not started (the caller tries again); the waiting thread is left to end by itself.
        lock (Gate)
        {
            var started = false;
            var thread = new Thread(() => started = TryStartOnThisThread(file, arguments, workingDirectory, hidden)) { IsBackground = true, Name = "pcdiag-shell-start" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread.Join(StartLimit) && started;
        }
    }

    private static readonly object Gate = new();
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(15);

    private static bool TryStartOnThisThread(string file, string? arguments, string? workingDirectory, bool hidden)
    {
        object? shellWindows = null;
        object? desktop = null;
        IShellBrowser? browser = null;
        IShellView? view = null;
        object? folderView = null;
        try
        {
            shellWindows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: true)!);
            object location = CsidlDesktop;
            object root = Missing;
            desktop = ((IShellWindows)shellWindows!).FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch);
            if (desktop is not IComServiceProvider services)
            {
                return false;
            }

            var shellBrowserId = typeof(IShellBrowser).GUID;
            var service = TopLevelBrowserService;
            if (services.QueryService(ref service, ref shellBrowserId, out var pointer) != 0 || pointer == 0)
            {
                return false;
            }

            browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pointer);
            Marshal.Release(pointer);
            if (browser.QueryActiveShellView(out view) != 0 || view is null)
            {
                return false;
            }

            var dispatchId = typeof(IDispatchMarker).GUID;
            if (view.GetItemObject(SvgioBackground, ref dispatchId, out folderView) != 0 || folderView is null)
            {
                return false;
            }

            // IShellFolderViewDual.Application is Shell.Application (IShellDispatch2), running in Explorer.
            dynamic shell = ((dynamic)folderView).Application;
            try
            {
                shell.ShellExecute(file, arguments ?? string.Empty, workingDirectory ?? string.Empty, "open", hidden ? SwHide : SwShowNormal);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }

            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException or TypeLoadException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
        finally
        {
            foreach (var com in new[] { folderView, view, browser, desktop, shellWindows })
            {
                if (com is not null && Marshal.IsComObject(com))
                {
                    Marshal.FinalReleaseComObject(com);
                }
            }
        }
    }

    private static object Missing => System.Reflection.Missing.Value;

    /// <summary>IShellWindows (exdisp.h); only FindWindowSW is called, the earlier slots keep the layout.</summary>
    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object Item(object index);

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object NewEnum();

        void Register([MarshalAs(UnmanagedType.IDispatch)] object window, int hwnd, int classId, out int cookie);

        void RegisterPending(int threadId, ref object location, ref object root, int classId, out int cookie);

        void Revoke(int cookie);

        void OnNavigate(int cookie, ref object location);

        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(ref object location, ref object root, int classId, out int hwnd, int options);
    }

    /// <summary>IServiceProvider (servprov.h).</summary>
    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, out nint pointer);
    }

    /// <summary>IShellBrowser (shobjidl_core.h), through QueryActiveShellView.</summary>
    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out nint hwnd);

        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);

        void InsertMenusSB(nint shared, nint menuWidths);

        void SetMenuSB(nint shared, nint oleMenu, nint activeObject);

        void RemoveMenusSB(nint shared);

        void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);

        void EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);

        void TranslateAcceleratorSB(nint message, ushort id);

        void BrowseObject(nint pidl, uint flags);

        void GetViewStateStream(uint mode, out nint stream);

        void GetControlWindow(uint id, out nint hwnd);

        void SendControlMsg(uint id, uint message, nint wParam, nint lParam, out nint result);

        [PreserveSig]
        int QueryActiveShellView(out IShellView? view);
    }

    /// <summary>IShellView (shobjidl_core.h), through GetItemObject.</summary>
    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out nint hwnd);

        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);

        void TranslateAccelerator(nint message);

        void EnableModeless([MarshalAs(UnmanagedType.Bool)] bool enable);

        void UIActivate(uint state);

        void Refresh();

        void CreateViewWindow(nint previous, nint settings, nint browser, nint rect, out nint hwnd);

        void DestroyViewWindow();

        void GetCurrentInfo(nint settings);

        void AddPropertySheetPages(uint reserved, nint callback, nint lParam);

        void SaveViewState();

        void SelectItem(nint pidl, uint flags);

        [PreserveSig]
        int GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.IDispatch)] out object? result);
    }

    /// <summary>Carries IID_IDispatch.</summary>
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchMarker;
}
