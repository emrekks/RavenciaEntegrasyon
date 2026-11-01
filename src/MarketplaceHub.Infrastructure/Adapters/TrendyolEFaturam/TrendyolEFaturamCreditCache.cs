using System.Collections.Concurrent;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

// The remaining-credit answer per E-Faturam connection, so opening the integrations page does not ask the provider each
// time: a value for 10 minutes, "not a partner" for a day, any other failure for 15 minutes (a refused password is not
// tried again on every page view). Concurrent readers of one connection wait for a single request.
public sealed class TrendyolEFaturamCreditCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan AvailableLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan NotPartnerLifetime = TimeSpan.FromHours(24);
    internal static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(15);
    private const int MaximumEntries = 1_000;
    private readonly ConcurrentDictionary<Guid, (EFaturamRemainingCredit Credit, DateTimeOffset ExpiresAt)> entries = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    public bool TryGet(Guid connectionId, out EFaturamRemainingCredit credit)
    {
        if (entries.TryGetValue(connectionId, out var entry) && entry.ExpiresAt > timeProvider.GetUtcNow())
        {
            credit = entry.Credit;
            return true;
        }
        credit = null!;
        return false;
    }

    public void Set(Guid connectionId, EFaturamRemainingCredit credit, TimeSpan? retryAfter = null)
    {
        var lifetime = credit.Available ? AvailableLifetime
            : credit.Reason == EFaturamRemainingCredit.NotPartnerReason ? NotPartnerLifetime
            : retryAfter is { } wait && wait > FailureLifetime ? wait
            : FailureLifetime;
        var now = timeProvider.GetUtcNow();
        if (entries.Count >= MaximumEntries)
            foreach (var expired in entries.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
                entries.TryRemove(expired, out _);
        if (entries.Count >= MaximumEntries) entries.Clear();
        entries[connectionId] = (credit, now + lifetime);
    }

    internal SemaphoreSlim Gate(Guid connectionId) => locks.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
}

// Available: the provider answered with a value. Otherwise Reason says why there is none and the panel shows nothing.
public sealed record EFaturamRemainingCredit(bool Available, long? RemainingCredit, string? Reason)
{
    public const string NotPartnerReason = "user-is-not-partner";

    public static EFaturamRemainingCredit Of(long remainingCredit) => new(true, remainingCredit, null);
    public static EFaturamRemainingCredit None(string reason) => new(false, null, reason);
}
