using System.Collections.Concurrent;

namespace MarketplaceHub.Api.Security;

// The sessions that passed the reset re-check (AuthEndpoints.ReauthenticateForResetAsync) a moment ago. Kept in this API
// process only: a restart (a deploy) simply asks for the password again.
public static class RecentReauthentication
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> Passed = new();

    public static bool IsFresh(Guid sessionId, DateTimeOffset now) =>
        Passed.TryGetValue(sessionId, out var at) && at <= now && now - at < Window;

    public static void Mark(Guid sessionId, DateTimeOffset now)
    {
        foreach (var entry in Passed)
            if (now - entry.Value >= Window) Passed.TryRemove(entry);
        Passed[sessionId] = now;
    }

    // Tests only.
    internal static void Forget(Guid sessionId) => Passed.TryRemove(sessionId, out _);
}
