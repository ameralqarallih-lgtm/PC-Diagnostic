using System.Buffers.Binary;
using PcDiag.Core.Overlay;

namespace PcDiag.Core.Tests;

public sealed class CharacterPackTests
{
    /// <summary>A PNG's signature and header for a size, then filler (the pack reads only the header).</summary>
    private static byte[] Png(int width, int height, int extra = 20)
    {
        var bytes = new byte[33 + extra];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static byte[] Pack(int width = 40, int height = 50, double tall = 40, int faces = 1, params (string Action, int Fps, int Frames)[] clips) =>
        CharacterPack.Write("Kit", width, height, 20, 48, tall, faces, [.. (clips.Length > 0 ? clips : [("idle", 12, 3), ("walk", 24, 4)]).Select(c => (c.Action, c.Fps, (IReadOnlyList<byte[]>)[.. Enumerable.Range(0, c.Frames).Select(i => Png(width, height, 20 + i))]))]);

    [Fact]
    public void A_character_is_read_back_as_written()
    {
        var bytes = Pack();
        var pack = CharacterPack.Read(bytes)!;
        pack.Name.ShouldBe("Kit");
        (pack.Width, pack.Height, pack.AnchorX, pack.AnchorY, pack.Tall, pack.Faces).ShouldBe((40, 50, 20d, 48d, 40d, 1));
        pack.Actions.ShouldBe(["idle", "walk"]);
        var walk = pack.Clips[1];
        walk.Fps.ShouldBe(24);
        walk.Frames.Count.ShouldBe(4);
        walk.Duration.ShouldBe(4 / 24d);

        // Each frame where the file has it.
        var (offset, length) = walk.Frames[2];
        bytes.AsSpan(offset, length).ToArray().ShouldBe(Png(40, 50, 22));
    }

    [Fact]
    public void A_clip_loops_and_an_action_without_one_plays_the_closest()
    {
        var pack = CharacterPack.Read(Pack(clips: [("idle", 10, 5), ("walk", 24, 4), ("play", 12, 6)]))!;
        var idle = pack.ClipFor("idle");
        idle.FrameAt(0).ShouldBe(0);
        idle.FrameAt(0.25).ShouldBe(2);
        idle.FrameAt(0.55).ShouldBe(0, "round again after half a second");
        idle.FrameAt(-0.05).ShouldBe(4, "before the start counts back from the end");
        pack.ClipFor("run").Action.ShouldBe("walk");
        pack.ClipFor("dance").Action.ShouldBe("play");
        pack.ClipFor("jump").Action.ShouldBe("play");
        pack.ClipFor("sleep").Action.ShouldBe("idle");
        pack.ClipFor("sit").Action.ShouldBe("idle");
        CharacterPack.Read(Pack(clips: [("idle", 10, 2), ("run", 24, 2)]))!.ClipFor("walk").Action.ShouldBe("run");
    }

    [Fact]
    public void Only_a_sound_character_is_read()
    {
        CharacterPack.Read(Pack()).ShouldNotBeNull();
        CharacterPack.Read([.. Pack(), 0]).ShouldBeNull("bytes after the last frame");
        CharacterPack.Read(Pack().AsSpan(..^1)).ShouldBeNull("cut short");
        CharacterPack.Read([.. "PCDA"u8, .. Pack().AsSpan(4)]).ShouldBeNull("another kind of file");
        CharacterPack.Read(Pack(clips: [("walk", 24, 2)])).ShouldBeNull("no idle");
        CharacterPack.Read(Pack(clips: [("idle", 24, 2), ("Fly!", 24, 2)])).ShouldBeNull("an action there is not");
        // A new animation (made by the generator) is an action too; its own file (the clip alone, no idle) is an extra.
        CharacterPack.Read(Pack(clips: [("idle", 24, 2), ("wave-hello", 24, 2)])).ShouldNotBeNull();
        CharacterPack.Read(Pack(clips: [("wave", 24, 2)])).ShouldBeNull("a character needs its idle clip");
        CharacterPack.ReadExtra(Pack(clips: [("wave", 24, 2)])).ShouldNotBeNull();
        CharacterPack.ReadExtra(Pack(clips: [("Wave!", 24, 2)])).ShouldBeNull();
        CharacterPack.Read(Pack(clips: [("idle", 24, 2), ("worried", 24, 2)]))!.ClipFor("panic").Action.ShouldBe("idle", "panic without its clip: the closest (no jump here)");
        CharacterPack.Read(Pack(clips: [("idle", 24, 2), ("dance", 24, 2)]))!.ClipFor("cheer").Action.ShouldBe("dance");
        CharacterPack.Read(Pack(clips: [("idle", 24, 2), ("idle", 12, 2)])).ShouldBeNull("the same action twice");
        CharacterPack.Read(Pack(clips: [("idle", 0, 2)])).ShouldBeNull("no frames a second");
        CharacterPack.Read(Pack(clips: [("idle", 24, 301)])).ShouldBeNull("too many frames");
        CharacterPack.Read(Pack(tall: 60)).ShouldBeNull("taller than its frames");
        CharacterPack.Read(Pack(faces: 0)).ShouldBeNull("facing neither way");
        CharacterPack.Read(Pack(width: 2000)).ShouldBeNull("too large");

        // A frame of another size, or not a PNG.
        var odd = CharacterPack.Write("Kit", 40, 50, 20, 48, 40, 1, [("idle", 12, [Png(40, 50), Png(41, 50)])]);
        CharacterPack.Read(odd).ShouldBeNull();
        var jpeg = CharacterPack.Write("Kit", 40, 50, 20, 48, 40, 1, [("idle", 12, [[0xFF, 0xD8, .. new byte[40]]])]);
        CharacterPack.Read(jpeg).ShouldBeNull();
        CharacterPack.Read(CharacterPack.Write("  ", 40, 50, 20, 48, 40, 1, [("idle", 12, [Png(40, 50)])])).ShouldBeNull("no name");
    }

    [Fact]
    public void The_editor_and_the_recorder_read_the_same_file()
    {
        // The editor's test character (written by pack.ts; its test reads it too).
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(folder!.FullName, "web", "src", "features", "overlay", "characters", "test.char")))
        {
            folder = folder.Parent;
        }

