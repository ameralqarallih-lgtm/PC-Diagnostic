using System.Globalization;
using System.Runtime.InteropServices;
using PcDiag.Core.Telemetry;

namespace PcDiag.Platform.Windows.Pdh;

/// <summary>
/// A PDH query: add counters once, then call <see cref="Collect"/> once per tick and read every counter.
/// Not thread-safe; each collector owns its query and the scheduler never runs a collector concurrently with itself.
/// </summary>
public sealed class PdhQuery : IDisposable
{
    private readonly List<PdhCounter> _counters = [];
    private nint _handle;

    private PdhQuery(nint handle) => _handle = handle;

    /// <summary>Counters added to this query.</summary>
    public IReadOnlyList<PdhCounter> Counters => _counters;

    /// <summary>Opens a real-time query.</summary>
    /// <exception cref="PdhException">PDH could not open a query (for example, the service is unavailable).</exception>
    public static PdhQuery Open()
    {
        var status = PdhNative.PdhOpenQuery(null, 0, out var handle);
        return status == PdhNative.ErrorSuccess ? new PdhQuery(handle) : throw new PdhException(status, "PdhOpenQuery");
    }

    /// <summary>
    /// Adds a counter by English path, e.g. <c>\Processor Information(_Total)\% Processor Utility</c>.
    /// Returns null (and the PDH status) when the counter does not exist on this machine.
    /// </summary>
    public PdhCounter? TryAddCounter(string englishPath, out uint status)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        status = PdhNative.PdhAddEnglishCounter(_handle, englishPath, 0, out var counterHandle);
        if (status != PdhNative.ErrorSuccess)
        {
            return null;
        }

        var counter = new PdhCounter(counterHandle, englishPath);
        _counters.Add(counter);
        return counter;
    }

    /// <summary>Collects raw values for every counter. Returns the PDH status (0 on success).</summary>
    public uint Collect()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        return PdhNative.PdhCollectQueryData(_handle);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle != 0)
        {
            _ = PdhNative.PdhCloseQuery(_handle); // also frees the counters
            _handle = 0;
        }
    }
}

/// <summary>One counter in a <see cref="PdhQuery"/>. Valid until the query is disposed.</summary>
public sealed class PdhCounter
{
    private readonly nint _handle;
    private byte[] _arrayBuffer = [];
    private bool _hadValidValue;

    internal PdhCounter(nint handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    /// <summary>The counter path.</summary>
    public string Path { get; }

    /// <summary>
    /// Reads the formatted value from the last <see cref="PdhQuery.Collect"/>.
    /// Rate counters need two collections: the first read is <see cref="Availability.Pending"/>.
    /// </summary>
    /// <param name="noCap100">Allow percentages above 100 (e.g. "% Processor Utility" under turbo).</param>
    public Reading<double> GetDouble(bool noCap100 = false)
    {
        var status = PdhNative.PdhGetFormattedCounterValue(_handle, Format(noCap100), out _, out var value);
        if (status == PdhNative.ErrorSuccess && IsValid(value.CStatus) && double.IsFinite(value.DoubleValue))
        {
            _hadValidValue = true;
            return Reading<double>.Of(value.DoubleValue);
        }

        return NotAvailable();
    }

    /// <summary>
    /// Reads every instance of a wildcard counter, e.g. <c>\Processor Information(*)\% Processor Utility</c>.
    /// Allocates one string per instance; for counters with thousands of instances use <see cref="VisitArray"/>.
    /// </summary>
    public IReadOnlyList<PdhInstanceValue> GetArray(bool noCap100 = false)
    {
        var result = new List<PdhInstanceValue>();
        VisitArray(ref result, static (ref List<PdhInstanceValue> list, ReadOnlySpan<char> instance, Reading<double> value) =>
            list.Add(new PdhInstanceValue(instance.ToString(), value)), noCap100);
        return result;
    }

    /// <summary>
    /// Visits every instance of a wildcard counter without allocating instance-name strings.
    /// The span passed to the visitor is only valid during the call. Returns false when the array could not be read.
    /// </summary>
    public unsafe bool VisitArray<TState>(ref TState state, PdhArrayVisitor<TState> visitor, bool noCap100 = false)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (!FetchArray(Format(noCap100), out var count))
        {
            return false;
        }

        var items = MemoryMarshal.Cast<byte, PdhFmtCounterValueItem>(_arrayBuffer.AsSpan()).Slice(0, (int)count);
        foreach (var item in items)
        {
            var name = item.Name == 0 ? [] : MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)item.Name);
            Reading<double> reading;
            if (IsValid(item.Value.CStatus) && double.IsFinite(item.Value.DoubleValue))
            {
                _hadValidValue = true;
                reading = Reading<double>.Of(item.Value.DoubleValue);
            }
            else
            {
                reading = NotAvailable();
            }

            visitor(ref state, name, reading);
        }

        return true;
    }

    private unsafe bool FetchArray(uint format, out uint count)
    {
        count = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = (uint)_arrayBuffer.Length;
            uint status;
            fixed (byte* buffer = _arrayBuffer)
            {
                status = PdhNative.PdhGetFormattedCounterArray(_handle, format, ref size, out count, size == 0 ? null : buffer);
            }

            if (status == PdhNative.PdhMoreData)
            {
                _arrayBuffer = new byte[size + 4096]; // instances can appear between the two calls
                continue;
            }

            return status == PdhNative.ErrorSuccess;
        }

        return false;
    }

    private Reading<double> NotAvailable() =>
        Reading<double>.Unavailable(_hadValidValue ? Availability.Error : Availability.Pending);

    private static bool IsValid(uint cstatus) => cstatus is PdhNative.CStatusValidData or PdhNative.CStatusNewData;

    private static uint Format(bool noCap100) => PdhNative.FormatDouble | (noCap100 ? PdhNative.FormatNoCap100 : 0);

    /// <inheritdoc />
    public override string ToString() => Path;
}

/// <summary>Receives one instance of a wildcard counter; <paramref name="instance"/> is only valid during the call.</summary>
public delegate void PdhArrayVisitor<TState>(ref TState state, ReadOnlySpan<char> instance, Reading<double> value);

/// <summary>One instance of a wildcard counter.</summary>
/// <param name="Instance">Instance name, e.g. "0,3" for processor 3 in group 0.</param>
/// <param name="Value">The instance's value.</param>
public sealed record PdhInstanceValue(string Instance, Reading<double> Value);

/// <summary>A PDH call failed.</summary>
public sealed class PdhException : Exception
{
    /// <summary>Creates the exception for a PDH status code.</summary>
    public PdhException(uint status, string operation)
        : base($"{operation} failed with PDH status 0x{status.ToString("X8", CultureInfo.InvariantCulture)}.")
    {
        Status = status;
    }

    /// <summary>Creates the exception.</summary>
    public PdhException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PdhException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PdhException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The PDH status code.</summary>
    public uint Status { get; }
}
