using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Web.Security;

namespace PcDiag.Web.Tests;

public class LaunchTokenServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly IOptions<WebOptions> _options = Options.Create(new WebOptions { LaunchTokenLifetimeSeconds = 120, SessionIdleHours = 12 });

    [Fact]
    public void Tokens_expire()
    {
        var service = new LaunchTokenService(_options, _time);
        var token = service.IssueToken();

        _time.Advance(TimeSpan.FromSeconds(121));

        service.TryRedeem(token).ShouldBeFalse();
    }

    [Fact]
    public void Only_a_bounded_number_of_tokens_are_outstanding()
    {
        var service = new LaunchTokenService(_options, _time);
        var first = service.IssueToken();
        for (var i = 0; i < LaunchTokenService.MaxOutstandingTokens; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
            service.IssueToken();
        }

        service.TryRedeem(first).ShouldBeFalse(); // the oldest token was evicted
    }

    [Fact]
    public void Sessions_expire_after_idle_period_and_slide_on_use()
    {
        var sessions = new SessionStore(_options, _time, new SessionTable());
        var id = sessions.Create();

        _time.Advance(TimeSpan.FromHours(11));
        sessions.Validate(id).ShouldBeTrue(); // refreshes the idle timer

        _time.Advance(TimeSpan.FromHours(11));
        sessions.Validate(id).ShouldBeTrue();

        _time.Advance(TimeSpan.FromHours(12));
        sessions.Validate(id).ShouldBeFalse();
    }

    [Fact]
    public void Web_options_reject_non_loopback_dev_origins()
    {
        var result = new WebOptionsValidator().Validate(null, new WebOptions { AllowedDevOrigins = ["http://192.168.1.10:5173"] });

        result.Failed.ShouldBeTrue();
    }
}
