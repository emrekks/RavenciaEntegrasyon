using System.Collections.Concurrent;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

// A taxpayer lookup answer per provider connection and VKN/TCKN, kept for a day so opening the preview again does not
// ask the provider each time. Only certain answers are kept (Unknown is asked again), and the request that issues the
// invoice always asks afresh, so a registration that changed since then is caught before anything is sent.
public sealed class TrendyolEFaturamTaxpayerCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const int MaximumEntries = 10_000;
    private readonly ConcurrentDictionary<(Guid ConnectionId, string TaxId), (EInvoiceTaxpayerLookup Lookup, DateTimeOffset ExpiresAt)> entries = new();

    public bool TryGet(Guid connectionId, string taxId, out EInvoiceTaxpayerLookup lookup)
    {
        if (entries.TryGetValue((connectionId, taxId), out var entry) && entry.ExpiresAt > timeProvider.GetUtcNow())
        {
            lookup = entry.Lookup;
            return true;
        }
        lookup = null!;
        return false;
    }

    public void Set(Guid connectionId, string taxId, EInvoiceTaxpayerLookup lookup)
    {
        if (lookup.State == EInvoiceTaxpayerState.Unknown) return;
        var now = timeProvider.GetUtcNow();
        if (entries.Count >= MaximumEntries)
            foreach (var expired in entries.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
                entries.TryRemove(expired, out _);
        if (entries.Count >= MaximumEntries) entries.Clear();
        entries[(connectionId, taxId)] = (lookup, now + Lifetime);
    }
}