        var pack = CharacterPack.Read(File.ReadAllBytes(Path.Combine(folder.FullName, "web", "src", "features", "overlay", "characters", "test.char")))!;
        pack.ShouldNotBeNull();
        (pack.Name, pack.Width, pack.Height, pack.AnchorX, pack.AnchorY, pack.Tall, pack.Faces).ShouldBe(("Test", 6, 12, 3d, 11.5, 9d, -1));
        pack.Clips.Select(c => (c.Action, c.Fps, c.Frames.Count)).ShouldBe([("idle", 4, 2), ("walk", 8, 3)]);
    }

    [Fact]
    public void A_poses_opening_plays_once_then_its_settled_part_forwards_and_back()
    {
        var frames = Enumerable.Range(0, 10).Select(i => (i, 1)).ToList();
        var clip = new CharacterClip("sit", 10, frames) { Loop = 6, End = 8, Bounce = true };
        Enumerable.Range(0, 16).Select(n => clip.FrameAt(n / 10.0)).ShouldBe([0, 1, 2, 3, 4, 5, 6, 7, 8, 7, 6, 7, 8, 7, 6, 7]);
        Enumerable.Range(0, 8).Select(n => new CharacterClip("idle", 10, frames[..4]) { Bounce = true }.FrameAt(n / 10.0)).ShouldBe([0, 1, 2, 3, 2, 1, 0, 1]);
        Enumerable.Range(0, 6).Select(n => new CharacterClip("walk", 10, frames[..4]).FrameAt(n / 10.0)).ShouldBe([0, 1, 2, 3, 0, 1]);

        // Every shipped character's poses settle rather than start over.
        foreach (var (id, (_, pack)) in OverlayCharacters.Shipped)
        {
            // Asleep it holds its calmest frame; sitting settles unless the clip already loops cleanly.
            pack.ClipFor("sleep").Loop.ShouldNotBeNull(id);
        }
    }

    [Fact]
    public void The_shipped_characters_are_embedded_whole()
    {
        OverlayCharacters.ShippedIds.ShouldBe(["mochi", "biscuit", "mikan", "kuma", "yuki", "akari", "hana", "ren", "astro"]);
        OverlayCharacters.Shipped.Keys.ShouldBe(OverlayCharacters.ShippedIds, ignoreOrder: true);
        foreach (var (id, (bytes, pack)) in OverlayCharacters.Shipped)
        {
            pack.Actions.ShouldBe(["idle", "walk", "run", "jump", id == "hana" ? "dance" : "play", "sit", "sleep", "held", "land"], $"{id}'s clips");
            pack.Faces.ShouldBeOneOf(1, -1);
            pack.Tall.ShouldBeOneOf(200, 240);
            pack.AnchorY.ShouldBeGreaterThan(pack.Height * 0.8, "its feet near the bottom (room above for jumps)");
            bytes.Length.ShouldBeLessThan(8 * 1024 * 1024, $"{id} stays small");
            pack.ClipFor("walk").Fps.ShouldBeGreaterThanOrEqualTo(20);
        }

        OverlayCharacters.Shipped["mochi"].Pack.Name.ShouldBe("Mochi");
        OverlayCharacters.Shipped["biscuit"].Pack.Name.ShouldBe("Biscuit");
        OverlayCharacters.ShippedIds.Select(id => OverlayCharacters.Shipped[id].Pack.Name).ShouldBe(["Mochi", "Biscuit", "Mikan", "Kuma", "Yuki", "Akari", "Hana", "Ren", "Astro"]);
        OverlayCharacters.IsCharacterId("mochi").ShouldBeTrue();
        OverlayCharacters.IsCharacterId("0123456789abcdef.char").ShouldBeTrue();
        OverlayCharacters.IsCharacterId("0123456789ABCDEF.char").ShouldBeFalse();
        OverlayCharacters.IsCharacterId("kitty-ginger").ShouldBeFalse();
    }
}
