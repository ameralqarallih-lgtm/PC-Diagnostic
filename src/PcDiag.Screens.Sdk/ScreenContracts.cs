namespace PcDiag.Screens;

/// <summary>How a screen takes its pictures.</summary>
public enum FrameFormat
{
    /// <summary>A baseline JPEG at the screen's native size.</summary>
    Jpeg,

    /// <summary>A PNG at the screen's native size.</summary>
    Png,

    /// <summary>Raw 16-bit RGB565, little-endian, row by row.</summary>
    Rgb565,
}

/// <summary>A screen model a driver drives.</summary>
/// <param name="Id">Its id within the driver, e.g. "universal-88".</param>
/// <param name="Name">Its name as people know it, e.g. "Lian Li Universal Screen 8.8″".</param>
/// <param name="VendorId">USB vendor id.</param>
/// <param name="ProductId">USB product id.</param>
/// <param name="Width">Width of the picture it takes (its native orientation).</param>
/// <param name="Height">Height of the picture it takes.</param>
/// <param name="Format">How it takes pictures.</param>
/// <param name="MaxFrameBytes">The largest picture it takes, in bytes (0: no limit).</param>
/// <param name="MaxFps">The most pictures a second worth sending.</param>
/// <param name="Rotation">
/// How a picture is turned by default, in degrees clockwise, to read upright as it is usually mounted (90: a portrait
/// panel mounted as a landscape bar).
/// </param>
/// <param name="Tested">Whether this model has been run on real hardware with this driver.</param>
public sealed record ScreenModel(
    string Id,
    string Name,
    int VendorId,
    int ProductId,
    int Width,
    int Height,
    FrameFormat Format,
    int MaxFrameBytes = 0,
    int MaxFps = 30,
    int Rotation = 0,
    bool Tested = false);

/// <summary>Another program that holds a driver's screens (so it is paused while this app uses one).</summary>
/// <param name="Service">A Windows service, stopped while the screen is used and started again after.</param>
/// <param name="Name">How people know the program, e.g. "L-Connect".</param>
public sealed record ScreenHolder(string Service, string Name);

/// <summary>A screen found on the PC, with the driver and model that drive it.</summary>
/// <param name="Key">Its stable key: usb:vid:pid:serial (lower case hexadecimal).</param>
/// <param name="DriverId">The driver's id.</param>
/// <param name="Model">The model.</param>
/// <param name="Path">The device path the host opens.</param>
/// <param name="Serial">Its serial number (part of its key).</param>
public sealed record ScreenDevice(string Key, string DriverId, ScreenModel Model, string Path, string Serial)
{
    /// <summary>The key a USB screen has.</summary>
    public static string KeyOf(int vendorId, int productId, string serial) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"usb:{vendorId:x4}:{productId:x4}:{serial.ToLowerInvariant()}");
}

/// <summary>Bulk USB transfers to one screen (supplied by the screen host).</summary>
public interface IScreenTransport : IDisposable
{
    /// <summary>Writes one transfer; false when it failed or timed out.</summary>
    bool Write(ReadOnlySpan<byte> data, TimeSpan timeout);

    /// <summary>Reads one transfer of up to <paramref name="max"/> bytes; null when nothing came in time.</summary>
    byte[]? Read(int max, TimeSpan timeout);
}

/// <summary>
/// A driver: the screen models it knows and how to talk to one. A package's driver type has a public constructor taking
/// the package's id and its models (<c>(string id, IReadOnlyList&lt;ScreenModel&gt; models)</c>).
/// </summary>
public interface IScreenDriver
{
    /// <summary>The driver's id (its package's).</summary>
    string Id { get; }

    /// <summary>The models it drives.</summary>
    IReadOnlyList<ScreenModel> Models { get; }

    /// <summary>Starts a session with a screen over its transport (the screen is ready for pictures after it).</summary>
    IScreenSession Open(ScreenModel model, IScreenTransport transport);
}

/// <summary>A screen in use: pictures go to it until it is disposed (the screen keeps the last one).</summary>
public interface IScreenSession : IDisposable
{
    /// <summary>The screen's firmware, when it says.</summary>
    string? Firmware { get; }

    /// <summary>Shows a picture in the model's format and size; false when the screen did not take it.</summary>
    bool Present(ReadOnlySpan<byte> frame);

    /// <summary>Sets the backlight, 0–100 % (a screen without one ignores it).</summary>
    void SetBrightness(int percent);
}
