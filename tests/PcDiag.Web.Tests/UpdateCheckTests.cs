using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Hosting;
using PcDiag.Web.Settings;
using PcDiag.Web.Updates;

namespace PcDiag.Web.Tests;

/// <summary>A newer release on GitHub is shown with its page, installer and SHA-256; nothing is asked when it may not ask.</summary>
public sealed class UpdateCheckTests : IDisposable
{
    private const string Releases = "https://api.github.com/repos/owner/PC-Diagnostic/releases/latest";
    private const string Sha = "F933698A98A88F96B29F7140EAE3D4FF7DB5FB0229079FD710DF88C2B6044589";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", "updates-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly Answer _answer = new();

    public UpdateCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Answer : HttpMessageHandler, IHttpClientFactory
    {
        public string Json { get; set; } = "{}";

        public HttpStatusCode Code { get; set; } = HttpStatusCode.OK;

        public List<Uri> Asked { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(Code) { Content = new StringContent(Json, Encoding.UTF8, "application/json") });
        }
    }

    private UpdateCheck Check(string? releases = Releases) =>
        new(Options.Create(new UpdateOptions { Releases = releases }), new SettingsStore(new DataPaths(_root)), _answer, _time, NullLogger<UpdateCheck>.Instance);

    private static string Release(string tag, string host = "github.com") => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://{{host}}/owner/PC-Diagnostic/releases/tag/{{tag}}",
          "published_at": "2026-11-20T10:00:00Z",
          "body": "### Download\nSHA-256: `{{Sha.ToLowerInvariant()}}`\n",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://{{host}}/owner/PC-Diagnostic/releases/download/{{tag}}/notes.txt" },
            { "name": "PcDiagnostic-Setup-99.0.0.exe", "browser_download_url": "https://{{host}}/owner/PC-Diagnostic/releases/download/{{tag}}/PcDiagnostic-Setup-99.0.0.exe" }
          ]
        }
        """;

    [Fact]
    public async Task A_newer_release_is_shown_with_its_page_installer_and_sha()
    {
        _answer.Json = Release("v99.0.0");
        using var check = Check();

        var status = await check.CheckAsync(CancellationToken.None);

        status.ShouldSatisfyAllConditions(
            s => s.Available.ShouldBeTrue(),
            s => s.Newer.ShouldBeTrue(),
            s => s.Latest.ShouldBe("99.0.0"),
            s => s.Current.ShouldBe(ProductInfo.Version),
            s => s.Page.ShouldBe("https://github.com/owner/PC-Diagnostic/releases/tag/v99.0.0"),
            s => s.Download.ShouldBe("https://github.com/owner/PC-Diagnostic/releases/download/v99.0.0/PcDiagnostic-Setup-99.0.0.exe"),
            s => s.Sha256.ShouldBe(Sha),
            s => s.Problem.ShouldBeNull());
        _answer.Asked.ShouldHaveSingleItem().ToString().ShouldBe(Releases);
    }

    [Fact]
    public async Task The_version_running_is_not_newer_than_itself()
    {
        _answer.Json = Release("v" + ProductInfo.Version.Split('+')[0]);
        using var check = Check();

        (await check.CheckAsync(CancellationToken.None)).Newer.ShouldBeFalse();
    }

    [Fact]
    public async Task Links_away_from_github_are_not_shown()
    {
        _answer.Json = Release("v99.0.0", host: "example.com");
        using var check = Check();

        var status = await check.CheckAsync(CancellationToken.None);

        status.Newer.ShouldBeTrue();
        status.Page.ShouldBeNull();
        status.Download.ShouldBeNull();
    }

    [Fact]
    public async Task Nothing_is_asked_by_a_build_without_a_release_address_or_when_privacy_says_no()
    {
        using (var unconfigured = Check(releases: null))
        {
            (await unconfigured.CheckAsync(CancellationToken.None)).Available.ShouldBeFalse();
        }

        await File.WriteAllTextAsync(new DataPaths(_root).SettingsFile, """{ "Privacy": { "CheckForUpdates": false } }""");
        using var refused = Check();
        var status = await refused.CheckAsync(CancellationToken.None);

        status.Available.ShouldBeTrue();
        status.Enabled.ShouldBeFalse();
        _answer.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_check_says_why_and_asking_again_at_once_does_not_ask()
    {
        _answer.Code = HttpStatusCode.ServiceUnavailable;
        using var check = Check();

        (await check.CheckAsync(CancellationToken.None)).Problem.ShouldNotBeNull();
        await check.CheckAsync(CancellationToken.None);
        _answer.Asked.Count.ShouldBe(1);

        _time.Advance(UpdateCheck.AtMostEvery);
        _answer.Code = HttpStatusCode.OK;
        _answer.Json = Release("v99.0.0");
        (await check.CheckAsync(CancellationToken.None)).ShouldSatisfyAllConditions(s => s.Newer.ShouldBeTrue(), s => s.Problem.ShouldBeNull());
    }

    [Theory]
    [InlineData("v1.0.10", "1.0.10")]
    [InlineData("1.0.10+41eff8b2152c", "1.0.10")]
    [InlineData("PC Diagnostic 2.1", "2.1")]
    [InlineData("latest", null)]
    public void Versions_are_read_from_tags(string text, string? version) => (UpdateCheck.VersionOf(text)?.ToString()).ShouldBe(version);
}
