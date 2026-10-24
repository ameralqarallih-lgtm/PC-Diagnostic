using System.Globalization;
using PcDiag.Platform.Windows.Gpu;

namespace PcDiag.Collectors.Gpu;

/// <summary>GPU engine categories as reported in PDH "engtype_" suffixes.</summary>
public enum GpuEngineKind
{
    /// <summary>Anything else (VideoProcessing, Security, Legacy Overlay...). Counts toward overall utilization.</summary>
    Other = 0,

    /// <summary>3D / graphics.</summary>
    ThreeD = 1,

    /// <summary>Compute.</summary>
    Compute = 2,

    /// <summary>Video decode.</summary>
    VideoDecode = 3,

    /// <summary>Video encode.</summary>
    VideoEncode = 4,

    /// <summary>Copy.</summary>
    Copy = 5,
}

/// <summary>
/// Parses PDH GPU instance names without allocating, e.g.
/// <c>pid_1234_luid_0x00000000_0x0001C5A5_phys_0_eng_3_engtype_3D</c> (GPU Engine),
/// <c>luid_0x00000000_0x0001C5A5_phys_0</c> (GPU Adapter Memory),
/// <c>pid_1234_luid_0x00000000_0x0001C5A5_phys_0</c> (GPU Process Memory).
/// </summary>
public static class GpuInstanceNames
{
    /// <summary>Parses a GPU Engine instance.</summary>
    public static bool TryParseEngine(ReadOnlySpan<char> name, out int pid, out AdapterLuid luid, out int engine, out ReadOnlySpan<char> engineType)
    {
        engine = 0;
        engineType = default;
        luid = default;
        if (!TryParsePidPrefix(ref name, out pid) || !TryParseLuidAndPhys(ref name, out luid, out var phys))
        {
            return false;
        }

        if (!TryTake(ref name, "_eng_") || !TryReadInt(ref name, out var eng) || !TryTake(ref name, "_engtype_"))
        {
            return false;
        }

        // Engine identity must include the physical adapter for linked (multi-GPU) adapters.
        engine = (phys << 16) | eng;
        engineType = name;
        return true;
    }

    /// <summary>Parses a GPU Adapter Memory instance.</summary>
    public static bool TryParseAdapter(ReadOnlySpan<char> name, out AdapterLuid luid) =>
        TryParseLuidAndPhys(ref name, out luid, out _) && name.IsEmpty;

    /// <summary>Parses a GPU Process Memory instance.</summary>
    public static bool TryParseProcessMemory(ReadOnlySpan<char> name, out int pid, out AdapterLuid luid)
    {
        luid = default;
        return TryParsePidPrefix(ref name, out pid) && TryParseLuidAndPhys(ref name, out luid, out _) && name.IsEmpty;
    }

    /// <summary>Classifies an engine type suffix such as "3D", "Compute_0", "VideoDecode", "Video Codec".</summary>
    public static GpuEngineKind Classify(ReadOnlySpan<char> engineType)
    {
        if (engineType.StartsWith("3D", StringComparison.OrdinalIgnoreCase) || engineType.StartsWith("Graphics", StringComparison.OrdinalIgnoreCase))
        {
            return GpuEngineKind.ThreeD;
        }

        if (engineType.StartsWith("Compute", StringComparison.OrdinalIgnoreCase))
        {
            return GpuEngineKind.Compute;
        }

        if (engineType.Contains("Decode", StringComparison.OrdinalIgnoreCase))
        {
            return GpuEngineKind.VideoDecode;
        }

        if (engineType.Contains("Encode", StringComparison.OrdinalIgnoreCase))
        {
            return GpuEngineKind.VideoEncode;
        }

        return engineType.StartsWith("Copy", StringComparison.OrdinalIgnoreCase) ? GpuEngineKind.Copy : GpuEngineKind.Other;
    }

    private static bool TryParsePidPrefix(ref ReadOnlySpan<char> name, out int pid)
    {
        pid = 0;
        if (!TryTake(ref name, "pid_") || !TryReadInt(ref name, out pid))
        {
            return false;
        }

        if (!name.StartsWith("_", StringComparison.Ordinal))
        {
            return false;
        }

        name = name[1..];
        return true;
    }

    private static bool TryParseLuidAndPhys(ref ReadOnlySpan<char> name, out AdapterLuid luid, out int phys)
    {
        luid = default;
        phys = 0;
        if (!TryTake(ref name, "luid_0x") || !TryReadHex(ref name, out var high)
            || !TryTake(ref name, "_0x") || !TryReadHex(ref name, out var low)
            || !TryTake(ref name, "_phys_") || !TryReadInt(ref name, out phys))
        {
            return false;
        }

        luid = new AdapterLuid(unchecked((int)high), low);
        return true;
    }

    private static bool TryTake(ref ReadOnlySpan<char> name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        name = name[prefix.Length..];
        return true;
    }

    private static bool TryReadInt(ref ReadOnlySpan<char> name, out int value)
    {
        var length = 0;
        while (length < name.Length && char.IsAsciiDigit(name[length]))
        {
            length++;
        }

        if (length == 0 || !int.TryParse(name[..length], NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }

        name = name[length..];
        return true;
    }

    private static bool TryReadHex(ref ReadOnlySpan<char> name, out uint value)
    {
        var length = 0;
        while (length < name.Length && char.IsAsciiHexDigit(name[length]))
        {
            length++;
        }

        if (length is 0 or > 8 || !uint.TryParse(name[..length], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }

        name = name[length..];
        return true;
    }
}
