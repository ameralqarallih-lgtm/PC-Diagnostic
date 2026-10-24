using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Power;

/// <summary>A power plan (power scheme).</summary>
public sealed record PowerScheme(Guid Id, string Name, bool Active);

/// <summary>Power plans (powrprof), and whether the PC has a battery.</summary>
public static unsafe partial class PowerTuning
{
    private const uint AccessScheme = 16;

    /// <summary>The power plans with the active one marked.</summary>
    public static IReadOnlyList<PowerScheme> Schemes()
    {
        Guid? active = null;
        if (PowerGetActiveScheme(0, out var activePointer) == 0 && activePointer != 0)
        {
            active = *(Guid*)activePointer;
            _ = LocalFree(activePointer);
        }

        var schemes = new List<PowerScheme>();
        for (uint index = 0; index < 64; index++)
        {
            Guid guid;
            var size = (uint)sizeof(Guid);
            if (PowerEnumerate(0, null, null, AccessScheme, index, (byte*)&guid, ref size) != 0)
            {
                break;
            }

            schemes.Add(new PowerScheme(guid, FriendlyName(guid) ?? guid.ToString(), guid == active));
        }

        return schemes;
    }

    /// <summary>Makes a plan the active one.</summary>
    public static bool Activate(Guid scheme) => PowerSetActiveScheme(0, &scheme) == 0;

    /// <summary>Whether the PC has a system battery (a laptop or tablet).</summary>
    public static bool HasBattery()
    {
        SystemPowerStatus status;
        return GetSystemPowerStatus(&status) && status.BatteryFlag != 128 && status.BatteryFlag != 255;
    }

    private static string? FriendlyName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(0, &scheme, null, null, null, ref size) != 0 || size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        fixed (byte* data = buffer)
        {
            return PowerReadFriendlyName(0, &scheme, null, null, data, ref size) == 0 ? new string((char*)data).TrimEnd('\0') : null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint rootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(nint rootPowerKey, Guid* schemeGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(nint rootPowerKey, Guid* schemeGuid, Guid* subGroupGuid, uint accessFlags, uint index, byte* buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(nint rootPowerKey, Guid* schemeGuid, Guid* subGroupGuid, Guid* powerSettingGuid, byte* buffer, ref uint bufferSize);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(SystemPowerStatus* status);
}
