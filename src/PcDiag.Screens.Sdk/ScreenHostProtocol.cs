using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcDiag.Screens;

/// <summary>
/// What the recorder and its screen host say over their pipe. The recorder (elevated) never loads a driver: it starts
/// the host without administrator rights, the host connects back with the one-time token it was given, is told which
/// packages are approved, and then reports the screens it finds and shows the pictures it is sent. Each message is a
/// 4-byte little-endian length, a kind byte and its body: JSON for everything but a picture.
/// </summary>
public static class ScreenHostProtocol
{
    /// <summary>The largest message (a picture and its key).</summary>
    public const int MaxMessage = 16 * 1024 * 1024;

    /// <summary>JSON as both sides write it.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    /// <summary>Message kinds.</summary>
    public enum Kind : byte
    {
        /// <summary>Host → recorder, first: <see cref="Hello"/>.</summary>
        Hello = 1,

        /// <summary>Recorder → host, the answer: <see cref="Setup"/>.</summary>
        Setup = 2,

        /// <summary>Host → recorder: <see cref="Packages"/>, the packages found and why one is not used.</summary>
        Packages = 3,

        /// <summary>Host → recorder: <see cref="Devices"/>, the screens connected now (sent when they change).</summary>
        Devices = 4,

        /// <summary>Recorder → host: <see cref="Target"/>, start a session with a screen.</summary>
        Open = 5,

        /// <summary>Recorder → host: <see cref="Target"/>, end it (the screen keeps its picture).</summary>
        Close = 6,

        /// <summary>Recorder → host: a picture (2-byte key length, the key, the picture's bytes).</summary>
        Frame = 7,

        /// <summary>Recorder → host: <see cref="Brightness"/>.</summary>
        Brightness = 8,

        /// <summary>Host → recorder: <see cref="Status"/>, a screen's session changed or a picture was not shown.</summary>
        Status = 9,

        /// <summary>Host → recorder: <see cref="Readings"/>, the sensor drivers' values (at most once a second).</summary>
        Readings = 10,
    }

    /// <summary>The host's greeting.</summary>
    public sealed record Hello(string Token, int ProcessId, string Version);

    /// <summary>The approved packages (id and hash) and where packages are.</summary>
    public sealed record Setup(string DriversFolder, IReadOnlyList<Approval> Approved);

    /// <summary>A package the user approved.</summary>
    public sealed record Approval(string Id, string Sha256);

    /// <summary>A package found, as the user sees it.</summary>
    public sealed record PackageInfo(string Id, string Name, string Version, string Publisher, bool BuiltIn, string? Sha256, bool Approved, bool Loaded, string? Problem, IReadOnlyList<string> Models, IReadOnlyList<string>? Capabilities = null, IReadOnlyList<string>? Sensors = null);

    /// <summary>The packages found.</summary>
    public sealed record Packages(IReadOnlyList<PackageInfo> List);

    /// <summary>A screen connected now.</summary>
    public sealed record DeviceInfo(string Key, string DriverId, ScreenModel Model, string Serial, bool Busy);

    /// <summary>The screens connected now.</summary>
    public sealed record Devices(IReadOnlyList<DeviceInfo> List);

    /// <summary>A screen a request is about.</summary>
    public sealed record Target(string Key);

    /// <summary>A screen's backlight.</summary>
    public sealed record Brightness(string Key, int Percent);

    /// <summary>
    /// A screen's session: open (with its firmware) or not (and why), how the last picture went, and the pictures shown
    /// and not taken since it opened (sent every few seconds while they change).
    /// </summary>
    public sealed record Status(string Key, bool Open, string? Firmware, string? Problem, bool? Shown = null, int Pictures = 0, int Refused = 0);

    /// <summary>A sensor driver's values now (at most <see cref="SensorDrivers.MaxValues"/>), or why it gave none.</summary>
    public sealed record DriverReadings(string DriverId, IReadOnlyList<SensorValue> Values, string? Problem);

    /// <summary>The sensor drivers' values.</summary>
    public sealed record Readings(IReadOnlyList<DriverReadings> List);

    /// <summary>Writes a message.</summary>
    public static async Task WriteAsync(Stream stream, Kind kind, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var head = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(head, body.Length + 1);
        head[4] = (byte)kind;
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a JSON message.</summary>
    public static Task WriteJsonAsync<T>(Stream stream, Kind kind, T body, CancellationToken cancellationToken) =>
        WriteAsync(stream, kind, JsonSerializer.SerializeToUtf8Bytes(body, Json), cancellationToken);

    /// <summary>A picture for a screen as a message body.</summary>
    public static byte[] FrameBody(string key, ReadOnlySpan<byte> picture)
    {
        ArgumentNullException.ThrowIfNull(key);
        var name = Encoding.UTF8.GetBytes(key);
        var body = new byte[2 + name.Length + picture.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)name.Length);
        name.CopyTo(body, 2);
        picture.CopyTo(body.AsSpan(2 + name.Length));
        return body;
    }

    /// <summary>A picture message's key and picture.</summary>
    public static (string Key, ReadOnlyMemory<byte> Picture) ReadFrame(ReadOnlyMemory<byte> body)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
        return (Encoding.UTF8.GetString(body.Span.Slice(2, length)), body[(2 + length)..]);
    }

    /// <summary>Reads a message; null at the end of the stream.</summary>
    public static async Task<(Kind Kind, byte[] Body)?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var head = new byte[5];
        if (!await FillAsync(stream, head, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (length is < 1 or > MaxMessage)
        {
            throw new InvalidDataException("A screen host message has an impossible length.");
        }

        var body = new byte[length - 1];
        return await FillAsync(stream, body, cancellationToken).ConfigureAwait(false) ? ((Kind)head[4], body) : null;
    }

    /// <summary>A JSON body.</summary>
    public static T Parse<T>(byte[] body) => JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("An empty screen host message.");

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
