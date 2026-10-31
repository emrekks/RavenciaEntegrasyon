using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class SessionLifetimeTests
{
    private static readonly DateTimeOffset Issued = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private static UserSession Session(bool remembered, SessionState state = SessionState.Active) =>
        new() { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), State = state, TokenHash = "x", IssuedAt = Issued, AbsoluteExpiresAt = SessionLifetime.AbsoluteExpiry(Issued, remembered) };

    [Fact]
    public void A_remembered_session_stays_signed_in_for_a_week_of_idleness_within_thirty_days()
    {
        var session = Session(remembered: true);
        Assert.True(SessionLifetime.IsRemembered(session));
        Assert.Equal(Issued.AddDays(30), session.AbsoluteExpiresAt);
        Assert.Equal(Issued.AddHours(2).AddDays(7), SessionLifetime.IdleExpiry(session, Issued.AddHours(2)));
        Assert.Equal(Issued.AddDays(30), SessionLifetime.IdleExpiry(session, Issued.AddDays(28)));
        Assert.Equal(Issued.AddDays(30), SessionLifetime.CookieExpiry(session));
    }

    [Fact]
    public void A_session_without_remember_me_keeps_the_short_windows_and_a_browser_session_cookie()
    {
        var session = Session(remembered: false);
        Assert.False(SessionLifetime.IsRemembered(session));
        Assert.Equal(Issued.AddHours(12), session.AbsoluteExpiresAt);
        Assert.Equal(Issued.AddHours(1).AddMinutes(30), SessionLifetime.IdleExpiry(session, Issued.AddHours(1)));
        Assert.Equal(Issued.AddHours(12), SessionLifetime.IdleExpiry(session, Issued.AddHours(11).AddMinutes(50)));
        Assert.Null(SessionLifetime.CookieExpiry(session));
    }

    [Theory]
    [InlineData(SessionState.MfaChallenge)]
    [InlineData(SessionState.PasswordChangeRequired)]
    public void A_remembered_session_that_has_not_finished_signing_in_gets_the_short_idle_window(SessionState state)
    {
        var session = Session(remembered: true, state);
        Assert.Equal(Issued.AddMinutes(30), SessionLifetime.IdleExpiry(session, Issued));
    }
}
