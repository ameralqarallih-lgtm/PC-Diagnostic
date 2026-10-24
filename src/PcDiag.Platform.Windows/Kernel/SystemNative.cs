using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Kernel;

/// <summary>
/// kernel32 and powrprof declarations used for memory and processor information.
/// References: GlobalMemoryStatusEx, GetPerformanceInfo (K32GetPerformanceInfo), CallNtPowerInformation.
/// </summary>
internal static partial class SystemNative
{
    /// <summary>POWER_INFORMATION_LEVEL.ProcessorInformation.</summary>
    public const int ProcessorInformation = 11;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPerformanceInfo(out PerformanceInformation information, uint size);

    [LibraryImport("powrprof.dll")]
    public static unsafe partial int CallNtPowerInformation(int informationLevel, void* inputBuffer, uint inputBufferLength, void* outputBuffer, uint outputBufferLength);
}

/// <summary>MEMORYSTATUSEX.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhys;
    public ulong AvailPhys;
    public ulong TotalPageFile;
    public ulong AvailPageFile;
    public ulong TotalVirtual;
    public ulong AvailVirtual;
    public ulong AvailExtendedVirtual;
}

/// <summary>PERFORMANCE_INFORMATION. Sizes are in pages; multiply by PageSize.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PerformanceInformation
{
    public uint Size;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}

/// <summary>PROCESSOR_POWER_INFORMATION.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessorPowerInformation
{
    public uint Number;
    public uint MaxMhz;
    public uint CurrentMhz;
    public uint MhzLimit;
    public uint MaxIdleState;
    public uint CurrentIdleState;
}
