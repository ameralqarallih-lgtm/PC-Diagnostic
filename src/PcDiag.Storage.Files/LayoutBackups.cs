using System.Globalization;
using Microsoft.Extensions.Logging;

namespace PcDiag.Storage.Files;

/// <summary>
/// The on-screen layout's backups, "overlay-yyyyMMdd-HHmmss-fff.json" (UTC), in the locations that keep backups: kept
/// at most every 5 minutes unless asked, never the same content as the newest, the newest 50. Kept by the Web host
/// before a save and before bringing one back, and by the storage host when the layout changes outside the editor.
/// </summary>
public static partial class LayoutBackups
{
    /// <summary>A backup's name starts so (then its time, UTC).</summary>
    public const string Prefix = "overlay-";

    /// <summary>The newest backups kept.</summary>
    public const int Kept = 50;

    /// <summary>How often a backup is kept while the layout keeps changing.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    /// <summary>Whether a name is a backup's.</summary>
    public static bool IsName(string? name) =>
        name is not null && name.StartsWith(Prefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal) && FileKinds.IsName(name);

    /// <summary>The backups, newest first.</summary>
    public static async Task<IReadOnlyList<StoredFile>> ListAsync(FileStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return [.. (await store.ListAsync(FileKinds.Backups, cancellationToken).ConfigureAwait(false)).Where(f => IsName(f.Name)).OrderByDescending(f => f.Name, StringComparer.Ordinal)];
    }

    /// <summary>Keeps the layout file as it is now; false when it was not kept (no layout, too soon, or the same as the newest).</summary>
    public static async Task<bool> KeepFileAsync(FileStore store, string layoutFile, bool always, ILogger logger, CancellationToken cancellationToken)
    {
        if (await ReadAsync(layoutFile, cancellationToken).ConfigureAwait(false) is not { } content)
        {
            return false;
        }

        return await KeepAsync(store, content, always, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a layout's content as a backup; false when it was not kept (too soon unless <paramref name="always"/>, or
    /// the same as the newest). A backup that cannot be kept never stops anything: it is logged.
    /// </summary>
    public static async Task<bool> KeepAsync(FileStore store, byte[] content, bool always, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            var backups = await ListAsync(store, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (backups.Count > 0)
            {
                if (!always && now - TimeOf(backups[0]) < Every)
                {
                    return false;
                }

                if (await SameAsync(store, backups[0].Name, content, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            using (var stream = new MemoryStream(content, writable: false))
            {
                await store.PutAsync(FileKinds.Backups, $"{Prefix}{now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.json", stream, cancellationToken).ConfigureAwait(false);
            }

            foreach (var old in backups.Skip(Kept - 1))
            {
                await store.DeleteAsync(FileKinds.Backups, old.Name, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            LogNotKept(logger, ex);
            return false;
        }
    }

    /// <summary>The layout file's content (read while the recorder may be replacing it); null when there is none.</summary>
    internal static async Task<byte[]?> ReadAsync(string layoutFile, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(layoutFile))
            {
                return null;
            }

            await using var file = new FileStream(layoutFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            using var copy = new MemoryStream();
            await file.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            return copy.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A backup's time, from its name (else when it was written).</summary>
    private static DateTimeOffset TimeOf(StoredFile file) =>
        DateTimeOffset.TryParseExact(file.Name[Prefix.Length..^".json".Length], "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : file.Updated;

    private static async Task<bool> SameAsync(FileStore store, string name, byte[] content, CancellationToken cancellationToken)
    {
        await using var newest = await store.OpenAsync(FileKinds.Backups, name, cancellationToken).ConfigureAwait(false);
        if (newest is null)
        {
            return false;
        }

        using var copy = new MemoryStream();
        await newest.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.GetBuffer().AsSpan(0, (int)copy.Length).SequenceEqual(content);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The layout's backup was not kept")]
    private static partial void LogNotKept(ILogger logger, Exception exception);
}
