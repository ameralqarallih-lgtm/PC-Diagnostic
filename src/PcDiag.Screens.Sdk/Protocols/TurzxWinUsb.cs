using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace PcDiag.Screens.Protocols;

/// <summary>
/// TURZX panels over WinUSB (Lian Li's Universal Screen 8.8″ and its siblings): every command opens with a 512-byte
/// header, its 500-byte plaintext (command, 0, 1A 6D, a millisecond timestamp, parameters from byte 8) DES-CBC
/// encrypted with key and IV "slv3tuzx" and PKCS#7 padding to 504 bytes, then 6 zero bytes and A1 1A. A picture is the
/// PushJpg header carrying the JPEG's size followed by the JPEG in the same transfer. The panel answers each command
/// with up to 512 bytes (the command, C8 for success, a body from byte 8); a full 512-byte answer is followed by a
/// zero-length packet, read so the next answer lines up. Protocol facts from Hypercolor's spec 84 and lian-li-linux
/// (MIT), checked on a Universal Screen 8.8″ (firmware lianli88_0001_0023): every frame acknowledged in about 11 ms.
/// </summary>
public static class TurzxWinUsb
{
    /// <summary>The protocol family's name in a driver package.</summary>
    public const string Family = "turzx-winusb";

    /// <summary>A header's length on the wire.</summary>
    public const int HeaderLength = 512;

    /// <summary>The success status in an answer.</summary>
    public const byte Ok = 0xC8;

    private static readonly byte[] Key = "slv3tuzx"u8.ToArray();

    /// <summary>Commands (the first plaintext byte).</summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "The protocol's vocabulary, read with the protocol.")]
    public static class Commands
    {
        /// <summary>Reads the firmware string.</summary>
        public const byte GetVersion = 0x0A;

        /// <summary>Sets the backlight, 0–100.</summary>
        public const byte Brightness = 0x0E;

        /// <summary>Sets the panel's frame rate.</summary>
        public const byte FrameRate = 0x0F;

        /// <summary>Sets the clock overlay's time.</summary>
        public const byte SetClock = 0x33;

        /// <summary>Stops the clock overlay.</summary>
        public const byte StopClock = 0x34;

        /// <summary>A JPEG for the background layer (its size big-endian at byte 8).</summary>
        public const byte PushJpg = 0x65;

        /// <summary>A PNG for the overlay layer above it.</summary>
        public const byte PushPng = 0x66;

        /// <summary>Stops video playback another program left running.</summary>
        public const byte StopPlay = 0x7B;
    }

    /// <summary>A command's 512-byte header.</summary>
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The panel's wire format; the key ships with every copy of the vendor's software (obfuscation, not security).")]
    public static byte[] Header(byte command, uint timestamp, ReadOnlySpan<byte> parameters)
    {
        Span<byte> plain = stackalloc byte[500];
        plain.Clear();
        plain[0] = command;
        plain[2] = 0x1A;
        plain[3] = 0x6D;
        BinaryPrimitives.WriteUInt32LittleEndian(plain[4..], timestamp);
        parameters[..Math.Min(parameters.Length, 492)].CopyTo(plain[8..]);
        using var des = DES.Create();
        des.Key = Key;
        var header = new byte[HeaderLength];
        des.EncryptCbc(plain, Key, header.AsSpan(0, 504), PaddingMode.PKCS7);
        header[510] = 0xA1;
        header[511] = 0x1A;
        return header;
    }

