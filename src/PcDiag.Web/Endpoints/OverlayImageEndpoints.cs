using System.Buffers.Binary;
using System.Security.Cryptography;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// Background pictures for the on-screen panels, and the picture library: stills (PNG, JPEG; the editor converts and
/// shrinks what the user chooses) and animations (".anim": JPEG frames, the editor's conversion of a GIF, WebP or APNG,
/// or a generated scene baked), kept in the data folder by the hash of their content and read by the recorder; copied
/// to the other storage locations that keep pictures. Pictures uploaded by the user stay in the library until they
/// delete them; pictures the editor generates for a panel (art drawn for its layout) are removed once no panel uses them.
/// Imported characters (".char", <see cref="CharacterPack"/>) are added here too and kept alike, but are listed, served
/// and removed as characters (<see cref="OverlayCharacterEndpoints"/>).
/// </summary>
internal static class OverlayImageEndpoints
{
    /// <summary>Largest still accepted.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Largest animation accepted.</summary>
    public const int MaxAnimationBytes = 48 * 1024 * 1024;

    /// <summary>A generated picture not used yet is kept this long (the layout naming it follows).</summary>
    public static readonly TimeSpan Unused = TimeSpan.FromHours(1);

    public static IEndpointRouteBuilder MapOverlayImageEndpoints(this IEndpointRouteBuilder app)
    {
        var images = app.MapGroup("/api/v1/overlay/images");

        // The library: here, and in the other storage locations (a picture only there is brought here when chosen).
        images.MapGet(string.Empty, async (DataPaths paths, PcDiag.Storage.Files.FileStore store, CancellationToken ct) =>
        {
            var index = PictureIndex.Read(paths);
            var config = OverlayStyles.Read(paths.OverlayFile, out _);
            var used = config.EveryPanel().SelectMany(p => new[] { p.Style.Image, p.Style.ArtSource }).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var here = Directory.Exists(paths.OverlayImagesDirectory)
                ? Directory.EnumerateFiles(paths.OverlayImagesDirectory).Select(Path.GetFileName).OfType<string>().Where(OverlayStyles.IsImageId).ToHashSet(StringComparer.Ordinal)
                : [];
            var stored = (await store.ListAsync(PcDiag.Storage.Files.FileKinds.Pictures, ct).ConfigureAwait(false)).Where(f => OverlayStyles.IsImageId(f.Name)).ToDictionary(f => f.Name, StringComparer.Ordinal);
            var list = here.Union(stored.Keys).Select(id =>
            {
                var entry = index.GetValueOrDefault(id);
                var file = Path.Combine(paths.OverlayImagesDirectory, id);
                var size = here.Contains(id) ? new FileInfo(file).Length : stored[id].Size;
                var added = entry?.Added ?? (here.Contains(id) ? new DateTimeOffset(File.GetLastWriteTimeUtc(file)) : stored[id].Updated);
                return new LibraryPicture(id, entry?.Name, entry?.Kind ?? PictureIndex.Upload, entry?.Width ?? 0, entry?.Height ?? 0, entry?.Frames ?? (id.EndsWith(".anim", StringComparison.Ordinal) ? 0 : 1), size, added, used.Contains(id), here.Contains(id));
            }).OrderByDescending(p => p.Added).ToList();
            return Results.Ok(list);
        });

        images.MapPost(string.Empty, async (HttpRequest request, DataPaths paths, PcDiag.Storage.Files.FileStore store, ILogger<OverlayImage> logger, string? name, bool? generated, CancellationToken ct) =>
        {
            if (request.ContentLength is > MaxAnimationBytes)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Picture too large", detail: $"At most {MaxBytes / 1024 / 1024} MB for a picture, {MaxAnimationBytes / 1024 / 1024} MB for an animation.");
            }

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            var animation = OverlayAnimation.Read(bytes);
            var character = animation is null ? CharacterPack.Read(bytes) : null;
            if (bytes.Length > (animation is null && character is null ? MaxBytes : MaxAnimationBytes))
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Picture too large", detail: $"At most {MaxBytes / 1024 / 1024} MB for a picture, {MaxAnimationBytes / 1024 / 1024} MB for an animation.");
            }

            (string Extension, int Width, int Height, int Frames) picture;
            if (animation is not null)
            {
                // Every frame a JPEG of the animation's size.
                if (animation.Frames.Any(f => Describe(bytes.AsSpan(f.Offset, f.Length)) is not ("jpg", var w, var h) || w != animation.Width || h != animation.Height))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not an animation", detail: "Each frame must be a JPEG of the animation's size.");
                }

                picture = ("anim", animation.Width, animation.Height, animation.Frames.Count);
            }
            else if (character is not null)
            {
                // An imported character (its frames were checked as it was read): in the library, not a panel's picture.
                picture = ("char", character.Width, character.Height, character.Clips.Sum(c => c.Frames.Count));
                name = string.IsNullOrWhiteSpace(name) ? character.Name : name;
            }
            else if (Describe(bytes) is { } still)
            {
                picture = (still.Extension, still.Width, still.Height, 1);
            }
            else
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not a picture", detail: "A PNG or JPEG picture, or an animation or a character made by the editor, is required.");
            }

            var id = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + "." + picture.Extension;
            Directory.CreateDirectory(paths.OverlayImagesDirectory);
            var path = Path.Combine(paths.OverlayImagesDirectory, id);
            if (!File.Exists(path))
            {
                var temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }

            // In the library: an upload (kept), or a picture generated for a panel (removed once unused). A picture
            // uploaded before stays an upload.
            PictureIndex.Add(paths, id, new PictureEntry(string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(80, name.Trim().Length)], generated == true ? PictureIndex.Generated : PictureIndex.Upload, picture.Width, picture.Height, picture.Frames, DateTimeOffset.UtcNow, character is null ? null : [.. character.Actions]));

            // An upload is copied to the other storage locations that keep pictures, in the background (a slow service
            // never holds the editor). A generated one is not: the editor makes it again from the panel, at every change.
            if (generated == true)
            {
                return Results.Ok(new OverlayImage(id, picture.Width, picture.Height, picture.Frames));
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await store.MirrorAsync(OverlayCharacters.IsImportedId(id) ? PcDiag.Storage.Files.FileKinds.Characters : PcDiag.Storage.Files.FileKinds.Pictures, id, path, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "The picture {Id} was not copied to the other storage locations", id);
                }
            }, CancellationToken.None);

            return Results.Ok(new OverlayImage(id, picture.Width, picture.Height, picture.Frames));
        });

        // A picture of the library that is only in another storage location: brought here (where the recorder reads it).
        images.MapPost("/{id}/fetch", async (string id, DataPaths paths, PcDiag.Storage.Files.FileStore store, CancellationToken ct) =>
        {
            if (!OverlayStyles.IsImageId(id))
            {
                return Results.NotFound();
            }

            var path = Path.Combine(paths.OverlayImagesDirectory, id);
            if (!File.Exists(path))
            {
                await using var source = await store.OpenAsync(PcDiag.Storage.Files.FileKinds.Pictures, id, ct).ConfigureAwait(false);
                if (source is null)
                {
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Picture not found", detail: "No storage location has it (or none can be reached).");
                }

                Directory.CreateDirectory(paths.OverlayImagesDirectory);
                var temp = path + ".tmp";
                await using (var file = File.Create(temp))
                {
                    await source.CopyToAsync(file, ct).ConfigureAwait(false);
                }

                File.Move(temp, path, overwrite: true);
            }

            return Results.NoContent();
        });

        // Removed from the library, here and in every storage location; refused while a panel uses it.
        images.MapDelete("/{id}", async (string id, DataPaths paths, PcDiag.Storage.Files.FileStore store, CancellationToken ct) =>
        {
            if (!OverlayStyles.IsImageId(id))
            {
                return Results.NotFound();
            }

            var config = OverlayStyles.Read(paths.OverlayFile, out _);
            if (config.EveryPanel().Any(p => p.Style.Image == id || p.Style.ArtSource == id))
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The picture is in use", detail: "A panel shows it. Choose another picture for it first.");
            }

            File.Delete(Path.Combine(paths.OverlayImagesDirectory, id));
            await store.DeleteAsync(PcDiag.Storage.Files.FileKinds.Pictures, id, ct).ConfigureAwait(false);
            PictureIndex.Remove(paths, id);
            return Results.NoContent();
        });

        images.MapGet("/{id}", (string id, DataPaths paths) =>
        {
            var path = Path.Combine(paths.OverlayImagesDirectory, id);
            if (!OverlayStyles.IsImageId(id) || !File.Exists(path))
            {
                return Results.NotFound();
            }

            // Named by its content: it never changes.
            var type = id.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : id.EndsWith(".anim", StringComparison.Ordinal) ? "application/octet-stream" : "image/jpeg";
            return Results.File(path, type, lastModified: File.GetLastWriteTimeUtc(path), entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{id}\""));
        });

        return app;
    }

    /// <summary>
    /// Removes the pictures generated for panels that no panel of <paramref name="config"/> uses (those made within the
    /// last hour stay); pictures uploaded to the library stay.
    /// </summary>
    public static void RemoveUnused(DataPaths paths, OverlayConfig config)
    {
        if (!Directory.Exists(paths.OverlayImagesDirectory))
        {
            return;
        }

        // Pictures of portrait panels too (a picture only those use is kept), and the pictures glass art is drawn over.
        var used = config.EveryPanel().SelectMany(p => new[] { p.Style.Image, p.Style.ArtSource }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var index = PictureIndex.Read(paths);
        foreach (var file in Directory.EnumerateFiles(paths.OverlayImagesDirectory))
        {
            var name = Path.GetFileName(file);
            if (index.GetValueOrDefault(name)?.Kind != PictureIndex.Generated || used.Contains(name) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) <= Unused)
            {
                continue;
            }

            try
            {
                File.Delete(file);
                PictureIndex.Remove(paths, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use (the recorder reads it into memory, so only briefly); next time.
            }
        }
    }

    /// <summary>A character added to the library (a generated one), as an upload is: named by its content, copied to storage.</summary>
    internal static async Task<string> AddToLibraryAsync(byte[] bytes, string name, CharacterPack pack, DataPaths paths, PcDiag.Storage.Files.FileStore store, ILogger logger)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + ".char";
        Directory.CreateDirectory(paths.OverlayImagesDirectory);
        var path = Path.Combine(paths.OverlayImagesDirectory, id);
        if (!File.Exists(path))
        {
            await File.WriteAllBytesAsync(path + ".tmp", bytes).ConfigureAwait(false);
            File.Move(path + ".tmp", path, overwrite: true);
        }

        PictureIndex.Add(paths, id, new PictureEntry(name.Trim()[..Math.Min(80, name.Trim().Length)], PictureIndex.Upload, pack.Width, pack.Height, pack.Clips.Sum(c => c.Frames.Count), DateTimeOffset.UtcNow, [.. pack.Actions]));
        try
        {
            await store.MirrorAsync(OverlayCharacters.IsImportedId(id) ? PcDiag.Storage.Files.FileKinds.Characters : PcDiag.Storage.Files.FileKinds.Pictures, id, path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "The character {Id} was not copied to the other storage locations", id);
        }

        return id;
    }

    /// <summary>The type and size of a PNG or JPEG from its bytes; null for anything else.</summary>
    internal static (string Extension, int Width, int Height)? Describe(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length > 24 && bytes[..8].SequenceEqual(png) && bytes[12..16].SequenceEqual("IHDR"u8))
        {
            var width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
            return width is > 0 and <= 16384 && height is > 0 and <= 16384 ? ("png", width, height) : null;
        }

        if (bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            // Walk the markers to the frame header (SOF0-SOF15, not DHT, JPG or DAC), which holds the size.
            var i = 2;
            while (i + 9 < bytes.Length)
            {
                if (bytes[i] != 0xFF)
                {
                    return null;
                }

                var marker = bytes[i + 1];
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                {
                    i += 2;
                    continue;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 2)..(i + 4)]);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 5)..(i + 7)]);
                    var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 7)..(i + 9)]);
                    return width > 0 && height > 0 ? ("jpg", width, height) : null;
                }

                i += 2 + length;
            }
        }

        return null;
    }
}

