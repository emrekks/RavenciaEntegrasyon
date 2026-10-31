using MarketplaceHub.Domain;

namespace MarketplaceHub.Api.Security;

// How long a sign-in lasts. "Beni Hatırla" keeps a fully signed-in session for up to 30 days while it is used at least
// once a week; without it the session ends after 30 idle minutes, after 12 hours, or when the browser closes.
// A session that has not passed every step (second factor, forced password change) always gets the short idle window.
// Whether a session is remembered is read from its own lifetime, so no column is needed for it.
public static class SessionLifetime
{
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Absolute = TimeSpan.FromHours(12);
    public static readonly TimeSpan RememberedIdle = TimeSpan.FromDays(7);
    public static readonly TimeSpan RememberedAbsolute = TimeSpan.FromDays(30);

    public static DateTimeOffset AbsoluteExpiry(DateTimeOffset issuedAt, bool remembered) => issuedAt + (remembered ? RememberedAbsolute : Absolute);

    public static bool IsRemembered(UserSession session) => session.AbsoluteExpiresAt - session.IssuedAt > Absolute;

    public static DateTimeOffset IdleExpiry(UserSession session, DateTimeOffset now)
    {
        var window = session.State == SessionState.Active && IsRemembered(session) ? RememberedIdle : Idle;
        var expiry = now + window;
        return expiry < session.AbsoluteExpiresAt ? expiry : session.AbsoluteExpiresAt;
    }

    // A remembered session's cookie outlives the browser; any other is a browser-session cookie.
    public static DateTimeOffset? CookieExpiry(UserSession session) => IsRemembered(session) ? session.AbsoluteExpiresAt : null;
}
