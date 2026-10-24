using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Kernel;

/// <summary>Physical memory and commit figures from GlobalMemoryStatusEx and GetPerformanceInfo, in bytes.</summary>
public sealed record MemoryInfo(
    long TotalBytes,
    long AvailableBytes,
    long CommittedBytes,
    long CommitLimitBytes,
    long SystemCacheBytes,
    long PagedPoolBytes,
    long NonPagedPoolBytes,
    int HandleCount,
    int ProcessCount,
    int ThreadCount);

/// <summary>Safe wrappers over the memory and processor APIs.</summary>
public static class SystemInfo
{
    /// <summary>Reads memory figures. Returns null if either API fails (never throws).</summary>
    public static MemoryInfo? TryGetMemoryInfo()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!SystemNative.GlobalMemoryStatusEx(ref status))
        {
            return null;
        }

        if (!SystemNative.GetPerformanceInfo(out var perf, (uint)Marshal.SizeOf<PerformanceInformation>()))
        {
            return null;
        }

        var page = (long)perf.PageSize;
        return new MemoryInfo(
            TotalBytes: (long)status.TotalPhys,
            AvailableBytes: (long)status.AvailPhys,
            CommittedBytes: (long)perf.CommitTotal * page,
            CommitLimitBytes: (long)perf.CommitLimit * page,
            SystemCacheBytes: (long)perf.SystemCache * page,
            PagedPoolBytes: (long)perf.KernelPaged * page,
            NonPagedPoolBytes: (long)perf.KernelNonpaged * page,
            HandleCount: (int)perf.HandleCount,
            ProcessCount: (int)perf.ProcessCount,
            ThreadCount: (int)perf.ThreadCount);
    }

    /// <summary>
    /// The highest rated processor frequency in MHz (CallNtPowerInformation, ProcessorInformation), or null.
    /// Covers the processors in the calling thread's processor group.
    /// </summary>
    public static unsafe double? TryGetMaxProcessorMhz()
    {
        var count = Environment.ProcessorCount;
        var buffer = new ProcessorPowerInformation[count];
        var size = (uint)(Marshal.SizeOf<ProcessorPowerInformation>() * count);

        int status;
        fixed (ProcessorPowerInformation* output = buffer)
        {
            status = SystemNative.CallNtPowerInformation(SystemNative.ProcessorInformation, null, 0, output, size);
        }

        if (status != 0)
        {
            return null;
        }

        var max = buffer.Max(p => p.MaxMhz);
        return max > 0 ? max : null;
    }
}
