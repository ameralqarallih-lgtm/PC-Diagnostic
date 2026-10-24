using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;
using PcDiag.Core.Ipc;

namespace PcDiag.Web.Security;

/// <summary>
/// Issues one-time launch tokens. A token is embedded in the "Open dashboard" link and exchanged exactly once
/// for a session cookie. Only SHA-256 hashes of tokens are kept in memory. Tokens issued by the Agent tray arrive
/// as ticket files instead (see <see cref="LaunchTickets"/>).
/// </summary>
public sealed class LaunchTokenService(IOptions<WebOptions> options, TimeProvider timeProvider, DataPaths? paths = null)
{
    /// <summary>Most outstanding (unredeemed, unexpired) tokens kept at once; older ones are dropped.</summary>
    public const int MaxOutstandingTokens = 8;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _outstanding = new(StringComparer.Ordinal);

    /// <summary>Creates a new single-use token.</summary>
    public string IssueToken()
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiresAt = timeProvider.GetUtcNow().AddSeconds(options.Value.LaunchTokenLifetimeSeconds);

        lock (_lock)
        {
            RemoveExpired();
            if (_outstanding.Count >= MaxOutstandingTokens)
            {
                var oldest = _outstanding.MinBy(kv => kv.Value).Key;
                _outstanding.Remove(oldest);
            }

            _outstanding[Hash(token)] = expiresAt;
        }

        return token;
    }

    /// <summary>Consumes a token. Returns true only once per valid, unexpired token.</summary>
    public bool TryRedeem(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return false;
        }

        var hash = Hash(token);
        lock (_lock)
        {
            RemoveExpired();
            if (_outstanding.Remove(hash))
            {
                return true;
            }
        }

        return paths is not null && LaunchTickets.TryRedeem(paths, token, timeProvider.GetUtcNow());
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (hash, expiresAt) in _outstanding.ToList())
        {
            if (expiresAt <= now)
            {
                _outstanding.Remove(hash);
            }
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
