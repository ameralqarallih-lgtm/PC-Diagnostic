using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;

namespace PcDiag.Web.Security;

/// <summary>A device paired for access from the local network.</summary>
/// <param name="Id">Short ID used to revoke it.</param>
/// <param name="Name">What the user called it (or the browser's description).</param>
/// <param name="PairedAt">When it was paired.</param>
/// <param name="LastSeenAt">When it was last used (updated at most every few minutes).</param>
/// <param name="LastAddress">The address it last connected from.</param>
public sealed record PairedDevice(string Id, string Name, DateTimeOffset PairedAt, DateTimeOffset LastSeenAt, string? LastAddress);

/// <summary>
/// Paired devices, kept in devices.json in the data folder with this install's ID (which the companion app uses to
/// recognise this PC after its address changes). Only SHA-256 hashes of device tokens are stored; the token itself
/// lives only on the device: in a cookie for browsers, and sent as the <see cref="HeaderName"/> header by the
/// companion app. A device that has not been used for <see cref="LanOptions.DeviceIdleDays"/> must pair again.
/// </summary>
public sealed class PairedDevices(DataPaths paths, IOptionsMonitor<WebOptions> options, TimeProvider timeProvider)
{
    /// <summary>Name of the device cookie.</summary>
    public const string CookieName = "pcdiag_device";

    /// <summary>Header the companion app sends its token in. A custom header cannot be sent by another site's page.</summary>
    public const string HeaderName = "X-PCDiag-Device";

    /// <summary>Most paired devices.</summary>
    public const int MaxDevices = 20;

    /// <summary>Last-seen times are written at most this often.</summary>
    public static readonly TimeSpan SeenWriteInterval = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private List<Stored>? _devices;
    private string? _installId;

    /// <summary>This install's random ID (16 hex digits), created once.</summary>
    public string InstallId
    {
        get
        {
            lock (_lock)
            {
                Load();
                if (_installId is null)
                {
                    _installId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
                    Save(_devices!);
                }

                return _installId;
            }
        }
    }

    /// <summary>The device token a request carries: the companion app's header, or a browser's cookie.</summary>
    public static string? TokenOf(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Headers[HeaderName].ToString();
        return header.Length > 0 ? header : request.Cookies[CookieName];
    }

    /// <summary>The paired devices, most recently used first.</summary>
    public IReadOnlyList<PairedDevice> List()
    {
        lock (_lock)
        {
            return Load().Where(d => !Expired(d)).Select(d => d.Device).OrderByDescending(d => d.LastSeenAt).ToList();
        }
    }

    /// <summary>Pairs a new device; returns it and its token (for the cookie). The oldest is removed beyond <see cref="MaxDevices"/>.</summary>
    public (PairedDevice Device, string Token) Pair(string? name, string? address)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = timeProvider.GetUtcNow();
        var device = new PairedDevice(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)), Clean(name), now, now, address);
        lock (_lock)
        {
            var devices = Load();
            devices.RemoveAll(Expired);
            while (devices.Count >= MaxDevices)
            {
                devices.Remove(devices.MinBy(d => d.Device.LastSeenAt)!);
            }

            devices.Add(new Stored(device, Hash(token)));
            Save(devices);
        }

        return (device, token);
    }

    /// <summary>The device a token belongs to, or null; refreshes its last-seen time.</summary>
    public PairedDevice? Validate(string? token, string? address)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return null;
        }

        var hash = Hash(token);
        lock (_lock)
        {
            var devices = Load();
            var index = devices.FindIndex(d => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(d.TokenHash), Encoding.ASCII.GetBytes(hash)));
            if (index < 0 || Expired(devices[index]))
            {
                return null;
            }

            var now = timeProvider.GetUtcNow();
            var stored = devices[index];
            if (now - stored.Device.LastSeenAt >= SeenWriteInterval || stored.Device.LastAddress != address)
            {
                stored = stored with { Device = stored.Device with { LastSeenAt = now, LastAddress = address } };
                devices[index] = stored;
                Save(devices);
            }

            return stored.Device;
        }
    }

    /// <summary>Removes a device. False when there is no such device.</summary>
    public bool Revoke(string id)
    {
        lock (_lock)
        {
            var devices = Load();
            var removed = devices.RemoveAll(d => d.Device.Id == id) > 0;
            if (removed)
            {
                Save(devices);
            }

            return removed;
        }
    }

    private bool Expired(Stored stored) => timeProvider.GetUtcNow() - stored.Device.LastSeenAt > TimeSpan.FromDays(options.CurrentValue.Lan.DeviceIdleDays);

    private static string Clean(string? name)
    {
        var text = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length == 0 ? "Device" : text.Length > 60 ? text[..60] : text;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private List<Stored> Load()
    {
        if (_devices is not null)
        {
            return _devices;
        }

        try
        {
            var file = File.Exists(paths.PairedDevicesFile) ? JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(paths.PairedDevicesFile), Json) : null;
            _installId = file?.Id;
            _devices = file?.Devices?.ToList() ?? [];
        }
        catch (JsonException)
        {
            // An unreadable file pairs nobody; devices pair again.
            _devices = [];
        }

        return _devices;
    }

    private void Save(List<Stored> devices)
    {
        _devices = devices;
        Directory.CreateDirectory(paths.Root);
        var temp = paths.PairedDevicesFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new StoredFile(_installId, devices), Json));
        File.Move(temp, paths.PairedDevicesFile, overwrite: true);
    }

    private sealed record Stored(PairedDevice Device, string TokenHash);

    private sealed record StoredFile(string? Id, IReadOnlyList<Stored>? Devices);
}

