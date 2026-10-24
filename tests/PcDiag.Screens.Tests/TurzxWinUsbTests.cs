using System.Buffers.Binary;
using System.Security.Cryptography;
using PcDiag.Screens.Protocols;

namespace PcDiag.Screens.Tests;

public sealed class TurzxWinUsbTests
{
    private static readonly ScreenModel Universal = new("universal-88", "Lian Li Universal Screen 8.8″", 0x1CBE, 0xA088, 480, 1920, FrameFormat.Jpeg, 512_000, 30, 90, Tested: true);

    [Fact]
    public void Headers_match_the_known_answers()
    {
        // Hypercolor spec 84 section 3.2 (OpenSSL); the same bytes the panel acknowledged on the dev PC.
        var getVersion = TurzxWinUsb.Header(TurzxWinUsb.Commands.GetVersion, 1, []);
        Convert.ToHexStringLower(getVersion[..16]).ShouldBe("f132a5d4e3cff85748b82aaacac68f8a");
        Convert.ToHexStringLower(getVersion[488..504]).ShouldBe("9d6987a86a3d9954b13ec42bb4836704");
        var push = TurzxWinUsb.Header(TurzxWinUsb.Commands.PushJpg, 42, TurzxWinUsb.Size(123_456));
        Convert.ToHexStringLower(push[..16]).ShouldBe("6edf63cc8e6c3fe43f32428b39cce22c");
        Convert.ToHexStringLower(push[496..504]).ShouldBe("27382732b83ef643");

        push.Length.ShouldBe(512);
        push[504..510].ShouldAllBe(b => b == 0);
        push[510].ShouldBe((byte)0xA1);
        push[511].ShouldBe((byte)0x1A);

        // Back to plaintext: the command, 1A 6D, the timestamp little-endian, the size big-endian.
        var plain = Decrypt(push);
        plain[0].ShouldBe(TurzxWinUsb.Commands.PushJpg);
        (plain[2], plain[3]).ShouldBe(((byte)0x1A, (byte)0x6D));
        BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4)).ShouldBe(42u);
        BinaryPrimitives.ReadInt32BigEndian(plain.AsSpan(8)).ShouldBe(123_456);
        TurzxWinUsb.Clock(new DateTime(2026, 10, 9, 9, 4, 5)).ShouldBe(new byte[] { 0x07, 0xEA, 10, 9, 9, 4, 5, 2 });
    }

    [Fact]
    public void A_session_wakes_the_panel_shows_pictures_and_keeps_its_answers_in_step()
    {
        var panel = new FakePanel();
        var driver = ScreenDrivers.Create(ScreenDriverPackage.BuiltInPackages().Single(p => p.Id == "lianli-turzx"), (w, h) => [1, 2, 3], (w, h) => [4, 5]);
        using var session = driver.Open(driver.Models.Single(m => m.Id == "universal-88"), panel);

        // Unread answers drained, then the vendor's wake sequence, each answered in turn (a full answer and an empty packet).
        panel.Commands.ShouldBe([
            TurzxWinUsb.Commands.StopPlay, TurzxWinUsb.Commands.GetVersion, TurzxWinUsb.Commands.FrameRate, TurzxWinUsb.Commands.Brightness,
            TurzxWinUsb.Commands.SetClock, TurzxWinUsb.Commands.StopClock, TurzxWinUsb.Commands.PushPng, TurzxWinUsb.Commands.PushJpg]);
        session.Firmware.ShouldBe("lianli88_0001_0023");
        panel.Stale.ShouldBe(0);

        session.Present(new byte[60_000]).ShouldBeTrue();
        panel.Commands[^1].ShouldBe(TurzxWinUsb.Commands.PushJpg);
        panel.LastPayloadLength.ShouldBe(60_000);

        // Too large for the panel: refused without sending; a picture the panel does not answer is not shown.
        session.Present(new byte[512_001]).ShouldBeFalse();
        panel.Silent = true;
        session.Present(new byte[1000]).ShouldBeFalse();

        // Timestamps strictly increase (the panel drops repeats).
        panel.Timestamps.Zip(panel.Timestamps.Skip(1)).ShouldAllBe(p => p.Second > p.First);
    }

    [Fact]
    public void A_session_passes_over_answers_left_unread_or_late_and_stays_in_step()
    {
        // A long-running vendor app left 40 answers unread (more than a quick drain reads), and one comes late later.
        var panel = new FakePanel(stale: 40) { DrainReadsBeforeQuiet = 64 };
        var driver = ScreenDrivers.Create(ScreenDriverPackage.BuiltInPackages().Single(), (w, h) => [1], (w, h) => [2]);
        using var session = driver.Open(driver.Models.Single(m => m.Id == "universal-88"), panel);
        session.Firmware.ShouldBe("lianli88_0001_0023");

        panel.Late(TurzxWinUsb.Commands.Brightness);
        session.Present(new byte[1000]).ShouldBeTrue();
        session.Present(new byte[1000]).ShouldBeTrue();
        panel.Stale.ShouldBe(0);
    }

    [Fact]
    public void Driver_packages_are_checked_and_a_changed_package_has_a_new_hash()
    {
        var builtIn = ScreenDriverPackage.BuiltInPackages().Single();
        builtIn.BuiltIn.ShouldBeTrue();
        builtIn.Models.Single(m => m.Id == "universal-88").ToModel().ShouldBe(Universal);
        builtIn.Holders!.Select(h => h.Service).ShouldBe(["LConnectServiceWatcher", "LConnectService"]);

        ScreenDriverPackage.Parse("""{ "id": "Bad Id", "name": "n", "version": "1", "publisher": "p", "family": "turzx-winusb", "models": [] }""", out var problem).ShouldBeNull();
        problem.ShouldNotBeNull();
        ScreenDriverPackage.Parse("""{ "id": "x", "name": "n", "version": "1", "publisher": "p", "family": "turzx-winusb", "assembly": "x.dll", "type": "T", "models": [] }""", out problem).ShouldBeNull();
        problem!.ShouldContain("either");
        ScreenDriverPackage.Parse("""{ "id": "x", "name": "n", "version": "1", "publisher": "p", "family": "unknown", "models": [ { "id": "m", "name": "M", "vid": "1234", "pid": "5678", "width": 320, "height": 480, "format": "jpeg" } ] }""", out problem).ShouldBeNull();
        problem!.ShouldContain("family");

        var folder = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var manifest = """{ "id": "my-screen", "name": "My screen", "version": "1.0", "publisher": "Me", "family": "turzx-winusb", "models": [ { "id": "m", "name": "M", "vid": "1CBE", "pid": "0088", "width": 480, "height": 1920, "format": "jpeg" } ] }""";
            File.WriteAllText(Path.Combine(folder, "driver.json"), manifest);
            var first = ScreenDriverPackage.Read(folder, out problem)!;
            problem.ShouldBeNull();
            first.BuiltIn.ShouldBeFalse();
            first.Sha256.Length.ShouldBe(64);
            File.WriteAllText(Path.Combine(folder, "driver.json"), manifest.Replace("1.0", "1.1", StringComparison.Ordinal));
            ScreenDriverPackage.Read(folder, out _)!.Sha256.ShouldNotBe(first.Sha256);

            // An assembly outside the package's folder is refused.
            File.WriteAllText(Path.Combine(folder, "driver.json"), """{ "id": "x", "name": "n", "version": "1", "publisher": "p", "assembly": "..\\evil.dll", "type": "T", "models": [ { "id": "m", "name": "M", "vid": "1234", "pid": "5678", "width": 320, "height": 480, "format": "jpeg" } ] }""");
            ScreenDriverPackage.Read(folder, out problem).ShouldBeNull();
            problem!.ShouldContain("folder");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static byte[] Decrypt(byte[] header)
    {
#pragma warning disable CA5351 // The panel's wire format.
        using var des = DES.Create();
#pragma warning restore CA5351
        var key = "slv3tuzx"u8.ToArray();
        des.Key = key;
        return des.DecryptCbc(header.AsSpan(0, 504), key, PaddingMode.PKCS7);
    }

    /// <summary>A panel that answers like the real one: a full 512-byte answer then a zero-length packet.</summary>
    private sealed class FakePanel : IScreenTransport
    {
        private readonly Queue<byte[]> _answers = new();

        public FakePanel(int stale = 1)
        {
            // Answers another program left unread.
            for (var i = 0; i < stale; i++)
            {
                _answers.Enqueue(Answer(i % 2 == 0 ? TurzxWinUsb.Commands.GetVersion : TurzxWinUsb.Commands.PushJpg));
                _answers.Enqueue([]);
            }
        }

        /// <summary>How many unread answers come before the panel goes quiet (a drain that stops sooner leaves the rest).</summary>
        public int DrainReadsBeforeQuiet { get; set; } = int.MaxValue;

        /// <summary>An answer to an earlier command arrives late.</summary>
        public void Late(byte command)
        {
            _answers.Enqueue(Answer(command));
            _answers.Enqueue([]);
        }

        public List<byte> Commands { get; } = [];

        public List<uint> Timestamps { get; } = [];

        public int LastPayloadLength { get; private set; }

        public bool Silent { get; set; }

        public int Stale => _answers.Count;

        public bool Write(ReadOnlySpan<byte> data, TimeSpan timeout)
        {
            var plain = Decrypt(data[..512].ToArray());
            Commands.Add(plain[0]);
            Timestamps.Add(BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4)));
            LastPayloadLength = data.Length - 512;
            if (!Silent)
            {
                _answers.Enqueue(Answer(plain[0]));
                _answers.Enqueue([]);
            }

            return true;
        }

        public byte[]? Read(int max, TimeSpan timeout) => _answers.Count > 0 ? _answers.Dequeue() : null;

        public void Dispose()
        {
        }

        private static byte[] Answer(byte command)
        {
            var answer = new byte[512];
            answer[0] = command;
            answer[1] = TurzxWinUsb.Ok;
            if (command == TurzxWinUsb.Commands.GetVersion)
            {
                "lianli88_0001_0023"u8.CopyTo(answer.AsSpan(8));
            }

            return answer;
        }
    }
}
