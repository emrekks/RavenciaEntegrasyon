using System.Collections.Concurrent;

namespace MarketplaceHub.Api.Security;

// Wrong passwords and codes are counted per account *and sending network* (ClientNetwork). Five failures
// from one network stop that network from trying that account for 15 minutes, and every further block
// doubles, up to a day. Nothing is counted for a network that is already blocked or for an account that
// does not exist, and nothing here ever answers differently from a wrong password.
//
// Account-wide, failures from all networks together are counted too (AccountFailuresPerWindow in
// AccountWindow; the Identity lockout in DependencyInjection is the same ceiling, kept in the database).
// Reaching it does not lock the account for everybody, it only slows guessing: while the account is "under
// attack", a network that has already failed for it (an IPv6 sender: its whole /48) may not try again,
// but
//   - a network the account fully signed in from (password and second factor) in the last TrustFor days,
//   - a network that has not failed for the account at all,
//   - and a session that is already signed in (its re-authentications and step-ups)
// still may. An attacker who knows the e-mail can therefore no longer keep the owner out, and each new
// network gives the attacker one guess at most while the account is under attack.
//
// Counters are reset only by a complete sign-in (RegisterSuccess), never by a correct password alone,
// so second-factor failures keep counting across new sign-ins.
//
// The state is in memory: the API runs as one instance, and a restart only forgets blocks and trust.
public sealed class SignInThrottle(TimeProvider time)
{
    public const int FailuresPerBlock = 5;
    public const int AccountFailuresPerWindow = 30;
    public static readonly TimeSpan AccountWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FirstBlock = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LongestBlock = TimeSpan.FromHours(24);
    // A network that stayed quiet this long starts again from the first block.
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(24);
    public static readonly TimeSpan TrustFor = TimeSpan.FromDays(30);
    private const int MaximumEntries = 50_000;

    private readonly ConcurrentDictionary<(Guid UserId, string Network), Entry> entries = new();
    private readonly ConcurrentDictionary<(Guid UserId, string Group), DateTimeOffset> failedGroups = new();
    private readonly ConcurrentDictionary<(Guid UserId, string Network), DateTimeOffset> trusted = new();
    private readonly ConcurrentDictionary<Guid, Window> accountWindows = new();

    // The network's own block (five failures from it).
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
        failedGroups[(userId, ClientNetwork.WideGroup(network))] = now;
        CountAccountFailure(userId, now);
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

    // A complete sign-in (or step-up) from this network: its count is cleared and the network is trusted.
    public void RegisterSuccess(Guid userId, string network)
    {
        entries.TryRemove((userId, network), out _);
        failedGroups.TryRemove((userId, ClientNetwork.WideGroup(network)), out _);
        trusted[(userId, network)] = time.GetUtcNow();
    }

    // True while the account took AccountFailuresPerWindow failures in the current window.
    public bool IsAccountUnderAttack(Guid userId)
    {
        if (!accountWindows.TryGetValue(userId, out var window)) return false;
        lock (window) return time.GetUtcNow() - window.StartedAt < AccountWindow && window.Count >= AccountFailuresPerWindow;
    }

    public bool IsTrusted(Guid userId, string network) =>
        trusted.TryGetValue((userId, network), out var at) && time.GetUtcNow() - at < TrustFor;

    // The network (an IPv6 sender: its /48) failed for the account within ForgetAfter.
    public bool HasFailedRecently(Guid userId, string network) =>
        failedGroups.TryGetValue((userId, ClientNetwork.WideGroup(network)), out var at) && time.GetUtcNow() - at <= ForgetAfter;

    // While the account is under attack (here or by the Identity lockout), may this network still try?
    public bool RefusesWhileUnderAttack(Guid userId, string network) =>
        !IsTrusted(userId, network) && HasFailedRecently(userId, network);

    public static TimeSpan BlockLength(int block)
    {
        var minutes = FirstBlock.TotalMinutes * Math.Pow(2, Math.Clamp(block - 1, 0, 16));
        return minutes >= LongestBlock.TotalMinutes ? LongestBlock : TimeSpan.FromMinutes(minutes);
    }

    private void CountAccountFailure(Guid userId, DateTimeOffset now)
    {
        var window = accountWindows.GetOrAdd(userId, _ => new Window());
        lock (window)
        {
            if (now - window.StartedAt >= AccountWindow) { window.StartedAt = now; window.Count = 0; }
            window.Count++;
        }
    }

    private void PruneIfLarge(DateTimeOffset now)
    {
        if (entries.Count < MaximumEntries && accountWindows.Count < MaximumEntries && failedGroups.Count < MaximumEntries && trusted.Count < MaximumEntries) return;
        foreach (var (key, entry) in entries)
        {
            lock (entry)
            {
                var blocked = entry.BlockedUntil is { } until && until > now;
                if (!blocked && (entry.LastFailureAt is not { } last || now - last > ForgetAfter)) entries.TryRemove(key, out _);
            }
        }
        foreach (var (key, at) in failedGroups) if (now - at > ForgetAfter) failedGroups.TryRemove(key, out _);
        foreach (var (key, at) in trusted) if (now - at >= TrustFor) trusted.TryRemove(key, out _);
        foreach (var (key, window) in accountWindows)
        {
            lock (window) if (now - window.StartedAt >= AccountWindow) accountWindows.TryRemove(key, out _);
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
