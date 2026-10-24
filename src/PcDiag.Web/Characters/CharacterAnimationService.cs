using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;

namespace PcDiag.Web.Characters;

/// <summary>
/// The new animations (Settings › Characters › Animations): each made from a prompt for every character by the
/// character generator (its <c>/v1/animations</c>: the character's file in, its clip alone out, at its height), kept
/// in <c>animations.json</c> with each character's clip in the animations folder, where the recorder reads them. The
/// clips waiting are sent together as one job (the generator loads its video model once for them all), followed every
/// few seconds; they wait while there is no generator (and go on once there is one). The emotions are new animations with prompts of their own (<see cref="CharacterAnimations.Emotions"/>).
/// </summary>
public sealed partial class CharacterAnimationService(DataPaths paths, CharacterGeneration generation, ILogger<CharacterAnimationService> logger) : BackgroundService
{
    private readonly Lock _gate = new();

    private string ClipsFolder => Path.Combine(paths.Root, CharacterAnimations.Folder);

    /// <summary>The new animations, newest last.</summary>
    public IReadOnlyList<CharacterAnimation> List()
    {
        lock (_gate)
        {
            return CharacterAnimations.Read(paths.Root);
        }
    }

    /// <summary>The characters there are now: those shipped, and those imported that are on this PC.</summary>
    public IReadOnlyList<string> CurrentCharacters()
    {
        IEnumerable<string> imported = Directory.Exists(paths.OverlayImagesDirectory)
            ? Directory.EnumerateFiles(paths.OverlayImagesDirectory).Select(Path.GetFileName).OfType<string>().Where(OverlayCharacters.IsImportedId).Order(StringComparer.Ordinal)
            : [];
        return [.. OverlayCharacters.ShippedIds, .. imported];
    }

    /// <summary>
    /// A new animation from a prompt, for <paramref name="characters"/> (null: every character there is now); an
    /// animation of that name already there is made for the characters it does not have yet. Its id, or the problem.
    /// </summary>
    /// <param name="id">Its id (null: made from its name).</param>
    /// <param name="kind">animation or emotion.</param>
    public (CharacterAnimation? Animation, string? Problem) Create(string name, string prompt, IReadOnlyList<string>? characters, string? id = null, string kind = "animation")
    {
        id ??= CharacterAnimations.IdOf(name);
        if (id is null || !OverlayCharacters.IsAnimationId(id))
        {
            return (null, "Name it with letters or digits (not one of the built-in actions, such as Run).");
        }

        prompt = (prompt ?? string.Empty).Trim();
        if (prompt.Length is < 3 or > 300)
        {
            return (null, "Describe the animation in 3-300 characters.");
        }

        var wanted = (characters ?? CurrentCharacters()).Where(OverlayCharacters.IsCharacterId).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return (null, "There is no character to make it for.");
        }

