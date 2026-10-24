using PcDiag.Core.Overlay;

namespace PcDiag.Core.Tests;

public sealed class CharacterAnimationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", "animations-" + Guid.NewGuid().ToString("N"));

    public CharacterAnimationsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("Wave hello!", "wave-hello")]
    [InlineData("  Spin  around  ", "spin-around")]
    [InlineData("3 cheers", "a-3-cheers")]
    [InlineData("A very very long animation name indeed", "a-very-very-long-animati")]
    [InlineData("!!!", null)]
    [InlineData("Run", null)]
    [InlineData("x", null)]
    public void A_name_becomes_its_id(string name, string? id) => CharacterAnimations.IdOf(name).ShouldBe(id);

    [Fact]
    public void They_are_kept_in_the_data_folder_and_the_emotions_are_new_animations()
    {
        CharacterAnimations.Read(_root).ShouldBeEmpty("none yet");
        var made = new CharacterAnimation("wave", "Wave", "The character waves.", DateTimeOffset.UnixEpoch,
            new Dictionary<string, AnimationClip> { ["ren"] = new("done", "0123456789abcdef.char"), ["mochi"] = new("failed", Error: "no GPU") });
        CharacterAnimations.Write(_root, [made]);
        var read = CharacterAnimations.Read(_root).ShouldHaveSingleItem();
        (read.Id, read.Name, read.Prompt).ShouldBe(("wave", "Wave", "The character waves."));
        read.Clips["ren"].ShouldBe(new AnimationClip("done", "0123456789abcdef.char"));
        read.Clips["mochi"].Error.ShouldBe("no GPU");
        File.WriteAllText(Path.Combine(_root, CharacterAnimations.FileName), "{ not json");
        CharacterAnimations.Read(_root).ShouldBeEmpty("an unreadable file is none");
        CharacterAnimations.Emotions.Select(e => e.Id).ShouldBe(["worried", "panic", "tired", "cheer"]);
        CharacterAnimations.Emotions.ShouldAllBe(e => OverlayCharacters.IsAnimationId(e.Id) && e.Prompt.Length < 300);
    }
}
