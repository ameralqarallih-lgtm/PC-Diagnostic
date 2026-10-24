using System.Security.Cryptography;
using System.Text.Json;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;
using PcDiag.Storage.Files;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// The panels' characters: the shipped ones (embedded in PcDiag.Core) and those the user imported (".char" files of
/// the picture library, added through it: <see cref="OverlayImageEndpoints"/>). Listed for the editor, served to it
/// (an imported one kept only in another storage location is brought here first, where the recorder reads it) and
/// removed (an imported one, while no panel has it).
/// </summary>
internal static class OverlayCharacterEndpoints
{
    private static readonly Lazy<string> EffectsTag = new(() => Convert.ToHexStringLower(SHA256.HashData(OverlayReactions.EffectsPack.Bytes))[..16]);
    private static readonly Lazy<string> PropsTag = new(() => Convert.ToHexStringLower(SHA256.HashData(OverlayReactions.PropsPack.Bytes))[..16]);

    private static readonly Lazy<Dictionary<string, string>> ShippedTags = new(() =>
        OverlayCharacters.Shipped.ToDictionary(s => s.Key, s => Convert.ToHexStringLower(SHA256.HashData(s.Value.Bytes))[..16], StringComparer.Ordinal));

    public static IEndpointRouteBuilder MapOverlayCharacterEndpoints(this IEndpointRouteBuilder app)
    {
        var characters = app.MapGroup("/api/v1/overlay/characters");

        // The reactions' effects (zZ, sweat, a flame, sparks, …), drawn with characters that react to readings.
        app.MapGet("/api/v1/overlay/effects", () =>
            Results.File(OverlayReactions.EffectsPack.Bytes, "application/octet-stream", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{EffectsTag.Value}\"")));

