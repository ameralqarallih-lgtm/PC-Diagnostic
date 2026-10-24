using System.Buffers.Text;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PcDiag.Core.Configuration;

namespace PcDiag.Core.Ipc;

/// <summary>
/// One-time dashboard launch tickets for the tray's "Open dashboard". The Agent has no HTTP code, so it cannot ask
/// the Web host for a launch token; instead it writes a ticket file named after the SHA-256 of a random token into
/// the per-user data folder and opens a link that carries the token. The Web host redeems a token exactly once by
/// atomically claiming (renaming) the file. The token itself is never written to disk.
/// </summary>
public static class LaunchTickets
{
    /// <summary>How long a ticket can be redeemed.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>Longest accepted token (the issued ones are 43 characters).</summary>
    public const int MaxTokenLength = 128;

    /// <summary>Creates a ticket and returns its token.</summary>
    public static string Issue(DataPaths paths, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.LaunchDirectory);
        Prune(paths, now);

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var file = Path.Combine(paths.LaunchDirectory, FileName(token));
        var temp = file + ".tmp";
        File.WriteAllText(temp, (now + Lifetime).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        File.Move(temp, file);
        return token;
    }

    /// <summary>Redeems a token: true once for an unexpired ticket written by this account (or an administrator).</summary>
    public static bool TryRedeem(DataPaths paths, string? token, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        var file = Path.Combine(paths.LaunchDirectory, FileName(token));
        var claimed = $"{file}.{Guid.NewGuid():N}.claimed";
        try
        {
            // The rename succeeds for exactly one caller, so a ticket cannot be redeemed twice.
            File.Move(file, claimed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows() && !HasTrustedOwner(claimed))
            {
                return false;
            }

            var text = File.ReadAllText(claimed);
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresMs)
                   && now.ToUnixTimeMilliseconds() < expiresMs
                   && expiresMs <= (now + Lifetime + TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDelete(claimed);
        }
    }

    /// <summary>The ticket file name: lowercase hex SHA-256 of the token (never contains path characters).</summary>
    internal static string FileName(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Only tickets owned by this account, the Administrators group (an elevated Agent) or SYSTEM count, so another
    /// local account cannot plant a ticket even when the data folder is in a shared location.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool HasTrustedOwner(string path)
    {
        var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        using var identity = WindowsIdentity.GetCurrent();
        return owner is not null
               && (owner == identity.User
                   || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                   || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
    }

    private static void Prune(DataPaths paths, DateTimeOffset now)
    {
        foreach (var file in Directory.EnumerateFiles(paths.LaunchDirectory))
        {
            if (File.GetLastWriteTimeUtc(file) < now.UtcDateTime - (2 * Lifetime))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pruned on the next issue.
        }
    }
}
