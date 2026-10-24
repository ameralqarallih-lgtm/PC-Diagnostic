using System.Buffers.Binary;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>
/// Compact per-logical-processor encodings for CpuMetrics:
/// per_core_util = one byte per core (percent 0–100, 255 = unavailable);
/// per_core_mhz = little-endian uint16 per core (0 = unavailable).
/// </summary>
public static class CoreBlobs
{
    /// <summary>Byte value meaning "unavailable" in per_core_util.</summary>
    public const byte UnavailableUtilization = 255;

    /// <summary>Encodes per-core utilization.</summary>
    public static byte[] PackUtilization(IReadOnlyList<CpuCoreSample> cores)
    {
        ArgumentNullException.ThrowIfNull(cores);
        var blob = new byte[cores.Count];
        for (var i = 0; i < cores.Count; i++)
        {
            var u = cores[i].UtilizationPercent;
            blob[i] = u.HasValue ? (byte)Math.Round(Math.Clamp(u.Value!.Value, 0, 100)) : UnavailableUtilization;
        }

        return blob;
    }

    /// <summary>Encodes per-core effective frequency.</summary>
    public static byte[] PackFrequency(IReadOnlyList<CpuCoreSample> cores)
    {
        ArgumentNullException.ThrowIfNull(cores);
        var blob = new byte[cores.Count * 2];
        for (var i = 0; i < cores.Count; i++)
        {
            var f = cores[i].FrequencyMhz;
            var mhz = f.HasValue ? (ushort)Math.Clamp(Math.Round(f.Value!.Value), 1, ushort.MaxValue) : (ushort)0;
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(i * 2), mhz);
        }

        return blob;
    }

    /// <summary>Decodes per_core_util; null entries are unavailable.</summary>
    public static double?[] UnpackUtilization(ReadOnlySpan<byte> blob)
    {
        var result = new double?[blob.Length];
        for (var i = 0; i < blob.Length; i++)
        {
            result[i] = blob[i] == UnavailableUtilization ? null : blob[i];
        }

        return result;
    }

    /// <summary>Decodes per_core_mhz; null entries are unavailable.</summary>
    public static double?[] UnpackFrequency(ReadOnlySpan<byte> blob)
    {
        var result = new double?[blob.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            var mhz = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(i * 2));
            result[i] = mhz == 0 ? null : mhz;
        }

        return result;
    }
}
