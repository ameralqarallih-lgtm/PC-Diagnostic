using PcDiag.Core.Configuration;
using PcDiag.Core.Ipc;

namespace PcDiag.Core.Tests;

public sealed class LaunchTicketsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly DataPaths _paths;

    public LaunchTicketsTests() => _paths = new DataPaths(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void A_ticket_is_redeemed_exactly_once()
    {
        var token = LaunchTickets.Issue(_paths, Now);

        LaunchTickets.TryRedeem(_paths, token, Now.AddSeconds(5)).ShouldBeTrue();
        LaunchTickets.TryRedeem(_paths, token, Now.AddSeconds(6)).ShouldBeFalse();
        Directory.EnumerateFiles(_paths.LaunchDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void The_token_itself_is_never_written_to_disk()
    {
        var token = LaunchTickets.Issue(_paths, Now);

        var file = Directory.EnumerateFiles(_paths.LaunchDirectory).ShouldHaveSingleItem();
        Path.GetFileName(file).ShouldBe(LaunchTickets.FileName(token));
        Path.GetFileName(file).ShouldNotContain(token);
        File.ReadAllText(file).ShouldNotContain(token);
    }

    [Fact]
    public void Expired_tickets_are_refused_and_removed()
    {
        var token = LaunchTickets.Issue(_paths, Now);

        LaunchTickets.TryRedeem(_paths, token, Now + LaunchTickets.Lifetime).ShouldBeFalse();
        Directory.EnumerateFiles(_paths.LaunchDirectory).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ticket")]
    [InlineData("../../web.json")]
    public void Unknown_or_malformed_tokens_are_refused(string? token)
    {
        LaunchTickets.Issue(_paths, Now);

        LaunchTickets.TryRedeem(_paths, token, Now).ShouldBeFalse();
        LaunchTickets.TryRedeem(_paths, new string('a', LaunchTickets.MaxTokenLength + 1), Now).ShouldBeFalse();
        Directory.EnumerateFiles(_paths.LaunchDirectory).ShouldHaveSingleItem();
    }

    [Fact]
    public void Nothing_is_redeemed_before_the_launch_folder_exists() =>
        LaunchTickets.TryRedeem(_paths, "anything", Now).ShouldBeFalse();

    [Fact]
    public void A_ticket_claiming_a_far_future_expiry_is_refused()
    {
        var token = LaunchTickets.Issue(_paths, Now);
        var file = Path.Combine(_paths.LaunchDirectory, LaunchTickets.FileName(token));
        File.WriteAllText(file, Now.AddDays(30).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        LaunchTickets.TryRedeem(_paths, token, Now).ShouldBeFalse();
    }

    [Fact]
    public void Issuing_prunes_stale_tickets()
    {
        LaunchTickets.Issue(_paths, Now);
        var stale = Directory.EnumerateFiles(_paths.LaunchDirectory).Single();
        File.SetLastWriteTimeUtc(stale, Now.UtcDateTime.AddHours(-1));

        LaunchTickets.Issue(_paths, Now);

        File.Exists(stale).ShouldBeFalse();
        Directory.EnumerateFiles(_paths.LaunchDirectory).ShouldHaveSingleItem();
    }
}
