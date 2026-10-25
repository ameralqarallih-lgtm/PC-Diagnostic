using PcDiag.Platform.Windows.PawnIo;

namespace PcDiag.Platform.Windows.Gpu;

/// <summary>
/// The hotspot of an NVIDIA GeForce RTX 50 series (Blackwell, GB202–GB207) GPU, from the GPU's own thermal registers.
/// NVIDIA left it out of NVAPI and NVML for these cards; the PawnIO module Nvidia.bin (PawnIO.Modules 0.2.11, by
/// CapFrameX and Blacktempel, LGPL-2.1; shipped unmodified as pawnio\Nvidia.bin) reads six channels at BAR0 + 0xAD0A90,
/// read-only, after checking the vendor, the display class and that the GPU answers. Bit 30 marks a valid channel, the
/// low 16 bits are 1/256 °C; the hotspot is the highest valid channel (channel 0 is the one HWMonitor 1.65.1 shows,
/// LibreHardwareMonitor's next release takes the highest). Other GPUs are refused by the module (not supported).
/// The PCI bus is shared with other monitoring tools through the Global\Access_PCI mutex they use.
/// </summary>
public sealed class NvidiaThermalRegisters : IDisposable
{
    /// <summary>Thermal channels the module reads.</summary>
    public const int ChannelCount = 6;

    private const long ChannelValid = 1L << 30;
    private const string ReadFunction = "ioctl_read_thermal_registers";

    private readonly PawnIoModule _module;
    private readonly Mutex? _pciBus;

    private NvidiaThermalRegisters(PawnIoModule module)
    {
        _module = module;
        try
        {
            _pciBus = new Mutex(false, @"Global\Access_PCI");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            _pciBus = Mutex.TryOpenExisting(@"Global\Access_PCI", out var existing) ? existing : null;
        }
    }

    /// <summary>The module file next to the program.</summary>
    public static string ModulePath => Path.Combine(AppContext.BaseDirectory, "pawnio", "Nvidia.bin");

    /// <summary>Loads the module, or returns null with the reason (no PawnIO, not elevated, no module file).</summary>
    public static NvidiaThermalRegisters? TryOpen(out string reason) =>
        PawnIoModule.TryLoad(ModulePath, out reason) is { } module ? new NvidiaThermalRegisters(module) : null;

    /// <summary>
    /// The hotspot of the GPU at this PCI address in °C; null when the GPU is not a supported one, is asleep or has no
    /// valid channel (<paramref name="error"/> is the Windows error when the module refused: 50 = not a supported GPU).
    /// </summary>
    public double? TryReadHotspot(uint bus, uint device, uint function, out int error)
    {
        Span<long> raw = stackalloc long[ChannelCount];
        var taken = false;
        try
        {
            // Another tool is mid-access when the bus stays taken: this sample goes without.
            try
            {
                taken = _pciBus?.WaitOne(TimeSpan.FromMilliseconds(50)) ?? true;
            }
            catch (AbandonedMutexException)
            {
                taken = true;
            }

            if (!taken)
            {
                error = 0;
                return null;
            }

            var count = _module.Execute(ReadFunction, [bus, device, function], raw, out error);
            return count == ChannelCount ? Hotspot(raw) : null;
        }
        finally
        {
            if (taken)
            {
                _pciBus?.ReleaseMutex();
            }
        }
    }

    /// <summary>The highest valid channel in °C, or null when none is valid.</summary>
    public static double? Hotspot(ReadOnlySpan<long> raw)
    {
        double? highest = null;
        foreach (var value in raw)
        {
            if ((value & ChannelValid) != 0 && (value & 0xFFFF) / 256.0 is var celsius and > 0 and < 150)
            {
                highest = Math.Max(highest ?? celsius, celsius);
            }
        }

        return highest;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _module.Dispose();
        _pciBus?.Dispose();
    }
}
