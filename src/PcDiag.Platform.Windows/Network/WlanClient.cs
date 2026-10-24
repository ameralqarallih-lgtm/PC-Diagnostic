using System.Runtime.InteropServices;
using System.Text;

namespace PcDiag.Platform.Windows.Network;

/// <summary>A wireless interface known to the WLAN service.</summary>
public sealed record WlanInterfaceInfo(Guid InterfaceGuid, string Description, bool IsConnected);

/// <summary>Result of a WLAN query: a value, or the Win32 error that prevented it.</summary>
public readonly record struct WlanResult<T>(T? Value, uint Error)
    where T : struct
{
    /// <summary>ERROR_ACCESS_DENIED: on Windows 11 24H2+ usually the location privacy setting.</summary>
    public bool AccessDenied => Error == 5;
}

/// <summary>
/// Native Wi-Fi (wlanapi.dll). Signal strength comes from <c>wlan_intf_opcode_rssi</c>, which is not subject to the
/// Windows 11 24H2 location-permission restriction. <c>wlan_intf_opcode_current_connection</c> (SSID, signal quality)
/// IS restricted and can show a system location prompt, so it is only used when the user opts in.
/// See https://learn.microsoft.com/windows/win32/nativewifi/wi-fi-access-location-changes.
/// </summary>
public sealed partial class WlanClient : IDisposable
{
    private const uint ClientVersion = 2;
    private const int OpcodeCurrentConnection = 7;          // wlan_intf_opcode_current_connection
    private const int OpcodeRssi = 0x10000102;              // wlan_intf_opcode_rssi
    private const int InterfaceStateConnected = 1;          // wlan_interface_state_connected
    private const int InterfaceInfoSize = 16 + (256 * 2) + 4; // GUID + WCHAR[256] + state

    private nint _handle;

    private WlanClient(nint handle) => _handle = handle;

    /// <summary>Opens a WLAN session. Returns null (with the error) when the WLAN service is not available.</summary>
    public static WlanClient? TryOpen(out uint error)
    {
        try
        {
            error = WlanOpenHandle(ClientVersion, 0, out _, out var handle);
            return error == 0 ? new WlanClient(handle) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = 1157; // ERROR_DLL_NOT_FOUND
            return null;
        }
    }

    /// <summary>Lists wireless interfaces.</summary>
    public unsafe IReadOnlyList<WlanInterfaceInfo> EnumerateInterfaces()
    {
        if (WlanEnumInterfaces(_handle, 0, out var list) != 0)
        {
            return [];
        }

        try
        {
            // WLAN_INTERFACE_INFO_LIST: DWORD count, DWORD index, then WLAN_INTERFACE_INFO[count].
            var count = *(uint*)list;
            var result = new List<WlanInterfaceInfo>((int)count);
            for (var i = 0; i < count; i++)
            {
                var item = (byte*)list + 8 + (i * InterfaceInfoSize);
                var guid = *(Guid*)item;
                var description = new string((char*)(item + 16)).TrimEnd('\0');
                var state = *(int*)(item + 16 + 512);
                result.Add(new WlanInterfaceInfo(guid, description, state == InterfaceStateConnected));
            }

            return result;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>Received signal strength in dBm (not location-restricted).</summary>
    public unsafe WlanResult<int> QueryRssi(Guid interfaceGuid)
    {
        var error = WlanQueryInterface(_handle, interfaceGuid, OpcodeRssi, 0, out var size, out var data, 0);
        if (error != 0)
        {
            return new WlanResult<int>(null, error);
        }

        try
        {
            return size >= 4 ? new WlanResult<int>(*(int*)data, 0) : new WlanResult<int>(null, 13); // ERROR_INVALID_DATA
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    /// <summary>
    /// SSID of the current connection. Location-restricted on Windows 11 24H2+: may return access denied and may
    /// cause Windows to show a one-time location prompt. Only call when the user opted in.
    /// </summary>
    public unsafe string? QueryConnectedSsid(Guid interfaceGuid, out uint error)
    {
        error = WlanQueryInterface(_handle, interfaceGuid, OpcodeCurrentConnection, 0, out var size, out var data, 0);
        if (error != 0)
        {
            return null;
        }

        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: isState(4), mode(4), strProfileName WCHAR[256] (512), then
            // WLAN_ASSOCIATION_ATTRIBUTES starting with DOT11_SSID { ULONG length; UCHAR ssid[32]; }.
            const int ssidOffset = 4 + 4 + 512;
            if (size < ssidOffset + 36)
            {
                return null;
            }

            var bytes = (byte*)data + ssidOffset;
            var length = Math.Min(*(uint*)bytes, 32u);
            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(bytes + 4, (int)length));
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    /// <summary>Converts RSSI to Windows' signal quality: −100 dBm = 0 %, −50 dBm = 100 %, linear in between.</summary>
    public static double SignalQualityFromRssi(int rssiDbm) => Math.Clamp((rssiDbm + 100) * 2.0, 0, 100);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle != 0)
        {
            _ = WlanCloseHandle(_handle, 0);
            _handle = 0;
        }
    }

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint clientHandle);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(nint clientHandle, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(nint clientHandle, nint reserved, out nint interfaceList);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanQueryInterface(nint clientHandle, in Guid interfaceGuid, int opCode, nint reserved, out uint dataSize, out nint data, nint opcodeValueType);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);
}
