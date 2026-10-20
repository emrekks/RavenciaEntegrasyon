using System.Collections.Concurrent;

namespace MarketplaceHub.Api.Security;

// Wrong passwords and codes are counted per account *and sending network* (ClientNetwork). Five failures
// from one network stop that network from trying that account for 15 minutes, and every further block
// doubles, up to a day. The account itself is not locked for everybody: an attacker who keeps guessing
// can no longer keep the only owner out, because the owner signs in from another network. The account-wide
// Identity lockout stays as a ceiling for guesses spread over many networks (see DependencyInjection).
//
// A second, plain limit caps the sign-in attempts an account takes per minute from all networks together.
// The state is in memory: the API runs as one instance, and a restart only forgets blocks.
public sealed class SignInThrottle(TimeProvider time)
{
    public const int FailuresPerBlock = 5;
    public const int AccountAttemptsPerMinute = 20;
    public static readonly TimeSpan FirstBlock = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LongestBlock = TimeSpan.FromHours(24);
    // A network that stayed quiet this long starts again from the first block.
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(24);
    private const int MaximumEntries = 50_000;

    private readonly ConcurrentDictionary<(Guid UserId, string Network), Entry> entries = new();
    private readonly ConcurrentDictionary<Guid, Window> accountWindows = new();

    public bool IsBlocked(Guid userId, string network)
    {
        if (!entries.TryGetValue((userId, network), out var entry)) return false;
        lock (entry) return entry.BlockedUntil is { } until && until > time.GetUtcNow();
    }

    // True when this failure started a block for the network.
    public bool RegisterFailure(Guid userId, string network)
    {
        var now = time.GetUtcNow();
        PruneIfLarge(now);
        var entry = entries.GetOrAdd((userId, network), _ => new Entry());
        lock (entry)
        {
            if (entry.BlockedUntil is { } until && until > now) return false;
            if (entry.LastFailureAt is { } last && now - last > ForgetAfter) { entry.Blocks = 0; entry.Failures = 0; }
            entry.LastFailureAt = now;
            entry.Failures++;
            if (entry.Failures < FailuresPerBlock) return false;
            entry.Failures = 0;
            entry.Blocks++;
            entry.BlockedUntil = now + BlockLength(entry.Blocks);
            return true;
        }
    }

    public void RegisterSuccess(Guid userId, string network) => entries.TryRemove((userId, network), out _);

    // False when the account already took its sign-in attempts for this minute.
    public bool TryTakeAccountAttempt(Guid userId)
    {
        var now = time.GetUtcNow();
        var window = accountWindows.GetOrAdd(userId, _ => new Window());
        lock (window)
        {
            if (now - window.StartedAt >= TimeSpan.FromMinutes(1)) { window.StartedAt = now; window.Count = 0; }
            if (window.Count >= AccountAttemptsPerMinute) return false;
            window.Count++;
            return true;
        }
    }

    public static TimeSpan BlockLength(int block)
    {
        var minutes = FirstBlock.TotalMinutes * Math.Pow(2, Math.Clamp(block - 1, 0, 16));
        return minutes >= LongestBlock.TotalMinutes ? LongestBlock : TimeSpan.FromMinutes(minutes);
    }

    private void PruneIfLarge(DateTimeOffset now)
    {
        if (entries.Count < MaximumEntries && accountWindows.Count < MaximumEntries) return;
        foreach (var (key, entry) in entries)
        {
            lock (entry)
            {
                var blocked = entry.BlockedUntil is { } until && until > now;
                if (!blocked && (entry.LastFailureAt is not { } last || now - last > ForgetAfter)) entries.TryRemove(key, out _);
            }
        }
        foreach (var (key, window) in accountWindows)
        {
            lock (window) if (now - window.StartedAt >= TimeSpan.FromMinutes(1)) accountWindows.TryRemove(key, out _);
        }
    }

    private sealed class Entry
    {
        public int Failures;
        public int Blocks;
        public DateTimeOffset? BlockedUntil;
        public DateTimeOffset? LastFailureAt;
    }

    private sealed class Window
    {
        public DateTimeOffset StartedAt = DateTimeOffset.MinValue;
        public int Count;
    }
}
