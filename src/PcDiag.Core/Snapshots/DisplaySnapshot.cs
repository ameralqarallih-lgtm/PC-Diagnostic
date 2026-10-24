using PcDiag.Core.Telemetry;

namespace PcDiag.Core.Snapshots;

/// <summary>The active display configuration at one point in time.</summary>
/// <param name="Timestamp">When the configuration was read.</param>
/// <param name="Displays">Active displays.</param>
/// <param name="IsRemoteSession">
/// The recorder runs in a Remote Desktop session. Display configuration is per session, so the displays are then
/// the remote session's virtual display, not the physical monitors.
/// </param>
public sealed record DisplaySnapshot(DateTimeOffset Timestamp, IReadOnlyList<DisplayTarget> Displays, bool IsRemoteSession = false);

/// <summary>One active display path (a monitor driven by an adapter output).</summary>
/// <param name="TargetKey">
/// Stable key for the monitor: its PnP instance ID (e.g. <c>DISPLAY\AUS32F4\5&amp;12862c70&amp;0&amp;UID4353</c>), which
/// survives reboots, or adapter key plus target ID when Windows reports no monitor device.
/// </param>
/// <param name="FriendlyName">Monitor friendly name.</param>
/// <param name="Manufacturer">EDID manufacturer ID (e.g. "DEL"), when available.</param>
/// <param name="ProductCode">EDID product code, when available.</param>
/// <param name="WidthPixels">Active horizontal resolution.</param>
/// <param name="HeightPixels">Active vertical resolution.</param>
/// <param name="RefreshRateHz">Refresh rate in Hz.</param>
/// <param name="Connection">Physical connection type.</param>
/// <param name="HdrEnabled">Whether HDR / advanced color is enabled.</param>
/// <param name="HdrSupported">Whether the display supports HDR / advanced color.</param>
/// <param name="VariableRefreshSetting">The Windows variable refresh rate setting (not the live VRR state).</param>
/// <param name="OrientationDegrees">Rotation: 0, 90, 180 or 270.</param>
/// <param name="IsPrimary">Whether this is the primary display.</param>
/// <param name="PositionX">Desktop position X.</param>
/// <param name="PositionY">Desktop position Y.</param>
/// <param name="AdapterKey">The GPU adapter driving the display.</param>
/// <param name="AdapterName">The GPU adapter name.</param>
/// <param name="GdiDeviceName">The source's GDI name, e.g. <c>\\.\DISPLAY1</c>.</param>
/// <param name="ColorMode">Active color mode: "SDR", "WCG" or "HDR" (Windows 11 24H2+), otherwise null.</param>
/// <param name="BitsPerColorChannel">Bits per color channel of the signal.</param>
/// <param name="MonitorDevicePath">Monitor device interface path (matches monitor arrival/removal notifications).</param>
public sealed record DisplayTarget(
    string TargetKey,
    string FriendlyName,
    string? Manufacturer,
    string? ProductCode,
    int WidthPixels,
    int HeightPixels,
    double RefreshRateHz,
    DisplayConnection Connection,
    Reading<bool> HdrEnabled,
    Reading<bool> HdrSupported,
    Reading<bool> VariableRefreshSetting,
    int OrientationDegrees,
    bool IsPrimary,
    int PositionX,
    int PositionY,
    string AdapterKey,
    string AdapterName,
    string? GdiDeviceName = null,
    string? ColorMode = null,
    int? BitsPerColorChannel = null,
    string? MonitorDevicePath = null);

/// <summary>Physical display connection (from DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY).</summary>
public enum DisplayConnection
{
    /// <summary>Unknown or other.</summary>
    Other = 0,

    /// <summary>HDMI.</summary>
    Hdmi = 1,

    /// <summary>DisplayPort (external).</summary>
    DisplayPort = 2,

    /// <summary>Internal panel (eDP / LVDS).</summary>
    Internal = 3,

    /// <summary>DVI.</summary>
    Dvi = 4,

    /// <summary>VGA / analog.</summary>
    Vga = 5,

    /// <summary>USB-C / Thunderbolt DisplayPort alt mode (reported as DisplayPort embedded or USB tunnel).</summary>
    UsbC = 6,

    /// <summary>Wireless (Miracast).</summary>
    Wireless = 7,

    /// <summary>Indirect / virtual display (e.g. remote desktop, virtual display drivers).</summary>
    Indirect = 8,
}