        lock (_gate)
        {
            var all = CharacterAnimations.Read(paths.Root).ToList();
            var at = all.FindIndex(a => a.Id == id);
            if (at < 0 && all.Count >= CharacterAnimations.Max)
            {
                return (null, $"There are {CharacterAnimations.Max} animations already: remove one first.");
            }

            var clips = new Dictionary<string, AnimationClip>(at < 0 ? [] : all[at].Clips, StringComparer.Ordinal);
            foreach (var c in wanted.Where(c => !clips.TryGetValue(c, out var clip) || clip.State == "failed"))
            {
                clips[c] = new AnimationClip("queued", Step: "Waiting for its turn");
            }

            var animation = at < 0
                ? new CharacterAnimation(id, name.Trim()[..Math.Min(40, name.Trim().Length)], prompt, DateTimeOffset.UtcNow, clips, kind is "emotion" ? "emotion" : "animation")
                : all[at] with { Clips = clips };
            if (at < 0)
            {
                all.Add(animation);
            }
            else
            {
                all[at] = animation;
            }

            CharacterAnimations.Write(paths.Root, all);
            LogCreated(logger, id, clips.Count(c => c.Value.State == "queued"));
            return (animation, null);
        }
    }

    /// <summary>The emotions (worried, panic, tired, cheer) for <paramref name="characters"/> (null: every character there is now).</summary>
    public string? CreateEmotions(IReadOnlyList<string>? characters)
    {
        foreach (var (id, name, prompt) in CharacterAnimations.Emotions)
        {
            if (Create(name, prompt, characters, id, "emotion").Problem is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>Its failed clips made again (and those of characters it does not have yet); false when there is no such animation.</summary>
    public bool Retry(string id)
    {
        CharacterAnimation? animation;
        lock (_gate)
        {
            animation = CharacterAnimations.Read(paths.Root).FirstOrDefault(a => a.Id == id);
        }

        return animation is not null && Create(animation.Name, animation.Prompt, null, animation.Id, animation.KindOf).Problem is null;
    }

    /// <summary>
    /// Made again: one character's clip (made for it when it has none), or every character's (null), and for the
    /// characters there are now that do not have it. A clip made again plays its last file until the new one is made.
    /// </summary>
    public bool Regenerate(string id, string? character)
    {
        lock (_gate)
        {
            var all = CharacterAnimations.Read(paths.Root).ToList();
            var at = all.FindIndex(a => a.Id == id);
            if (at < 0 || (character is not null && !OverlayCharacters.IsCharacterId(character)))
            {
                return false;
            }

            var clips = new Dictionary<string, AnimationClip>(all[at].Clips, StringComparer.Ordinal);
            foreach (var c in character is null ? clips.Keys.Union(CurrentCharacters()).ToList() : [character])
            {
                clips[c] = clips.TryGetValue(c, out var clip)
                    ? clip with { State = "queued", Step = "Waiting for its turn", Progress = 0, Error = null, Job = null }
                    : new AnimationClip("queued", Step: "Waiting for its turn");
            }

            all[at] = all[at] with { Clips = clips };
            CharacterAnimations.Write(paths.Root, all);
            return true;
        }
    }

    /// <summary>One character's clip removed (its file too): it does the closest it has.</summary>
    public bool RemoveClip(string id, string character)
    {
        lock (_gate)
        {
            var all = CharacterAnimations.Read(paths.Root).ToList();
            var at = all.FindIndex(a => a.Id == id);
            if (at < 0 || !all[at].Clips.ContainsKey(character))
            {
                return false;
            }

            all[at] = all[at] with { Clips = all[at].Clips.Where(c => c.Key != character).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal) };
            CharacterAnimations.Write(paths.Root, all);
            ForgetUnused(all);
            return true;
        }
    }

    /// <summary>Played or not: the whole animation (character null) or one character's clip (kept either way).</summary>
    public bool SetActive(string id, string? character, bool active)
    {
        lock (_gate)
        {
            var all = CharacterAnimations.Read(paths.Root).ToList();
            var at = all.FindIndex(a => a.Id == id);
            if (at < 0 || (character is not null && !all[at].Clips.ContainsKey(character)))
            {
                return false;
            }

            all[at] = character is null
                ? all[at] with { Active = active }
                : all[at] with { Clips = new Dictionary<string, AnimationClip>(all[at].Clips, StringComparer.Ordinal) { [character] = all[at].Clips[character] with { Active = active } } };
            CharacterAnimations.Write(paths.Root, all);
            return true;
        }
    }

    /// <summary>Its name or its prompt changed (the clips made stay until they are made again); the problem, or null.</summary>
    public string? Edit(string id, string? name, string? prompt)
    {
        if (prompt is not null && prompt.Trim().Length is < 3 or > 300)
        {
            return "Describe it in 3-300 characters.";
        }

        if (name is not null && name.Trim().Length is < 1 or > 40)
        {
            return "Name it in 1-40 characters.";
        }

        lock (_gate)
        {
            var all = CharacterAnimations.Read(paths.Root).ToList();
            var at = all.FindIndex(a => a.Id == id);
            if (at < 0)
            {
                return "There is no such animation.";
            }

            all[at] = all[at] with { Name = name?.Trim() ?? all[at].Name, Prompt = prompt?.Trim() ?? all[at].Prompt };
            CharacterAnimations.Write(paths.Root, all);
            return null;
        }
    }

    /// <summary>Removes an animation and its clips' files (characters doing it then idle, or the closest they have).</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            var all = CharacterAnimations.Read(paths.Root).ToList();
            if (all.RemoveAll(a => a.Id == id) == 0)
            {
                return false;
            }

            CharacterAnimations.Write(paths.Root, all);
            ForgetUnused(all);
            return true;
        }
    }

    /// <summary>A character's clip of an animation (its file), or null.</summary>
    public string? ClipFile(string id, string character)
    {
        var clip = List().FirstOrDefault(a => a.Id == id)?.Clips.GetValueOrDefault(character);
        return clip is { File: { } file } && OverlayCharacters.IsImportedId(file) && Path.Combine(ClipsFolder, file) is var path && File.Exists(path) ? path : null;
    }

    private void ForgetUnused(IReadOnlyList<CharacterAnimation> all)
    {
        if (!Directory.Exists(ClipsFolder))
        {
            return;
        }

        var kept = all.SelectMany(a => a.Clips.Values).Select(c => c.File).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(ClipsFolder, "*.char").Where(f => !kept.Contains(Path.GetFileName(f))))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Changes one character's clip of an animation (as it is now on disk: the page may have changed others).</summary>
    private void Update(string id, string character, Func<AnimationClip, AnimationClip> change)
    {
        lock (_gate)
        {
            var all = CharacterAnimations.Read(paths.Root).ToList();
            var at = all.FindIndex(a => a.Id == id);
            if (at < 0 || !all[at].Clips.TryGetValue(character, out var clip))
            {
                return;
            }

            var clips = new Dictionary<string, AnimationClip>(all[at].Clips, StringComparer.Ordinal) { [character] = change(clip) };
            all[at] = all[at] with { Clips = clips };
            CharacterAnimations.Write(paths.Root, all);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFollowFailed(logger, ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>The most clips sent in one job (the generator loads its video model once for them all).</summary>
    public const int Batch = 64;

    /// <summary>One look: the clips being made followed, or those waiting sent together.</summary>
    internal async Task StepAsync(CancellationToken ct)
    {
        var all = List();
        var clips = all.SelectMany(a => a.Clips.Select(c => (Animation: a, Character: c.Key, Clip: c.Value))).ToList();
        var running = clips.Where(c => c.Clip.State == "running" && c.Clip.Job is not null).ToList();
        var waiting = running.Count == 0 ? clips.Where(c => c.Clip.State == "queued").Take(Batch).ToList() : [];
        if (running.Count == 0 && waiting.Count == 0)
        {
            return;
        }

        var (client, problem) = await generation.GeneratorAsync(ct).ConfigureAwait(false);
        if (client is null)
        {
            foreach (var (a, c, _) in waiting.Concat(running))
            {
                Update(a.Id, c, x => x with { Step = problem });
            }

            return;
        }

        using (client)
        {
            try
            {
                if (running.Count > 0)
                {
                    // One job at a time: the one of the first clip under way, with every clip in it.
                    var job = JobOf(running[0].Clip.Job!).Job;
                    await FollowAsync(client, job, [.. running.Where(r => JobOf(r.Clip.Job!).Job == job).Select(r => (r.Animation, r.Character, JobOf(r.Clip.Job!).Index))], ct).ConfigureAwait(false);
                }
                else
                {
                    await SendAsync(client, waiting.Select(w => (w.Animation, w.Character)).ToList(), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or IOException)
            {
                foreach (var (a, c, _) in waiting.Concat(running))
                {
                    Update(a.Id, c, x => x with { Step = "The generator does not answer; trying again" });
                }
            }
        }
    }

    /// <summary>A clip's place in the generator's job: "JOB#INDEX" (one job makes many clips).</summary>
    private static (string Job, int Index) JobOf(string job) =>
        job.LastIndexOf('#') is var at and > 0 && int.TryParse(job[(at + 1)..], out var index) ? (job[..at], index) : (job, 0);

    /// <summary>Clips waiting sent to the generator as one job: each character's file once, then every clip asked for.</summary>
    private async Task SendAsync(HttpClient client, IReadOnlyList<(CharacterAnimation Animation, string Character)> wanted, CancellationToken ct)
    {
        var uploaded = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<(CharacterAnimation Animation, string Character, string File)>();
        foreach (var (animation, character) in wanted)
        {
            if (!uploaded.TryGetValue(character, out var file))
            {
                byte[]? bytes = OverlayCharacters.Shipped.TryGetValue(character, out var shipped) ? shipped.Bytes
                    : Path.Combine(paths.OverlayImagesDirectory, character) is var path && File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
                if (bytes is null)
                {
                    Update(animation.Id, character, c => c with { State = "failed", Error = "The character is not on this PC any more." });
                    continue;
                }

                using var upload = new ByteArrayContent(bytes);
                upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                var sent = await client.PostAsync("v1/files", upload, ct).ConfigureAwait(false);
                if (!sent.IsSuccessStatusCode)
                {
                    Update(animation.Id, character, c => c with { State = "failed", Error = "This generator does not make new animations (update it)." });
                    continue;
                }

                file = (await sent.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false)).GetProperty("file").GetString()!;
                uploaded[character] = file;
            }

            items.Add((animation, character, file));
        }

        if (items.Count == 0)
        {
            return;
        }

        var body = new { items = items.Select(i => new { character = i.File, name = i.Animation.Id, prompt = i.Animation.Prompt }) };
        using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        var started = await client.PostAsync("v1/animations", content, ct).ConfigureAwait(false);
        if (!started.IsSuccessStatusCode)
        {
            var why = await started.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            foreach (var i in items)
            {
                Update(i.Animation.Id, i.Character, c => c with { State = "failed", Error = $"The generator refused it: {why}" });
            }

            return;
        }

        var job = (await started.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false)).GetProperty("job").GetString();
        for (var n = 0; n < items.Count; n++)
        {
            var index = n;
            Update(items[n].Animation.Id, items[n].Character, c => c with { State = "running", Job = $"{job}#{index}", Step = "Sent to the generator", Progress = 0, Error = null });
        }
    }

    /// <summary>A job under way: its progress for each of its clips, and each clip's file once done (checked, then kept in the animations folder).</summary>
    private async Task FollowAsync(HttpClient client, string job, IReadOnlyList<(CharacterAnimation Animation, string Character, int Index)> clips, CancellationToken ct)
    {
        var remote = await client.GetFromJsonAsync<JsonElement>($"v1/jobs/{job}", ct).ConfigureAwait(false);
        var state = remote.GetProperty("state").GetString();
        if (state == "failed")
        {
            var error = remote.TryGetProperty("error", out var e) ? e.GetString() : null;
            foreach (var (a, c, _) in clips)
            {
                Update(a.Id, c, x => x with { State = "failed", Error = error ?? "The generator could not make it.", Job = null });
            }

            return;
        }

        if (state != "done")
        {
            var step = remote.GetProperty("step").GetString();
            var progress = remote.GetProperty("progress").GetDouble();
            foreach (var (a, c, _) in clips)
            {
                Update(a.Id, c, x => x with { Step = step, Progress = progress });
            }

            return;
        }

        // Each clip's file by its place in the job (a generator answering for one clip says "character").
        var files = remote.TryGetProperty("characters", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.String ? f.GetString() : null)]
            : new List<string?> { remote.GetProperty("character").GetString() };
        var errors = remote.TryGetProperty("errors", out var why) && why.ValueKind == JsonValueKind.Array ? [.. why.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.String ? f.GetString() : null)] : new List<string?>();
        foreach (var (animation, character, index) in clips)
        {
            if (index >= files.Count || files[index] is not { } file)
            {
                var error = index < errors.Count ? errors[index] : null;
                Update(animation.Id, character, c => c with { State = "failed", Error = error ?? "The generator could not make it.", Job = null });
                continue;
            }

            var bytes = await client.GetByteArrayAsync($"v1/files/{file}", ct).ConfigureAwait(false);
            if (CharacterPack.ReadExtra(bytes) is not { } pack || pack.Clips.All(c => c.Action != animation.Id))
            {
                Update(animation.Id, character, c => c with { State = "failed", Error = "The generator's clip is not one PC Diagnostic plays.", Job = null });
                continue;
            }

            Directory.CreateDirectory(ClipsFolder);
            var name = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + ".char";
            var path = Path.Combine(ClipsFolder, name);
            await File.WriteAllBytesAsync(path + ".tmp", bytes, ct).ConfigureAwait(false);
            File.Move(path + ".tmp", path, overwrite: true);
            Update(animation.Id, character, c => c with { State = "done", File = name, Step = "Made", Progress = 1, Error = null, Job = null });
            LogMade(logger, animation.Id, character);
            // A clip made again: its last file goes.
            lock (_gate)
            {
                ForgetUnused(CharacterAnimations.Read(paths.Root));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Animation {Id} asked for {Count} characters")]
    private static partial void LogCreated(ILogger logger, string id, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Animation {Id} made for {Character}")]
    private static partial void LogMade(ILogger logger, string id, string character);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Following the new animations failed; trying again")]
    private static partial void LogFollowFailed(ILogger logger, Exception exception);
}
