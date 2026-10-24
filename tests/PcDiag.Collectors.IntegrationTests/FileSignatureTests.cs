using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Processes;

namespace PcDiag.Collectors.IntegrationTests;

public sealed class FileSignatureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));

    public FileSignatureTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static readonly string[] WindowsPrograms = ["cmd.exe", "notepad.exe", "svchost.exe", "conhost.exe", "taskhostw.exe"];

    private static string SignedProgram => Environment.ProcessPath!; // the .NET host (dotnet.exe or testhost.exe), signed by Microsoft

    [Fact]
    public void Native_structures_have_the_windows_x64_layout()
    {
        var (data, file, catalog, catalogInfo) = FileSignatures.Layout();

        data.ShouldBe(88);
        file.ShouldBe(32);
        catalog.ShouldBe(72);
        catalogInfo.ShouldBe(524);
    }

    [Fact]
    public void A_program_signed_by_microsoft_is_valid_and_names_its_signer()
    {
        var signature = FileSignatures.Verify(SignedProgram);

        signature.Status.ShouldBe(SignatureStatus.Embedded);
        signature.Signer.ShouldNotBeNull().ShouldContain("Microsoft");
    }

    [Fact]
    public void Windows_files_signed_through_a_catalog_are_valid()
    {
        // Most of System32 has no signature of its own; Windows lists it in signed catalogs.
        var files = WindowsPrograms
            .Select(name => Path.Combine(Environment.SystemDirectory, name))
            .Where(File.Exists)
            .Select(FileSignatures.Verify)
            .ToList();

        files.ShouldNotBeEmpty();
        files.ShouldAllBe(s => s.Status == SignatureStatus.Catalog || s.Status == SignatureStatus.Embedded);
        files.ShouldContain(s => s.Status == SignatureStatus.Catalog, "at least one of these is catalog-signed on every Windows 10/11");
        files.ShouldAllBe(s => s.Signer != null && s.Signer.Contains("Microsoft"));
    }

    [Fact]
    public void A_file_without_a_signature_is_not_signed()
    {
        var path = Path.Combine(_directory, "plain.exe");
        File.WriteAllText(path, "not a program");

        FileSignatures.Verify(path).ShouldBe(new FileSignature(SignatureStatus.NotSigned, null));
    }

    [Fact]
    public void A_signed_program_changed_after_signing_is_not_trusted()
    {
        var path = Path.Combine(_directory, "changed.exe");
        File.Copy(SignedProgram, path);
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 3] ^= 0xFF; // inside the signed content, away from the headers and the signature
        File.WriteAllBytes(path, bytes);

        FileSignatures.Verify(path).Status.ShouldBe(SignatureStatus.NotTrusted);
    }

    [Fact]
    public void Programs_of_app_packages_carry_the_package_publisher()
    {
        // Any installed Microsoft Store / MSIX package with a program in it (every Windows 10/11 has several).
        var program = new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty)
            .Select(package => { try { return package.InstalledLocation?.Path; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { return null; } })
            .Where(location => location is not null && location.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            .SelectMany(location => SafeExecutables(location!))
            .FirstOrDefault();
        program.ShouldNotBeNull("no app package with a program was found");

        var signature = FileSignatures.Verify(program);

        signature.Status.ShouldBeOneOf(SignatureStatus.Package, SignatureStatus.Embedded, SignatureStatus.Catalog);
        FileSignatures.PackagePublisher(program).ShouldNotBeNull().Signer.ShouldNotBeNullOrWhiteSpace();
    }

    private static List<string> SafeExecutables(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [Fact]
    public void A_missing_file_is_unknown() =>
        FileSignatures.Verify(Path.Combine(_directory, "missing.exe")).Status.ShouldBe(SignatureStatus.Unknown);
}
