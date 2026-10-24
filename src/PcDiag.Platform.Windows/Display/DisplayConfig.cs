using System.Runtime.InteropServices;
using Microsoft.Win32;
using PcDiag.Platform.Windows.Gpu;

namespace PcDiag.Platform.Windows.Display;

/// <summary>Advanced color (HDR / wide color gamut) state of one display.</summary>
/// <param name="Supported">Some kind of advanced color is supported.</param>
/// <param name="Active">Advanced color is active (HDR or WCG).</param>
/// <param name="HdrSupported">HDR is supported (Windows 11 24H2+ only; null on older builds).</param>
/// <param name="HdrActive">HDR is the active color mode (on older builds: advanced color enabled and wide color not enforced).</param>
/// <param name="Mode">"SDR", "WCG" or "HDR" (Windows 11 24H2+), otherwise null.</param>
/// <param name="BitsPerColorChannel">Bits per color channel of the signal.</param>
/// <param name="LimitedByPolicy">System policy limits advanced color (e.g. on battery), or forces it off.</param>
public sealed record DisplayColorInfo(bool Supported, bool Active, bool? HdrSupported, bool HdrActive, string? Mode, int BitsPerColorChannel, bool LimitedByPolicy);

/// <summary>One active display path (source → monitor) from QueryDisplayConfig plus DisplayConfigGetDeviceInfo.</summary>
/// <param name="Adapter">LUID of the adapter that drives the monitor (changes across boots).</param>
/// <param name="TargetId">Target (output) ID on that adapter.</param>
/// <param name="GdiDeviceName">Source name such as <c>\\.\DISPLAY1</c>.</param>
/// <param name="FriendlyName">Monitor name (usually from EDID).</param>
/// <param name="MonitorDevicePath">Monitor device interface path (same form as device-arrival notifications).</param>
/// <param name="Manufacturer">EDID manufacturer (PnP) ID, e.g. "DEL", when the EDID IDs are valid.</param>
/// <param name="ProductCode">EDID product code as four hex digits, when valid.</param>
/// <param name="ConnectorInstance">Instance number of this connector type on the adapter.</param>
/// <param name="OutputTechnology">DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY value.</param>
/// <param name="Rotation">DISPLAYCONFIG_ROTATION value (1 = none … 4 = 270°).</param>
/// <param name="RefreshRateHz">Vertical refresh of the signal (target mode), falling back to the path's rate.</param>
/// <param name="Width">Desktop width of the source, in pixels.</param>
/// <param name="Height">Desktop height of the source, in pixels.</param>
/// <param name="PositionX">Desktop position X (the primary display is at 0,0).</param>
/// <param name="PositionY">Desktop position Y.</param>
/// <param name="Color">Advanced color state, when it could be read.</param>
/// <param name="AdapterDevicePath">Device path of the adapter (identifies adapters DXGI does not list, e.g. remote display).</param>
public sealed record DisplayPathData(
    AdapterLuid Adapter,
    uint TargetId,
    string? GdiDeviceName,
    string? FriendlyName,
    string? MonitorDevicePath,
    string? Manufacturer,
    string? ProductCode,
    uint ConnectorInstance,
    uint OutputTechnology,
    uint Rotation,
    double? RefreshRateHz,
    int Width,
    int Height,
    int PositionX,
    int PositionY,
    DisplayColorInfo? Color,
    string? AdapterDevicePath)
{
    /// <summary>Whether this is the primary display (its desktop starts at 0,0).</summary>
    public bool IsPrimary => PositionX == 0 && PositionY == 0;
}

/// <summary>The active display configuration, or the Win32 error that prevented reading it.</summary>
public sealed record DisplayConfigResult(IReadOnlyList<DisplayPathData> Paths, int Error);

/// <summary>
/// Reads the active display configuration (<c>QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)</c>) and per-target details
/// (<c>DisplayConfigGetDeviceInfo</c>). Struct layouts follow wingdi.h (Windows SDK 10.0.26100) and are checked by
/// tests. Works without administrator rights but needs the interactive session (session 0 gets access denied).
/// </summary>
public static unsafe partial class DisplayConfig
{
    /// <summary>ERROR_SUCCESS.</summary>
    public const int Success = 0;

    /// <summary>ERROR_ACCESS_DENIED (not in the interactive session).</summary>
    public const int AccessDenied = 5;

