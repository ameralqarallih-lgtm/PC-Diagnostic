using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PcDiag.Core.Configuration;

namespace PcDiag.Web.Security;

/// <summary>
/// The self-signed HTTPS certificate for access from the local network. It is created once per install for this
/// PC's names and addresses at that time and kept until it nears expiry, so its fingerprint stays the same when the
/// PC gets a new address: the companion app pins that fingerprint, and browsers warn about a self-signed
/// certificate either way. It is stored in the data folder encrypted with DPAPI for the current user; the
/// fingerprint shown in Settings lets the user check they reached this PC.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LanCertificate
{
    /// <summary>How long a certificate is valid.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(825);

    /// <summary>A certificate this close to expiry is replaced.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private static readonly byte[] Entropy = "PcDiagnostic LAN certificate v1"u8.ToArray();

    /// <summary>The stored certificate when it still covers these names and addresses, or a new one.</summary>
    public static X509Certificate2 GetOrCreate(DataPaths paths, IReadOnlyList<string> names, IReadOnlyList<IPAddress> addresses, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (TryLoad(paths.LanCertificateFile) is { } existing)
        {
            if (existing.NotAfter.ToUniversalTime() - now.UtcDateTime > RenewBefore)
            {
                return existing;
            }

            existing.Dispose();
        }

        using var created = Create(names, addresses, now);
        var pfx = created.Export(X509ContentType.Pkcs12);
        Directory.CreateDirectory(paths.Root);
        var temp = paths.LanCertificateFile + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(pfx, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, paths.LanCertificateFile, overwrite: true);
        return Load(pfx);
    }

    /// <summary>The SHA-256 fingerprint as browsers show it, e.g. "AB:CD:…".</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData)).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => $"{a}:{b}");
    }

    /// <summary>Whether a certificate names every one of these hosts and addresses.</summary>
    public static bool Covers(X509Certificate2 certificate, IReadOnlyList<string> names, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
        {
            return false;
        }

        var dns = san.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ips = san.EnumerateIPAddresses().ToHashSet();
        return names.All(dns.Contains) && addresses.All(ips.Contains);
    }

    private static X509Certificate2 Create(IReadOnlyList<string> names, IReadOnlyList<IPAddress> addresses, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=PC Diagnostic ({Environment.MachineName})", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            san.AddDnsName(name);
        }

        foreach (var address in addresses)
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(now.AddDays(-1), now + Validity);
    }

    private static X509Certificate2? TryLoad(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return Load(ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            // Another user's file, or damaged: a new certificate replaces it.
            return null;
        }
    }

    // SChannel needs a key it can find again during the handshake, so the key is not ephemeral; it is removed from
    // the user's key store when the certificate is disposed.
    private static X509Certificate2 Load(byte[] pfx) => X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
}