/// <summary>
/// One-time pairing codes, shown on this PC and typed on the new device. A code is 8 digits, valid for
/// <see cref="Lifetime"/>, works once, and stops working after <see cref="MaxWrongAttempts"/> wrong attempts;
/// each network address may try at most <see cref="MaxAttemptsPerMinute"/> times a minute.
/// </summary>
public sealed class PairingCodes(TimeProvider timeProvider)
{
    /// <summary>How long a code is valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Wrong attempts before the code is discarded.</summary>
    public const int MaxWrongAttempts = 5;

    /// <summary>Attempts per address per minute.</summary>
    public const int MaxAttemptsPerMinute = 10;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);
    private string? _code;
    private DateTimeOffset _expiresAt;
    private int _wrong;

    /// <summary>The current code and when it expires, or null.</summary>
    public (string Code, DateTimeOffset ExpiresAt)? Current
    {
        get
        {
            lock (_lock)
            {
                return _code is not null && timeProvider.GetUtcNow() < _expiresAt ? (Format(_code), _expiresAt) : null;
            }
        }
    }

    /// <summary>Creates a new code (replacing any previous one).</summary>
    public (string Code, DateTimeOffset ExpiresAt) Create()
    {
        lock (_lock)
        {
            _code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
            _expiresAt = timeProvider.GetUtcNow() + Lifetime;
            _wrong = 0;
            return (Format(_code), _expiresAt);
        }
    }

    /// <summary>Discards the current code.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _code = null;
        }
    }

    /// <summary>The outcome of an attempt.</summary>
    public enum Result
    {
        /// <summary>The code was right; it is now used up.</summary>
        Accepted,

        /// <summary>Wrong, expired or no code.</summary>
        Rejected,

        /// <summary>Too many attempts from this address; try again in a minute.</summary>
        Throttled,
    }

    /// <summary>Checks a code typed on a device.</summary>
    public Result TryRedeem(string? input, string address)
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_attempts.TryGetValue(address, out var attempts))
            {
                if (_attempts.Count > 256)
                {
                    _attempts.Clear();
                }

                attempts = new Queue<DateTimeOffset>();
                _attempts[address] = attempts;
            }

            while (attempts.Count > 0 && now - attempts.Peek() >= TimeSpan.FromMinutes(1))
            {
                attempts.Dequeue();
            }

            if (attempts.Count >= MaxAttemptsPerMinute)
            {
                return Result.Throttled;
            }

            attempts.Enqueue(now);
            var digits = new string((input ?? "").Where(char.IsAsciiDigit).ToArray());
            if (_code is null || now >= _expiresAt)
            {
                return Result.Rejected;
            }

            if (digits.Length == _code.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(digits), Encoding.ASCII.GetBytes(_code)))
            {
                _code = null;
                return Result.Accepted;
            }

            if (++_wrong >= MaxWrongAttempts)
            {
                _code = null;
            }

            return Result.Rejected;
        }
    }

    private static string Format(string code) => $"{code[..4]}-{code[4..]}";
}
