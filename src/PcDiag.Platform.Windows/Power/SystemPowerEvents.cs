using System.Runtime.InteropServices;
using Microsoft.Win32;
using PcDiag.Core.Abstractions;

namespace PcDiag.Platform.Windows.Power;

/// <summary>
/// Sleep/wake and end-of-session notifications through <see cref="SystemEvents"/>, which runs its own hidden
/// window and message loop on a dedicated thread in a non-UI process. Not raised in a Windows service (session 0);
/// the Agent runs in the user's session. Handlers run on that thread.
/// </summary>
public sealed class SystemPowerEvents : ISystemPowerEvents, IDisposable
{
    private readonly bool _subscribed;

    /// <summary>Subscribes to the system notifications.</summary>
    public SystemPowerEvents()
    {
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnded += OnSessionEnded;
            _subscribed = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            // "System event notifications are not supported under the current context" (e.g. a service).
            UnavailableReason = ex.Message;
        }
    }

    /// <inheritdoc />
    public event EventHandler? Suspending;

    /// <inheritdoc />
    public event EventHandler? Resumed;

    /// <inheritdoc />
    public event EventHandler? SessionEnding;

    /// <summary>Why notifications are unavailable; null when they work.</summary>
    public string? UnavailableReason { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_subscribed)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnded -= OnSessionEnded;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Suspending?.Invoke(this, EventArgs.Empty);
                break;
            case PowerModes.Resume:
                Resumed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    // SessionEnded (WM_ENDSESSION with TRUE) is final; SessionEnding can still be cancelled by another application.
    private void OnSessionEnded(object sender, SessionEndedEventArgs e) => SessionEnding?.Invoke(this, EventArgs.Empty);
}
