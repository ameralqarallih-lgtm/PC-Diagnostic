using System.Security.Cryptography;
using System.Text.Json;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;
using PcDiag.Storage.Files;

namespace PcDiag.Web;

/// <summary>
/// Keeps the app's own files in the storage locations as they are set (the live files stay on this PC, where the
/// recorder and the Web host read them; the locations keep copies, brought up to date every few minutes):
/// <list type="bullet">
/// <item>the characters: those that come with PC Diagnostic, and those imported or generated (earlier versions kept
/// them with the pictures);</item>
/// <item>settings and layouts: settings.json, overlay.json, dashboard.json, event-rules.json, expected-hardware.json
/// (never the paired devices' keys or the network certificate);</item>
/// <item>the period diagnostics' reports, the problem reports and the character generator's drafts (a copy whose file
/// was deleted here is deleted from the locations too).</item>
/// </list>
/// A file is copied again only when its content changed (its SHA-256 is remembered in the store's folder).
/// </summary>
internal sealed partial class StoreKeeper(FileStore store, DataPaths paths, ILogger<StoreKeeper> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string StateFile => Path.Combine(paths.Root, "storage", "kept.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A little after start: the locations and the recorder come up first.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        var shippedDone = false;
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                if (!shippedDone)
                {
                    await KeepShippedCharactersAsync(stoppingToken).ConfigureAwait(false);
                    shippedDone = true;
                }

                await KeepAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>The files kept now, by kind: their name in the store and their path here.</summary>
    internal IReadOnlyList<(string Kind, string Name, string Path)> Wanted()
    {
        var files = new List<(string, string, string)>();
        foreach (var file in new[] { paths.SettingsFile, paths.OverlayFile, paths.DashboardFile, paths.EventRulesFile, paths.ExpectedHardwareFile })
        {
            if (File.Exists(file))
            {
                files.Add((FileKinds.Settings, Path.GetFileName(file), file));
            }
        }

        // Imported and generated characters (earlier versions kept them with the pictures).
        files.AddRange(In(FileKinds.Characters, paths.OverlayImagesDirectory, "*.char", f => Path.GetFileName(f)).Where(f => OverlayCharacters.IsImportedId(f.Item2)));
        files.AddRange(In(FileKinds.Diagnostics, paths.DiagnosticsDirectory, "*.json", f => Path.GetFileName(f)));
        files.AddRange(In(FileKinds.Reports, Path.Combine(paths.ReportsDirectory, "outbox"), "*.json", f => Path.GetFileName(f)));

        // Drafts: generations/<job>/still-<n>.png kept as <job>-still-<n>.png.
        files.AddRange(In(FileKinds.Drafts, Path.Combine(paths.Root, "characters", "generations"), "still-*.png", f => $"{Path.GetFileName(Path.GetDirectoryName(f))}-{Path.GetFileName(f)}", recurse: true));
        return [.. files.Where(f => FileKinds.IsName(f.Item2))];
    }

    private static IEnumerable<(string, string, string)> In(string kind, string folder, string pattern, Func<string, string> name, bool recurse = false) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, pattern, recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Select(f => (kind, name(f), f))
            : [];

    private async Task KeepAllAsync(CancellationToken cancellationToken)
    {
        var kept = ReadState();
        var wanted = Wanted();
        var changed = false;
        foreach (var (kind, name, path) in wanted)
        {
            string sha;
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            }
            catch (IOException)
            {
                continue;
            }

            var key = $"{kind}/{name}";
            if (kept.TryGetValue(key, out var was) && was == sha)
            {
                continue;
            }

            // A copy in every location that keeps the kind (This PC's own copy is the file itself).
            await store.MirrorAsync(kind, name, path, cancellationToken).ConfigureAwait(false);
            kept[key] = sha;
            changed = true;
        }

        // Diagnostics, problem reports and drafts deleted here: deleted from the locations too.
        var present = wanted.Select(w => $"{w.Kind}/{w.Name}").ToHashSet(StringComparer.Ordinal);
        foreach (var gone in kept.Keys.Where(k => !present.Contains(k) && !k.StartsWith(FileKinds.Settings + "/", StringComparison.Ordinal)).ToList())
        {
            var at = gone.IndexOf('/', StringComparison.Ordinal);
            await store.DeleteAsync(gone[..at], gone[(at + 1)..], cancellationToken).ConfigureAwait(false);
            kept.Remove(gone);
            changed = true;
        }

        if (changed)
        {
            WriteState(kept);
        }
    }

    /// <summary>
    /// The characters that come with PC Diagnostic, kept in the other locations that keep characters (named
    /// &lt;id&gt;.char; on This PC they are part of the program itself).
    /// </summary>
    private async Task KeepShippedCharactersAsync(CancellationToken cancellationToken)
    {
        var there = (await store.ListAsync(FileKinds.Characters, cancellationToken).ConfigureAwait(false)).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, (bytes, _)) in OverlayCharacters.Shipped)
        {
            var name = id + ".char";
            if (there.Contains(name))
            {
                continue;
            }

            var temp = Path.Combine(Path.GetTempPath(), $"pcdiag-{Guid.NewGuid():N}.char");
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
                await store.MirrorAsync(FileKinds.Characters, name, temp, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(temp);
            }
        }
    }

    private Dictionary<string, string> ReadState()
    {
        try
        {
            return File.Exists(StateFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StateFile), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private void WriteState(Dictionary<string, string> kept)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile + ".tmp", JsonSerializer.Serialize(kept, Json));
        File.Move(StateFile + ".tmp", StateFile, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The app's files could not all be kept in the storage locations; tried again shortly")]
    private static partial void LogFailed(ILogger logger, Exception exception);

}
