using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using PcDiag.Core.Telemetry;

namespace PcDiag.Platform.Windows.Processes;

/// <summary>The outcome of checking a program file's digital signature.</summary>
/// <param name="Status">Signed, signed through a Windows catalog, unsigned, or signed but not trusted.</param>
/// <param name="Signer">The signing certificate's name (for catalog-signed files, the catalog's signer).</param>
public sealed record FileSignature(SignatureStatus Status, string? Signer);

/// <summary>
/// Checks Authenticode signatures with WinVerifyTrust (wintrust.h): the file's own (embedded) signature first, then
/// the system catalogs (most of Windows is catalog-signed). Revocation is not checked and nothing is fetched from
/// the network (WTD_CACHE_ONLY_URL_RETRIEVAL): the recorder never goes online. Reads the whole file to hash it.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class FileSignatures
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int CryptEFileError = unchecked((int)0x80092003);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    /// <summary>Checks <paramref name="path"/>. Unknown when the file cannot be read.</summary>
    public static FileSignature Verify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SafeFileHandle file;
        try
        {
            file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileSignature(SignatureStatus.Unknown, null);
        }

        using (file)
        {
            var embedded = VerifyEmbedded(path, file);
            if (embedded.Status != SignatureStatus.NotSigned)
            {
                return embedded;
            }

            // No signature in the file: look it up in the catalogs (SHA-256 first, then the older SHA-1 ones), and
            // for an app package's files take the package's publisher.
            return VerifyCatalog(path, file, "SHA256") ?? VerifyCatalog(path, file, "SHA1") ?? PackagePublisher(path) ?? embedded;
        }
    }

    /// <summary>
    /// Files of installed app packages (Program Files\WindowsApps\&lt;package&gt;\...) are signed as a package, not one
    /// by one; Windows verifies the package when it is installed and protects the folder. The publisher comes from
    /// the package's AppxManifest.xml.
    /// </summary>
    internal static FileSignature? PackagePublisher(string path)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var package = path[root.Length..].Split(Path.DirectorySeparatorChar)[0];
        var manifest = Path.Combine(root, package, "AppxManifest.xml");
        try
        {
            var document = System.Xml.Linq.XDocument.Load(manifest);
            var publisher = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Publisher")?.Value;
            var name = publisher?.Split(',').Select(part => part.Trim()).FirstOrDefault(part => part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))?[3..];
            return new FileSignature(SignatureStatus.Package, string.IsNullOrWhiteSpace(name) ? publisher : name.Trim('"'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static unsafe FileSignature VerifyEmbedded(string path, SafeFileHandle file)
    {
        fixed (char* filePath = path)
        {
            var fileInfo = new WintrustFileInfo { Size = (uint)sizeof(WintrustFileInfo), FilePath = filePath, File = file.DangerousGetHandle() };
            var data = NewData(WtdChoiceFile, &fileInfo);
            return Run(ref data);
        }
    }

    private static unsafe FileSignature? VerifyCatalog(string path, SafeFileHandle file, string algorithm)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, 0, algorithm, 0, 0))
        {
            return null;
        }

        nint catalog = 0;
        try
        {
            uint hashSize = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, file.DangerousGetHandle(), ref hashSize, null, 0);
            if (hashSize == 0 || hashSize > 64)
            {
                return null;
            }

            var hash = new byte[hashSize];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, file.DangerousGetHandle(), ref hashSize, hash, 0))
            {
                return null;
            }

            catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, hashSize, 0, 0);
            if (catalog == 0)
            {
                return null;
            }

            var info = new CatalogInfo { Size = (uint)sizeof(CatalogInfo) };
            if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0))
            {
                return null;
            }

            var catalogPath = new string(info.CatalogFile, 0, new ReadOnlySpan<char>(info.CatalogFile, 260).IndexOf('\0') is var n and >= 0 ? n : 260);
            var memberTag = Convert.ToHexString(hash);
            fixed (char* catalogFile = catalogPath)
            fixed (char* tag = memberTag)
            fixed (char* memberPath = path)
            fixed (byte* hashBytes = hash)
            {
                var catalogInfo = new WintrustCatalogInfo
                {
                    Size = (uint)sizeof(WintrustCatalogInfo),
                    CatalogFilePath = catalogFile,
                    MemberTag = tag,
                    MemberFilePath = memberPath,
                    MemberFile = file.DangerousGetHandle(),
                    CalculatedFileHash = hashBytes,
                    CalculatedFileHashSize = hashSize,
                    CatAdmin = admin,
                };
                var data = NewData(WtdChoiceCatalog, &catalogInfo);
                var result = Run(ref data);
                return result.Status == SignatureStatus.Embedded ? result with { Status = SignatureStatus.Catalog } : result;
            }
        }
        finally
        {
            if (catalog != 0)
            {
                CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
            }

            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    private static unsafe WintrustData NewData(uint choice, void* union) => new()
    {
        Size = (uint)sizeof(WintrustData),
        UiChoice = WtdUiNone,
        RevocationChecks = WtdRevokeNone,
        UnionChoice = choice,
        Union = union,
        StateAction = WtdStateActionVerify,
        ProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
    };

    private static unsafe FileSignature Run(ref WintrustData data)
    {
        var action = GenericVerifyV2;
        var result = WinVerifyTrust(-1, ref action, ref data);
        try
        {
            return result switch
            {
                0 => new FileSignature(SignatureStatus.Embedded, SignerName(data.StateData)),
                TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => new FileSignature(SignatureStatus.NotSigned, null),
                CryptEFileError => new FileSignature(SignatureStatus.Unknown, null),
                _ => new FileSignature(SignatureStatus.NotTrusted, SignerName(data.StateData)),
            };
        }
        finally
        {
            data.StateAction = WtdStateActionClose;
            WinVerifyTrust(-1, ref action, ref data);
        }
    }

    /// <summary>The leaf certificate's simple name from the verified state (CRYPT_PROVIDER_CERT.pCert at offset 8).</summary>
    private static unsafe string? SignerName(nint stateData)
    {
        if (stateData == 0)
        {
            return null;
        }

        var provider = WTHelperProvDataFromStateData(stateData);
        if (provider == 0)
        {
            return null;
        }

        var signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
        if (signer == 0)
        {
            return null;
        }

        var cert = WTHelperGetProvCertFromChain(signer, 0);
        if (cert == 0)
        {
            return null;
        }

        var context = *(nint*)(cert + 8);
        if (context == 0)
        {
            return null;
        }

        using var certificate = new X509Certificate2(context);
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Struct sizes for the layout tests (x64: WINTRUST_DATA 88, WINTRUST_FILE_INFO 32, WINTRUST_CATALOG_INFO 72, CATALOG_INFO 524).</summary>
    internal static unsafe (int Data, int File, int Catalog, int CatalogInfo) Layout() =>
        (sizeof(WintrustData), sizeof(WintrustFileInfo), sizeof(WintrustCatalogInfo), sizeof(CatalogInfo));

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WintrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public void* Union;
        public uint StateAction;
        public nint StateData;
        public char* UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WintrustFileInfo
    {
        public uint Size;
        public char* FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WintrustCatalogInfo
    {
        public uint Size;
        public uint CatalogVersion;
        public char* CatalogFilePath;
        public char* MemberTag;
        public char* MemberFilePath;
        public nint MemberFile;
        public byte* CalculatedFileHash;
        public uint CalculatedFileHashSize;
        public nint CatalogContext;
        public nint CatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CatalogInfo
    {
        public uint Size;
        public fixed char CatalogFile[260];
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint hwnd, ref Guid action, ref WintrustData data);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvSignerFromChain(nint providerData, uint signerIndex, int counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvCertFromChain(nint signer, uint certIndex);

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminAcquireContext2(out nint admin, nint subsystem, string hashAlgorithm, nint strongHashPolicy, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminCalcHashFromFileHandle2(nint admin, nint file, ref uint hashSize, [Out] byte[]? hash, uint flags);

    [LibraryImport("wintrust.dll")]
    private static partial nint CryptCATAdminEnumCatalogFromHash(nint admin, byte[] hash, uint hashSize, uint flags, nint previous);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATCatalogInfoFromContext(nint catalog, ref CatalogInfo info, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseCatalogContext(nint admin, nint catalog, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseContext(nint admin, uint flags);
}
