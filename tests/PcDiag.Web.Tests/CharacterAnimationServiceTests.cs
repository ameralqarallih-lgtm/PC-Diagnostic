using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;
using PcDiag.Storage.Files;
using PcDiag.Web.Characters;

namespace PcDiag.Web.Tests;

/// <summary>The new animations made with a generator that answers the character generator's API (here a fake one).</summary>
public sealed class CharacterAnimationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", "anim-" + Guid.NewGuid().ToString("N"));

    public CharacterAnimationServiceTests() => Directory.CreateDirectory(_root);

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

    /// <summary>The generator: takes a file, starts a job, reports it running once, then done with the clip (or refuses).</summary>
    private sealed class FakeGenerator(byte[] clip, bool refuse = false) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        public string? Prompt { get; private set; }

        public int Items { get; private set; }

        private int _looks;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Asked.Add($"{request.Method} {path}");
            HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
            switch (path)
            {
                case "/v1/files" when request.Method == HttpMethod.Post:
                    var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    return body.AsSpan(0, 4).SequenceEqual("PCDC"u8) ? Json(new { file = "up1" }, HttpStatusCode.Created) : Json(new { error = "no" }, HttpStatusCode.BadRequest);
                case "/v1/animations":
                    if (refuse)
                    {
                        return Json(new { error = "no GPU" }, HttpStatusCode.BadRequest);
                    }

                    var items = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("items");
                    Prompt = items[0].GetProperty("prompt").GetString();
                    Items = items.GetArrayLength();
                    return Json(new { job = "j1" }, HttpStatusCode.Accepted);
                case "/v1/jobs/j1":
                    return ++_looks < 2
                        ? Json(new { state = "running", step = "Animating", progress = 0.5 })
                        : Json(new { state = "done", step = "Done", progress = 1.0, characters = Enumerable.Range(0, Items).Select(i => i == 1 ? null : "out1").ToArray(), errors = Enumerable.Range(0, Items).Select(i => i == 1 ? "no frames" : null).ToArray() });
                case "/v1/files/out1":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(clip) };
                case "/v1/health":
                    return Json(new { name = "fake", version = 1 });
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A PNG's signature and header for its size (what a character file's reader checks), and some bytes.</summary>
    private static byte[] Png(int width, int height, int extra)
    {
        var bytes = new byte[33 + extra];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static byte[] Extra(string action) => CharacterPack.Write("Ren", 20, 30, 10, 29, 28, 1, [(action, 20, [Png(20, 30, 20), Png(20, 30, 21)])]);

    private (CharacterAnimationService Service, FakeGenerator Generator) Make(string action, bool refuse = false)
    {
        var paths = new DataPaths(_root);
        var generator = new FakeGenerator(Extra(action), refuse);
        var store = new FileStore(_root, paths.OverlayImagesDirectory, NullLogger.Instance);
        var generation = new CharacterGeneration(paths, new Factory(generator), store, NullLogger<CharacterGeneration>.Instance);
        generation.Save("external", "http://generator.test/", null).ShouldBeNull();
        return (new CharacterAnimationService(paths, generation, NullLogger<CharacterAnimationService>.Instance), generator);
    }

    [Fact]
    public async Task A_new_animation_is_made_for_each_character_one_at_a_time_and_kept_for_the_recorder()
    {
        var (service, generator) = Make("wave");
        service.Create("Wave!", "x", null).Problem.ShouldNotBeNull("a prompt of 3 characters or more");
        service.Create("Run", "The character runs.", null).Problem.ShouldNotBeNull("a built-in action");
        var (made, problem) = service.Create("Wave!", "The character waves hello.", ["ren", "mochi"]);
        problem.ShouldBeNull();
        made!.Id.ShouldBe("wave");
        made.Clips.Keys.ShouldBe(["ren", "mochi"], ignoreOrder: true);
        made.Clips.Values.ShouldAllBe(c => c.State == "queued");

        // Sent together as one job (one load of the video model), followed while it runs, then each clip's file kept in
        // the animations folder; one the generator could not make fails with why.
        await service.StepAsync(CancellationToken.None);
        service.List()[0].Clips.Values.Count(c => c.State == "running").ShouldBe(2);
        (generator.Prompt, generator.Items).ShouldBe(("The character waves hello.", 2));
        generator.Asked.Count(a => a == "POST /v1/animations").ShouldBe(1);
        await service.StepAsync(CancellationToken.None);
        service.List()[0].Clips.Values.ShouldAllBe(c => c.State == "running" && c.Progress == 0.5);
        await service.StepAsync(CancellationToken.None);
        service.List()[0].Clips.Values.Single(c => c.State == "failed").Error.ShouldBe("no frames");
        var first = service.List()[0].Clips.Values.Single(c => c.State == "done");
        File.Exists(Path.Combine(_root, CharacterAnimations.Folder, first.File!)).ShouldBeTrue();
        CharacterAnimations.Read(_root).ShouldHaveSingleItem().Id.ShouldBe("wave", "the recorder reads the same file");
        service.ClipFile("wave", service.List()[0].Clips.Single(c => c.Value.State == "done").Key).ShouldNotBeNull();

        // Removed: its files go too.
        service.Delete("wave").ShouldBeTrue();
        service.List().ShouldBeEmpty();
        Directory.EnumerateFiles(Path.Combine(_root, CharacterAnimations.Folder)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_character_s_clip_is_managed_made_again_on_or_off_or_removed_and_emotions_are_their_own_kind()
    {
        var (service, _) = Make("wave");
        service.Create("Wave", "The character waves hello.", ["ren"]);
        for (var i = 0; i < 3; i++)
        {
            await service.StepAsync(CancellationToken.None);
        }

        var made = service.List()[0].Clips["ren"];
        made.State.ShouldBe("done");
        CharacterAnimations.FilesFor(service.List(), "ren").ShouldBe([("wave", made.File!)]);

        // Off (the clip, then the whole animation): the character does the closest it has; its clip is kept.
        service.SetActive("wave", "ren", false).ShouldBeTrue();
        CharacterAnimations.FilesFor(service.List(), "ren").ShouldBeEmpty();
        service.SetActive("wave", "ren", true);
        service.SetActive("wave", null, false);
        CharacterAnimations.FilesFor(service.List(), "ren").ShouldBeEmpty();
        service.SetActive("wave", null, true);

        // Made again: its last file plays meanwhile.
        service.Regenerate("wave", "ren").ShouldBeTrue();
        var again = service.List()[0].Clips["ren"];
        (again.State, again.File, again.Active).ShouldBe(("queued", made.File, true));
        CharacterAnimations.FilesFor(service.List(), "ren").ShouldHaveSingleItem();
        service.Regenerate("wave", "mochi").ShouldBeTrue("for a character it has no clip of");
        service.List()[0].Clips["mochi"].State.ShouldBe("queued");

        // Edited, then removed for one character (its file goes when nothing else has it).
        service.Edit("wave", "Wave hello", "The character waves hello twice.").ShouldBeNull();
        service.Edit("wave", null, "x").ShouldNotBeNull();
        (service.List()[0].Name, service.List()[0].Prompt).ShouldBe(("Wave hello", "The character waves hello twice."));
        service.RemoveClip("wave", "ren").ShouldBeTrue();
        service.List()[0].Clips.ContainsKey("ren").ShouldBeFalse();
        File.Exists(Path.Combine(_root, CharacterAnimations.Folder, made.File!)).ShouldBeFalse();

        // An emotion is its own kind (those made before kinds: by their ids).
        service.Create("Angry", "The character is angry: frowning, stamping.", ["ren"], kind: "emotion").Animation!.KindOf.ShouldBe("emotion");
        service.List().Single(a => a.Id == "wave").KindOf.ShouldBe("animation");
        new CharacterAnimation("worried", "Worried", "p", DateTimeOffset.UnixEpoch, new Dictionary<string, AnimationClip>()).KindOf.ShouldBe("emotion");
    }

    [Fact]
    public async Task A_refusal_fails_the_clip_and_a_retry_asks_again_and_the_emotions_are_made_with_their_ids()
    {
        var (service, _) = Make("wave", refuse: true);
        service.Create("Wave", "The character waves hello.", ["ren"]);
        await service.StepAsync(CancellationToken.None);
        var failed = service.List()[0].Clips["ren"];
        (failed.State, failed.Error).ShouldBe(("failed", "The generator refused it: {\"error\":\"no GPU\"}"));
        service.Retry("wave").ShouldBeTrue();
        service.List()[0].Clips["ren"].State.ShouldBe("queued");

        service.CreateEmotions(["ren"]).ShouldBeNull();
        service.List().Select(a => a.Id).ShouldBe(["wave", "worried", "panic", "tired", "cheer"]);
        service.List().Single(a => a.Id == "panic").Prompt.ShouldContain("no smile");
    }
}
