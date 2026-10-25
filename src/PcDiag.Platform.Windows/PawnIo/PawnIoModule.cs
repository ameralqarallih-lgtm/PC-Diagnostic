using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcDiag.Platform.Windows.PawnIo;

/// <summary>
/// One module loaded into the PawnIO driver (\Device\PawnIO) and its functions, called through the driver's device
/// interface: the same two control codes LibreHardwareMonitor uses. PawnIO runs only modules signed by its author, so a
/// module file is used exactly as released (PawnIO.Modules); each handle holds its own copy of the module. Needs
/// administrator rights and the PawnIO driver; <see cref="TryLoad"/> says why when either is missing.
/// </summary>
public sealed partial class PawnIoModule : IDisposable
{
    private const uint DeviceType = 41394u << 16;
    private const uint IoctlLoadBinary = DeviceType | (0x821 << 2);
    private const uint IoctlExecute = DeviceType | (0x841 << 2);
    private const int FunctionNameLength = 32;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle _handle;

    private PawnIoModule(SafeFileHandle handle) => _handle = handle;

    /// <summary>Loads the module in <paramref name="path"/>, or returns null with the reason.</summary>
    public static PawnIoModule? TryLoad(string path, out string reason)
    {
        if (!File.Exists(path))
        {
            reason = $"module {Path.GetFileName(path)} not found";
            return null;
        }

        var handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", GenericRead | GenericWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            reason = error == 5 ? "the PawnIO driver needs administrator rights" : $"the PawnIO driver is not available (error {error})";
            return null;
        }

        var module = File.ReadAllBytes(path);
        if (!Control(handle, IoctlLoadBinary, module, [], out _))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            reason = $"PawnIO did not load {Path.GetFileName(path)} (error {error})";
            return null;
        }

        reason = "loaded";
        return new PawnIoModule(handle);
    }

    /// <summary>
    /// Calls <paramref name="function"/> with <paramref name="input"/>; fills <paramref name="output"/> and returns how many
    /// values it wrote, or -1 when the function failed (<paramref name="error"/> is the Windows error, e.g. 50 for a
    /// device the module does not support).
    /// </summary>
    public int Execute(string function, ReadOnlySpan<long> input, Span<long> output, out int error)
    {
        var request = new byte[FunctionNameLength + (input.Length * sizeof(long))];
        Encoding.ASCII.GetBytes(function, 0, Math.Min(function.Length, FunctionNameLength - 1), request, 0);
        MemoryMarshal.AsBytes(input).CopyTo(request.AsSpan(FunctionNameLength));

        var answer = new byte[output.Length * sizeof(long)];
        if (!Control(_handle, IoctlExecute, request, answer, out var returned))
        {
            error = Marshal.GetLastPInvokeError();
            return -1;
        }

        error = 0;
        var count = (int)Math.Min(returned / sizeof(long), (uint)output.Length);
        MemoryMarshal.Cast<byte, long>(answer.AsSpan(0, count * sizeof(long))).CopyTo(output);
        return count;
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();

    private static unsafe bool Control(SafeFileHandle handle, uint code, byte[] input, byte[] output, out uint returned)
    {
        fixed (byte* i = input, o = output)
        {
            return DeviceIoControl(handle, code, i, (uint)input.Length, output.Length == 0 ? null : o, (uint)output.Length, out returned, 0);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
}
