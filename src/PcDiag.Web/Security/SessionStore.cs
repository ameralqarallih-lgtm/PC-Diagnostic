using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace PcDiag.Web.Security;

/// <summary>
/// The session table; created once per process, so sessions survive the Web host restarting itself (to apply a
/// change of network access) but not the process ending.
/// </summary>
public sealed class SessionTable
{
    internal Lock Lock { get; } = new();

    internal Dictionary<string, DateTimeOffset> LastSeen { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// In-memory browser sessions with sliding idle expiry. Sessions end when the Web host process ends.
/// Only SHA-256 hashes of session IDs are kept.
/// </summary>
public sealed class SessionStore(IOptions<WebOptions> options, TimeProvider timeProvider, SessionTable table)
{
    /// <summary>Name of the session cookie.</summary>
    public const string CookieName = "pcdiag_session";

    /// <summary>Most concurrent sessions; the least recently used is evicted beyond this.</summary>
    public const int MaxSessions = 32;

    private readonly Lock _lock = table.Lock;
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = table.LastSeen;

    /// <summary>Creates a session and returns its ID (the cookie value).</summary>
    public string Create()
    {
        var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        lock (_lock)
        {
            RemoveExpired();
            if (_lastSeen.Count >= MaxSessions)
            {
                _lastSeen.Remove(_lastSeen.MinBy(kv => kv.Value).Key);
            }

            _lastSeen[Hash(id)] = timeProvider.GetUtcNow();
        }

        return id;
    }

    /// <summary>Checks a session ID and refreshes its idle timer.</summary>
    public bool Validate(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128)
        {
            return false;
        }

        var hash = Hash(id);
        lock (_lock)
        {
            RemoveExpired();
            if (!_lastSeen.ContainsKey(hash))
            {
                return false;
            }

            _lastSeen[hash] = timeProvider.GetUtcNow();
            return true;
        }
    }

    /// <summary>Ends a session.</summary>
    public void Revoke(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        lock (_lock)
        {
            _lastSeen.Remove(Hash(id));
        }
    }

    private void RemoveExpired()
    {
        var cutoff = timeProvider.GetUtcNow().AddHours(-options.Value.SessionIdleHours);
        foreach (var (hash, lastSeen) in _lastSeen.ToList())
        {
            if (lastSeen <= cutoff)
            {
                _lastSeen.Remove(hash);
            }
        }
    }

    private static string Hash(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
}