    internal const uint QdcOnlyActivePaths = 0x2;
    internal const uint PathActive = 0x1;
    internal const uint ModeInfoTypeSource = 1;
    internal const uint ModeInfoTypeTarget = 2;
    internal const uint ModeIndexInvalid = 0xFFFFFFFF;
    internal const uint GetSourceName = 1;
    internal const uint GetTargetName = 2;
    internal const uint GetAdapterName = 4;
    internal const uint GetAdvancedColorInfo = 9;
    internal const uint GetAdvancedColorInfo2 = 15;

    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Reads the active configuration. Retries when it changes between sizing and reading.</summary>
    public static DisplayConfigResult Query()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var error = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
            if (error != Success)
            {
                return new DisplayConfigResult([], error);
            }

            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            fixed (PathInfo* p = paths)
            fixed (ModeInfo* m = modes)
            {
                error = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, p, ref modeCount, m, nint.Zero);
            }

            if (error == ErrorInsufficientBuffer)
            {
                continue; // a display was added between the two calls
            }

            if (error != Success)
            {
                return new DisplayConfigResult([], error);
            }

            var result = new List<DisplayPathData>((int)pathCount);
            for (var i = 0; i < pathCount; i++)
            {
                if ((paths[i].Flags & PathActive) != 0)
                {
                    result.Add(Describe(paths[i], modes.AsSpan(0, (int)modeCount)));
                }
            }

            return new DisplayConfigResult(result, Success);
        }

        return new DisplayConfigResult([], ErrorInsufficientBuffer);
    }

    /// <summary>
    /// Decodes the EDID manufacturer ID as DisplayConfigGetDeviceInfo returns it (the two EDID bytes in their
    /// original big-endian order, read as a little-endian UINT16): three letters of 5 bits each, 1 = 'A'.
    /// </summary>
    public static string? DecodeManufacturer(ushort raw)
    {
        var value = (ushort)((raw >> 8) | (raw << 8));
        Span<char> letters = [(char)('@' + ((value >> 10) & 0x1F)), (char)('@' + ((value >> 5) & 0x1F)), (char)('@' + (value & 0x1F))];
        foreach (var c in letters)
        {
            if (c is < 'A' or > 'Z')
            {
                return null;
            }
        }

        return new string(letters);
    }

    /// <summary>
    /// The Windows "variable refresh rate" graphics setting (<c>VRROptimizeEnable</c> in
    /// HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings). This is the user setting, not
    /// whether VRR is active on a display. Null when the setting is not present.
    /// </summary>
    public static bool? ReadVariableRefreshSetting()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        return ParseVariableRefreshSetting(key?.GetValue("DirectXUserGlobalSettings") as string);
    }

    /// <summary>Parses "SwapEffectUpgradeEnable=0;VRROptimizeEnable=1;" style settings.</summary>
    public static bool? ParseVariableRefreshSetting(string? settings)
    {
        foreach (var part in (settings ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && part[..separator].Equals("VRROptimizeEnable", StringComparison.OrdinalIgnoreCase))
            {
                return part[(separator + 1)..].Trim() switch
                {
                    "1" => true,
                    "0" => false,
                    _ => null,
                };
            }
        }

        return null;
    }

    private static DisplayPathData Describe(in PathInfo path, ReadOnlySpan<ModeInfo> modes)
    {
        var adapter = path.Target.AdapterId;
        var luid = new AdapterLuid(adapter.HighPart, adapter.LowPart);

        // Source mode: desktop size and position. Target mode: the signal's vertical refresh.
        int width = 0, height = 0, x = 0, y = 0;
        if (path.Source.ModeInfoIdx != ModeIndexInvalid && path.Source.ModeInfoIdx < modes.Length && modes[(int)path.Source.ModeInfoIdx].InfoType == ModeInfoTypeSource)
        {
            ref readonly var source = ref modes[(int)path.Source.ModeInfoIdx];
            (width, height, x, y) = ((int)source.SourceWidth, (int)source.SourceHeight, source.PositionX, source.PositionY);
        }

        double? refresh = null;
        if (path.Target.ModeInfoIdx != ModeIndexInvalid && path.Target.ModeInfoIdx < modes.Length && modes[(int)path.Target.ModeInfoIdx].InfoType == ModeInfoTypeTarget)
        {
            refresh = Hz(modes[(int)path.Target.ModeInfoIdx].VSyncFreq);
        }

        refresh ??= Hz(path.Target.RefreshRate);

        string? friendly = null, devicePath = null, manufacturer = null, product = null;
        uint connector = 0;
        var target = new TargetDeviceName { Header = Header(GetTargetName, sizeof(TargetDeviceName), adapter, path.Target.Id) };
        if (DisplayConfigGetDeviceInfo(&target.Header) == Success)
        {
            friendly = Text(target.MonitorFriendlyDeviceName, 64);
            devicePath = Text(target.MonitorDevicePath, 128);
            connector = target.ConnectorInstance;
            if ((target.Flags & 0x4) != 0) // edidIdsValid
            {
                manufacturer = DecodeManufacturer(target.EdidManufactureId);
                product = target.EdidProductCodeId.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        string? gdiName = null;
        var sourceName = new SourceDeviceName { Header = Header(GetSourceName, sizeof(SourceDeviceName), path.Source.AdapterId, path.Source.Id) };
        if (DisplayConfigGetDeviceInfo(&sourceName.Header) == Success)
        {
            gdiName = Text(sourceName.ViewGdiDeviceName, 32);
        }

        string? adapterPath = null;
        var adapterName = new AdapterName { Header = Header(GetAdapterName, sizeof(AdapterName), adapter, 0) };
        if (DisplayConfigGetDeviceInfo(&adapterName.Header) == Success)
        {
            adapterPath = Text(adapterName.AdapterDevicePath, 128);
        }

        return new DisplayPathData(luid, path.Target.Id, gdiName, friendly, devicePath, manufacturer, product, connector,
            path.Target.OutputTechnology, path.Target.Rotation, refresh, width, height, x, y, ReadColor(adapter, path.Target.Id), adapterPath);
    }

    private static DisplayColorInfo? ReadColor(Luid adapter, uint targetId)
    {
        // Windows 11 24H2+: separate HDR and wide-color state and the active color mode.
        var info2 = new AdvancedColorInfo2 { Header = Header(GetAdvancedColorInfo2, sizeof(AdvancedColorInfo2), adapter, targetId) };
        if (DisplayConfigGetDeviceInfo(&info2.Header) == Success)
        {
            var v = info2.Value;
            var mode = info2.ActiveColorMode switch { 0 => "SDR", 1 => "WCG", 2 => "HDR", _ => null };
            return new DisplayColorInfo(
                Supported: (v & 0x1) != 0,
                Active: (v & 0x2) != 0,
                HdrSupported: (v & 0x10) != 0,
                HdrActive: info2.ActiveColorMode == 2,
                Mode: mode,
                BitsPerColorChannel: (int)info2.BitsPerColorChannel,
                LimitedByPolicy: (v & 0x8) != 0);
        }

        var info = new AdvancedColorInfo { Header = Header(GetAdvancedColorInfo, sizeof(AdvancedColorInfo), adapter, targetId) };
        if (DisplayConfigGetDeviceInfo(&info.Header) == Success)
        {
            var v = info.Value;
            var enabled = (v & 0x2) != 0;
            var wideColorEnforced = (v & 0x4) != 0;
            return new DisplayColorInfo(
                Supported: (v & 0x1) != 0,
                Active: enabled,
                HdrSupported: null,
                HdrActive: enabled && !wideColorEnforced,
                Mode: null,
                BitsPerColorChannel: (int)info.BitsPerColorChannel,
                LimitedByPolicy: (v & 0x8) != 0);
        }

        return null;
    }

    private static double? Hz(Rational rate) =>
        rate.Denominator == 0 || rate.Numerator == 0 ? null : Math.Round((double)rate.Numerator / rate.Denominator, 3);

    private static DeviceInfoHeader Header(uint type, int size, Luid adapter, uint id) =>
        new() { Type = type, Size = (uint)size, AdapterId = adapter, Id = id };

    private static string? Text(char* buffer, int length)
    {
        var span = new ReadOnlySpan<char>(buffer, length);
        var end = span.IndexOf('\0');
        var text = (end < 0 ? span : span[..end]).ToString();
        return text.Length == 0 ? null : text;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, PathInfo* pathArray, ref uint numModeInfoArrayElements, ModeInfo* modeInfoArray, nint currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(DeviceInfoHeader* requestPacket);

    // ---- wingdi.h structures -------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    /// <summary>DISPLAYCONFIG_PATH_SOURCE_INFO (20 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    /// <summary>DISPLAYCONFIG_PATH_TARGET_INFO (48 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    /// <summary>DISPLAYCONFIG_PATH_INFO (72 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo Source;
        public PathTargetInfo Target;
        public uint Flags;
    }

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO (64 bytes): the union starts at offset 16 because DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    /// begins with a UINT64. Target mode = video signal info; source mode = width, height, pixel format, position.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct ModeInfo
    {
        [FieldOffset(0)]
        public uint InfoType;

        [FieldOffset(4)]
        public uint Id;

        [FieldOffset(8)]
        public Luid AdapterId;

        [FieldOffset(16)]
        public ulong PixelRate;

        [FieldOffset(24)]
        public Rational HSyncFreq;

        [FieldOffset(32)]
        public Rational VSyncFreq;

        [FieldOffset(40)]
        public uint ActiveWidth;

        [FieldOffset(44)]
        public uint ActiveHeight;

        [FieldOffset(16)]
        public uint SourceWidth;

        [FieldOffset(20)]
        public uint SourceHeight;

        [FieldOffset(24)]
        public uint PixelFormat;

        [FieldOffset(28)]
        public int PositionX;

        [FieldOffset(32)]
        public int PositionY;
    }

    /// <summary>DISPLAYCONFIG_DEVICE_INFO_HEADER (20 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    /// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME (420 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    /// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME (84 bytes; CCHDEVICENAME = 32).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }

    /// <summary>DISPLAYCONFIG_ADAPTER_NAME (276 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdapterName
    {
        public DeviceInfoHeader Header;
        public fixed char AdapterDevicePath[128];
    }

    /// <summary>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO (32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    /// <summary>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 (36 bytes; Windows 11 24H2+).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdvancedColorInfo2
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
        public uint ActiveColorMode;
    }
}