/// <summary>An uploaded background picture: its id (for a panel's style), its size in pixels and its frames (1 for a still).</summary>
public sealed record OverlayImage(string Id, int Width, int Height, int Frames = 1);

/// <summary>A picture of the library: what it is, where it is, and whether a panel uses it.</summary>
/// <param name="Kind">upload (kept until deleted) or generated (made for a panel; removed once unused).</param>
/// <param name="Frames">1 for a still; an animation's frames (0 when not known yet).</param>
/// <param name="Here">On this PC (where the recorder reads it); otherwise only in another storage location.</param>
public sealed record LibraryPicture(string Id, string? Name, string Kind, int Width, int Height, int Frames, long Size, DateTimeOffset Added, bool Used, bool Here);

/// <summary>A picture's entry in the library's index.</summary>
/// <param name="Actions">An imported character's: what it does (its clips).</param>
internal sealed record PictureEntry(string? Name, string Kind, int Width, int Height, int Frames, DateTimeOffset Added, IReadOnlyList<string>? Actions = null);

/// <summary>The library's index (storage/pictures.json): names and kinds of the pictures, by id.</summary>
internal static class PictureIndex
{
    public const string Upload = "upload";

    public const string Generated = "generated";

    private static readonly Lock Gate = new();
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string FileOf(DataPaths paths) => Path.Combine(paths.Root, "storage", "pictures.json");

    public static Dictionary<string, PictureEntry> Read(DataPaths paths)
    {
        lock (Gate)
        {
            try
            {
                var file = FileOf(paths);
                return File.Exists(file)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PictureEntry>>(File.ReadAllBytes(file), Json) ?? new(StringComparer.Ordinal)
                    : new(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return new(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Adds a picture (one already there keeps its name if none is given, and stays an upload once it was one).</summary>
    public static void Add(DataPaths paths, string id, PictureEntry entry)
    {
        lock (Gate)
        {
            var index = Read(paths);
            if (index.TryGetValue(id, out var old))
            {
                entry = entry with { Name = entry.Name ?? old.Name, Kind = old.Kind == Upload ? Upload : entry.Kind, Added = old.Added };
            }

            index[id] = entry;
            Save(paths, index);
        }
    }

    public static void Remove(DataPaths paths, string id)
    {
        lock (Gate)
        {
            var index = Read(paths);
            if (index.Remove(id))
            {
                Save(paths, index);
            }
        }
    }

    private static void Save(DataPaths paths, Dictionary<string, PictureEntry> index)
    {
        try
        {
            var file = FileOf(paths);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(index, Json));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next time; a picture not in the index is kept as an upload.
        }
    }
}
