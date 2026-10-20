using MarketplaceHub.Domain;

namespace MarketplaceHub.Api.Security;

// A session that has passed the password but not the second factor (MFA challenge, valid for up to 12
// hours) could guess six-digit codes and recovery codes limited only by the per-address rate limit.
// Each wrong code now counts in the session, and the fifth ends it: the user signs in again with the
// password, which itself counts towards the account lockout.
public static class MfaAttemptPolicy
{
    public const int MaximumFailures = 5;

    // True when this failure ended the session.
    public static bool RegisterFailure(UserSession session, DateTimeOffset now)
    {
        session.MfaFailedAttempts++;
        if (session.MfaFailedAttempts < MaximumFailures) return false;
        Revoke(session, now);
        return true;
    }

    public static void RegisterSuccess(UserSession session) => session.MfaFailedAttempts = 0;

    public static void Revoke(UserSession session, DateTimeOffset now)
    {
        session.State = SessionState.Revoked;
        session.RevokedAt ??= now;
    }
}