        // The props (an ice pack, a cone, a siren, …), drawn with characters as they react.
        app.MapGet("/api/v1/overlay/props", () =>
            Results.File(OverlayReactions.PropsPack.Bytes, "application/octet-stream", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{PropsTag.Value}\"")));

        // The characters' weather now (null: none), and places by name to take it from (OverlayConfig.Weather).
        app.MapGet("/api/v1/overlay/weather", (PcDiag.Web.Characters.WeatherService weather) => Results.Ok(new WeatherAnswer(weather.Now())));
        app.MapGet("/api/v1/overlay/weather/places", async (string? name, HttpRequest request, PcDiag.Web.Characters.WeatherService weather, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A place's name is needed", detail: "1-120 characters.");
            }

            try
            {
                return Results.Ok(await weather.SearchAsync(name.Trim(), request.Headers.AcceptLanguage.ToString(), ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Places could not be searched", detail: "OpenStreetMap's search did not answer: " + ex.Message);
            }
        });

        // The shipped ones first, then the imported ones, newest first.
        characters.MapGet(string.Empty, async (DataPaths paths, FileStore store, CancellationToken ct) =>
        {
            var used = Used(paths);
            var list = OverlayCharacters.ShippedIds.Select(id =>
            {
                var pack = OverlayCharacters.Shipped[id].Pack;
                return new CharacterInfo(id, pack.Name, true, [.. pack.Actions], used.Contains(id), true, null);
            }).ToList();

            var index = PictureIndex.Read(paths);
            var here = Directory.Exists(paths.OverlayImagesDirectory)
                ? Directory.EnumerateFiles(paths.OverlayImagesDirectory).Select(Path.GetFileName).OfType<string>().Where(OverlayCharacters.IsImportedId).ToHashSet(StringComparer.Ordinal)
                : [];
            // Kept as characters (earlier versions kept them with the pictures).
            var stored = (await store.ListAsync(FileKinds.Characters, ct).ConfigureAwait(false)).Concat(await store.ListAsync(FileKinds.Pictures, ct).ConfigureAwait(false))
                .Where(f => OverlayCharacters.IsImportedId(f.Name)).GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var imported = here.Union(stored.Keys).Select(id =>
            {
                var entry = index.GetValueOrDefault(id);
                var actions = entry?.Actions;
                var name = entry?.Name;
                if ((actions is null || name is null) && here.Contains(id) && Read(paths, id) is { } pack)
                {
                    actions ??= [.. pack.Actions];
                    name ??= pack.Name;
                }

                var added = entry?.Added ?? (here.Contains(id) ? new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(paths.OverlayImagesDirectory, id))) : stored[id].Updated);
                return new CharacterInfo(id, name ?? "Character", false, actions ?? ["idle"], used.Contains(id), here.Contains(id), added);
            }).OrderByDescending(c => c.Added);
            return Results.Ok(list.Concat(imported).ToList());
        });

        // A character's file (named by its content, or a shipped one's by its own: it never changes under a name).
        characters.MapGet("/{id}", async (string id, DataPaths paths, FileStore store, CancellationToken ct) =>
        {
            if (OverlayCharacters.Shipped.TryGetValue(id, out var shipped))
            {
                return Results.File(shipped.Bytes, "application/octet-stream", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{ShippedTags.Value[id]}\""));
            }

            if (!OverlayCharacters.IsImportedId(id))
            {
                return Results.NotFound();
            }

            var path = Path.Combine(paths.OverlayImagesDirectory, id);
            if (!File.Exists(path))
            {
                await using var source = await store.OpenAsync(FileKinds.Characters, id, ct).ConfigureAwait(false) ?? await store.OpenAsync(FileKinds.Pictures, id, ct).ConfigureAwait(false);
                if (source is null)
                {
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Character not found", detail: "No storage location has it (or none can be reached).");
                }

                Directory.CreateDirectory(paths.OverlayImagesDirectory);
                var temp = path + ".tmp";
                await using (var file = File.Create(temp))
                {
                    await source.CopyToAsync(file, ct).ConfigureAwait(false);
                }

                File.Move(temp, path, overwrite: true);
            }

            return Results.File(path, "application/octet-stream", lastModified: File.GetLastWriteTimeUtc(path), entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{id}\""));
        });

        // An imported character removed, here and in every storage location; refused while a panel has it.
        characters.MapDelete("/{id}", async (string id, DataPaths paths, FileStore store, CancellationToken ct) =>
        {
            if (!OverlayCharacters.IsImportedId(id))
            {
                return OverlayCharacters.Shipped.ContainsKey(id)
                    ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A shipped character", detail: "The characters that come with PC Diagnostic stay.")
                    : Results.NotFound();
            }

            if (Used(paths).Contains(id))
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The character is in use", detail: "A panel has it. Take it off that panel first.");
            }

            File.Delete(Path.Combine(paths.OverlayImagesDirectory, id));
            await store.DeleteAsync(FileKinds.Characters, id, ct).ConfigureAwait(false);
            await store.DeleteAsync(FileKinds.Pictures, id, ct).ConfigureAwait(false);
            PictureIndex.Remove(paths, id);
            return Results.NoContent();
        });

        // Character generation: where (settings), and the generations followed in the background.
        var gen = app.MapGroup("/api/v1/overlay/generator");
        gen.MapGet(string.Empty, (PcDiag.Web.Characters.CharacterGeneration g) => Results.Ok(g.Settings()));
        gen.MapPut(string.Empty, (GeneratorChange change, PcDiag.Web.Characters.CharacterGeneration g) =>
            g.Save(change.Kind, change.Url, change.Key, change.Python, change.Folder, change.Kit) is { } problem ? Results.Problem(statusCode: 400, title: "Not saved", detail: problem) : Results.Ok(g.Settings()));
        gen.MapPost("/test", async (HttpContext context, PcDiag.Web.Characters.CharacterGeneration g, CancellationToken ct) =>
        {
            // The choice on the page (not saved yet) when one is sent, else the one saved.
            var choice = context.Request.ContentLength is > 0 ? await context.Request.ReadFromJsonAsync<PcDiag.Web.Characters.GeneratorTry>(ct).ConfigureAwait(false) : null;
            var (ok, message) = await g.TestAsync(ct, choice).ConfigureAwait(false);
            return Results.Ok(new { ok, message });
        });
        // The generator itself: its state, started and stopped here (the one on this PC).
        gen.MapGet("/status", async (PcDiag.Web.Characters.CharacterGeneration g, CancellationToken ct) => Results.Ok(await g.StatusAsync(ct).ConfigureAwait(false)));
        gen.MapPost("/start", async (PcDiag.Web.Characters.CharacterGeneration g, CancellationToken ct) =>
        {
            var (ok, message) = await g.StartGeneratorAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { ok, message });
        });
        gen.MapPost("/stop", async (PcDiag.Web.Characters.CharacterGeneration g, CancellationToken ct) => Results.Ok(new { ok = true, message = await g.StopGeneratorAsync(ct).ConfigureAwait(false) }));
        // Its prerequisites, step by step (this PC only, as every change under /overlay).
        gen.MapGet("/prerequisites", async (PcDiag.Web.Characters.CharacterGeneration g, CancellationToken ct) => Results.Ok(await g.PrerequisitesAsync(ct).ConfigureAwait(false)));
        gen.MapGet("/prerequisites/run", (PcDiag.Web.Characters.CharacterGeneration g) => Results.Ok(g.Setup()));
        gen.MapPost("/prerequisites/{action}", (string action, PcDiag.Web.Characters.CharacterGeneration g) =>
            g.RunSetup(action) is { } problem ? Results.Problem(statusCode: 409, title: "Not started", detail: problem) : Results.Accepted(value: g.Setup()));
        var jobs = app.MapGroup("/api/v1/overlay/generations");
        jobs.MapGet(string.Empty, (PcDiag.Web.Characters.CharacterGeneration g) => Results.Ok(g.Jobs()));
        jobs.MapPost(string.Empty, async (GenerationStart start, PcDiag.Web.Characters.CharacterGeneration g, CancellationToken ct) =>
        {
            var (job, problem) = await g.StartAsync(start.Type, start.Prompt ?? string.Empty, start.Kind is "animal" ? "animal" : "person", start.Name, start.From, start.Still ?? -1, start.Actions, start.Faces is -1 ? -1 : 1, ct).ConfigureAwait(false);
            return job is null ? Results.Problem(statusCode: 400, title: "Not started", detail: problem) : Results.Ok(job);
        });
        jobs.MapGet("/{id}/stills/{n:int}", (string id, int n, PcDiag.Web.Characters.CharacterGeneration g) =>
            g.StillFile(id, n) is { } file ? Results.File(file, "image/png") : Results.NotFound());
        jobs.MapDelete("/{id}", (string id, PcDiag.Web.Characters.CharacterGeneration g) =>
        {
            g.Forget(id);
            return Results.NoContent();
        });

        // New animations made from a prompt for every character (the emotions among them), and each character's clip.
        var animations = app.MapGroup("/api/v1/overlay/animations");
        animations.MapGet(string.Empty, (PcDiag.Web.Characters.CharacterAnimationService a) => Results.Ok(new AnimationsAnswer(a.List(), a.CurrentCharacters())));
        animations.MapPost(string.Empty, (AnimationStart start, PcDiag.Web.Characters.CharacterAnimationService a) =>
            a.Create(start.Name ?? string.Empty, start.Prompt ?? string.Empty, start.Characters, kind: start.Kind is "emotion" ? "emotion" : "animation") is var (made, problem) && made is not null
                ? Results.Ok(made)
                : Results.Problem(statusCode: 400, title: "Not started", detail: problem));
        animations.MapPost("/emotions", (AnimationStart? start, PcDiag.Web.Characters.CharacterAnimationService a) =>
            a.CreateEmotions(start?.Characters) is { } problem ? Results.Problem(statusCode: 400, title: "Not started", detail: problem) : Results.Ok(a.List()));
        animations.MapPost("/{id}/retry", (string id, PcDiag.Web.Characters.CharacterAnimationService a) => a.Retry(id) ? Results.Ok(a.List()) : Results.NotFound());
        animations.MapDelete("/{id}", (string id, PcDiag.Web.Characters.CharacterAnimationService a) => a.Delete(id) ? Results.NoContent() : Results.NotFound());
        // Managed: its name and prompt; made again (for one character or all); on or off (all, or one character's); one character's removed.
        animations.MapPut("/{id}", (string id, AnimationEdit edit, PcDiag.Web.Characters.CharacterAnimationService a) =>
            a.Edit(id, edit.Name, edit.Prompt) is { } problem ? Results.Problem(statusCode: 400, title: "Not changed", detail: problem) : Results.Ok(a.List()));
        animations.MapPost("/{id}/regenerate", (string id, PcDiag.Web.Characters.CharacterAnimationService a) => a.Regenerate(id, null) ? Results.Ok(a.List()) : Results.NotFound());
        animations.MapPost("/{id}/{character}/regenerate", (string id, string character, PcDiag.Web.Characters.CharacterAnimationService a) => a.Regenerate(id, character) ? Results.Ok(a.List()) : Results.NotFound());
        animations.MapPut("/{id}/active", (string id, AnimationActive set, PcDiag.Web.Characters.CharacterAnimationService a) => a.SetActive(id, null, set.Active) ? Results.Ok(a.List()) : Results.NotFound());
        animations.MapPut("/{id}/{character}/active", (string id, string character, AnimationActive set, PcDiag.Web.Characters.CharacterAnimationService a) =>
            a.SetActive(id, character, set.Active) ? Results.Ok(a.List()) : Results.NotFound());
        animations.MapDelete("/{id}/{character}", (string id, string character, PcDiag.Web.Characters.CharacterAnimationService a) => a.RemoveClip(id, character) ? Results.NoContent() : Results.NotFound());
        animations.MapGet("/{id}/{character}", (string id, string character, PcDiag.Web.Characters.CharacterAnimationService a) =>
            a.ClipFile(id, character) is { } file ? Results.File(file, "application/octet-stream", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{Path.GetFileNameWithoutExtension(file)}\"")) : Results.NotFound());

        return app;
    }

    /// <summary>The characters the panels have (landscape and portrait).</summary>
    private static HashSet<string> Used(DataPaths paths) =>
        OverlayStyles.Read(paths.OverlayFile, out _).EveryPanel().SelectMany(p => p.Characters ?? []).Select(c => c.Character).ToHashSet(StringComparer.Ordinal);

    private static CharacterPack? Read(DataPaths paths, string id)
    {
        try
        {
            return CharacterPack.Read(File.ReadAllBytes(Path.Combine(paths.OverlayImagesDirectory, id)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>A character the panels may have.</summary>
/// <param name="Shipped">Comes with PC Diagnostic (otherwise imported by the user).</param>
/// <param name="Actions">What it does (its clips); the others play the closest of these.</param>
/// <param name="Used">A panel has it.</param>
/// <param name="Here">On this PC (where the recorder reads it); otherwise only in another storage location.</param>
/// <param name="Added">When an imported one was added.</param>
public sealed record CharacterInfo(string Id, string Name, bool Shipped, IReadOnlyList<string> Actions, bool Used, bool Here, DateTimeOffset? Added);

/// <summary>Where characters are generated (a null key keeps the saved one, an empty one removes it).</summary>
public sealed record GeneratorChange(string Kind, string? Url, string? Key, string? Python = null, string? Folder = null, string? Kit = null);

/// <summary>A new animation asked for: its name, the prompt, for which characters (null: all of them), and its kind (animation or emotion).</summary>
public sealed record AnimationStart(string? Name, string? Prompt, IReadOnlyList<string>? Characters, string? Kind = null);

/// <summary>An animation's name or prompt changed (null: as it is).</summary>
public sealed record AnimationEdit(string? Name, string? Prompt);

/// <summary>Played or not.</summary>
public sealed record AnimationActive(bool Active);

/// <summary>The new animations, and the characters there are now (those a new one is made for).</summary>
public sealed record AnimationsAnswer(IReadOnlyList<CharacterAnimation> Animations, IReadOnlyList<string> Characters);

/// <summary>A generation to start: stills for a prompt, or a character from a still of a finished stills generation.</summary>
public sealed record GenerationStart(string Type, string? Prompt, string? Kind, string? Name, string? From, int? Still, IReadOnlyList<string>? Actions, int? Faces);

/// <summary>The characters' weather now (null: none fetched, or none wanted).</summary>
public sealed record WeatherAnswer(WeatherReport? Report);