    /// <summary>A size as the panel reads it (big-endian).</summary>
    public static byte[] Size(int bytes)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, bytes);
        return size;
    }

    /// <summary>The clock's parameters for a moment (year big-endian, month, day, hour, minute, second, mode 2).</summary>
    public static byte[] Clock(DateTime local) =>
        [(byte)(local.Year >> 8), (byte)local.Year, (byte)local.Month, (byte)local.Day, (byte)local.Hour, (byte)local.Minute, (byte)local.Second, 2];

    /// <summary>Drives the panels of a driver package that names this family.</summary>
    internal sealed class Driver(string id, IReadOnlyList<ScreenModel> models, Func<int, int, byte[]> clearPng, Func<int, int, byte[]> blackJpeg) : IScreenDriver
    {
        public string Id => id;

        public IReadOnlyList<ScreenModel> Models => models;

        public IScreenSession Open(ScreenModel model, IScreenTransport transport) =>
            Session.Start(model, transport, clearPng(model.Width, model.Height), blackJpeg(model.Width, model.Height));
    }

    /// <summary>A panel in use.</summary>
    internal sealed class Session : IScreenSession
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan FrameTimeout = TimeSpan.FromMilliseconds(500);
        private readonly ScreenModel _model;
        private readonly IScreenTransport _transport;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private uint _last;

        private Session(ScreenModel model, IScreenTransport transport)
        {
            _model = model;
            _transport = transport;
        }

        public string? Firmware { get; private set; }

        /// <summary>The panel's buffered pictures, from its last answer to a picture.</summary>
        public int Buffered { get; private set; }

        /// <summary>
        /// Wakes the panel the way the vendor's software leaves it: answers left unread are drained, playback stopped (and
        /// a moment for it), the firmware read, frame rate and backlight set, the clock overlay off, both layers cleared.
        /// </summary>
        public static Session Start(ScreenModel model, IScreenTransport transport, byte[] clearPng, byte[] blackJpeg)
        {
            var session = new Session(model, transport);

            // Answers another program left unread, until the panel is quiet (a long-running vendor app can leave many).
            for (var i = 0; i < 1024 && transport.Read(512, TimeSpan.FromMilliseconds(50)) is not null; i++)
            {
            }

            session.Command(Commands.StopPlay, []);
            Thread.Sleep(150);
            if (session.Command(Commands.GetVersion, []) is { Length: > 8 } version)
            {
                var end = Array.IndexOf(version, (byte)0, 8);
                session.Firmware = System.Text.Encoding.ASCII.GetString(version, 8, (end < 0 ? Math.Min(version.Length, 40) : end) - 8).Trim();
            }

            session.Command(Commands.FrameRate, [120]);
            session.Command(Commands.Brightness, [100]);
            session.Command(Commands.SetClock, Clock(DateTime.Now));
            session.Command(Commands.StopClock, [0]);
            session.Command(Commands.PushPng, Size(clearPng.Length), clearPng);
            session.Command(Commands.PushJpg, Size(blackJpeg.Length), blackJpeg);
            return session;
        }

        public bool Present(ReadOnlySpan<byte> frame)
        {
            if (frame.IsEmpty || (_model.MaxFrameBytes > 0 && frame.Length > _model.MaxFrameBytes))
            {
                return false;
            }

            var command = _model.Format == FrameFormat.Png ? Commands.PushPng : Commands.PushJpg;
            var reply = Command(command, Size(frame.Length), frame, FrameTimeout);
            if (reply is { Length: > 8 })
            {
                Buffered = reply[8];
            }

            return reply is { Length: > 1 } && reply[0] == command && reply[1] == Ok;
        }

        public void SetBrightness(int percent) => Command(Commands.Brightness, [(byte)Math.Clamp(percent, 0, 100)]);

        /// <summary>The panel keeps its last picture; nothing is sent.</summary>
        public void Dispose() => _transport.Dispose();

        /// <summary>Sends a command (and its payload in the same transfer) and reads its answer.</summary>
        private byte[]? Command(byte command, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> payload = default, TimeSpan? timeout = null)
        {
            var header = Header(command, NextTimestamp(), parameters);
            byte[] transfer;
            if (payload.IsEmpty)
            {
                transfer = header;
            }
            else
            {
                transfer = new byte[header.Length + payload.Length];
                header.CopyTo(transfer, 0);
                payload.CopyTo(transfer.AsSpan(header.Length));
            }

            if (!_transport.Write(transfer, TimeSpan.FromSeconds(5)))
            {
                return null;
            }

            // Its answer: answers to earlier commands still waiting (another program left them, or one came late) and
            // the zero-length packets after full answers are passed over, so the answers stay in step.
            var deadline = Stopwatch.StartNew();
            var wait = timeout ?? CommandTimeout;
            while (deadline.Elapsed < wait)
            {
                var reply = _transport.Read(512, wait - deadline.Elapsed);
                if (reply is null)
                {
                    return null;
                }

                if (reply.Length > 0 && reply[0] == command)
                {
                    Stale = 0;
                    if (reply.Length == 512)
                    {
                        // A full answer is followed by a zero-length packet.
                        _transport.Read(512, TimeSpan.FromMilliseconds(50));
                    }

                    return reply;
                }

                if (reply.Length > 0)
                {
                    Stale++;
                }
            }

            return null;
        }

        /// <summary>Answers passed over since the last one in step (for diagnostics).</summary>
        public int Stale { get; private set; }

        /// <summary>Milliseconds since the session began, strictly increasing (the panel drops repeats).</summary>
        private uint NextTimestamp()
        {
            var now = (uint)_clock.ElapsedMilliseconds;
            _last = now <= _last ? _last + 1 : now;
            return _last;
        }
    }
}
