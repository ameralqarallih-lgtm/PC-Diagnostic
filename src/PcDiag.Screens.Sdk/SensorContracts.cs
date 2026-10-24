namespace PcDiag.Screens;

/// <summary>
/// A sensor driver: reads the values of devices this app has no built-in support for (a sensor on a serial port, a
/// HID device, a gadget that answers JSON on the local network). It runs only in the host without administrator rights,
/// read every few seconds with a time limit; one that keeps failing is set aside. A package's driver type with code has
/// a public constructor taking the package's id, its sensor settings and the host's <see cref="ISensorIo"/>
/// (<c>(string id, SensorSpec spec, ISensorIo io)</c>).
/// </summary>
public interface ISensorDriver : IDisposable
{
    /// <summary>The driver's id (its package's).</summary>
    string Id { get; }

    /// <summary>Reads its values now (at most <see cref="SensorDrivers.MaxValues"/> are kept).</summary>
    IReadOnlyList<SensorValue> Read(CancellationToken cancellationToken);
}

/// <summary>
/// One value a sensor driver read.
/// </summary>
/// <param name="Device">The device it comes from, e.g. "probe-1" (letters, digits, dashes).</param>
/// <param name="Sensor">The sensor on it, e.g. "water-temp" (letters, digits, dashes).</param>
/// <param name="Name">How people read it, e.g. "Water temperature".</param>
/// <param name="Kind">temperature, clock, power, fan, voltage, load or other.</param>
/// <param name="Unit">Its unit, e.g. "°C".</param>
/// <param name="Value">Its value now; null when it could not be read this time.</param>
public sealed record SensorValue(string Device, string Sensor, string Name, string Kind, string Unit, double? Value);

/// <summary>
/// What the host gives a sensor driver to reach its device. A family's driver uses nothing else; a driver with code runs
/// in the host with a standard user's rights (never an administrator's), so only approve packages you trust.
/// </summary>
public interface ISensorIo
{
    /// <summary>
    /// GETs a URL on this PC or the local network (anything else is refused), at most <paramref name="maxBytes"/>, within
    /// <paramref name="timeout"/>.
    /// </summary>
    byte[] Fetch(Uri url, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Opens a serial port (e.g. "COM3") at a speed; its lines are read as they come.</summary>
    ISerialLines OpenSerial(string port, int baud);

    /// <summary>Opens the first HID device with this vendor and product id; null when none is connected.</summary>
    IHidReports? OpenHid(int vendorId, int productId);
}

/// <summary>A serial port's text, line by line.</summary>
public interface ISerialLines : IDisposable
{
    /// <summary>The lines that came since the last call (at most 256).</summary>
    IReadOnlyList<string> Take();
}

/// <summary>A HID device's input reports.</summary>
public interface IHidReports : IDisposable
{
    /// <summary>Its next input report (the report id first), or null when none came within the time.</summary>
    byte[]? Read(TimeSpan timeout);
}
