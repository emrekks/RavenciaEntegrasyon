using System.Security.Cryptography;
using System.Text;

namespace MarketplaceHub.Application;

// Queue phase 1A (27 Nov 2026): when the scheduler creates the daily reference reads and the jobs due at a worker start.
// Only the moment a job is created or becomes runnable changes; what a job reads and writes stays as it was.

// The daily reference read (REFERENCE_DATA: the category tree and, for Trendyol and Pazarama, the brand list) used to be due
// for every connection in the same quarter hour (the 14:00-17:00 Istanbul window with a jitter of at most a minute): live
// 20-25 Nov 2026 it held both background slots for 6-8.5 minutes at 11:00 UTC and gave the week's worst waits (290-513 s).
// Each connection now gets its own daily slot inside a quiet morning window (default 06:00-10:00 Istanbul, after the nightly
// link verification of 03:00-06:00 and before the afternoon daily window), at a stable offset from its id: a worker restart
// or a deploy never moves it. No marketplace document names a window for these reads (marketplace-docs, 27 Nov 2026).
public static class ReferenceReadSchedulePolicy
{
    public static readonly TimeSpan DefaultWindowStart = TimeSpan.FromHours(6);
    public static readonly TimeSpan DefaultWindowLength = TimeSpan.FromHours(4);
    // A list fetched less than half a day before the slot (by hand, at activation) counts as fresh for that slot. Half a
    // day, not the interval: yesterday's read ended a few minutes after yesterday's slot and must be stale at today's.
    public static readonly TimeSpan FreshBeforeSlot = TimeSpan.FromHours(12);

    // Where the connection's slot falls inside the window, the same on every day and every process.
    public static TimeSpan Offset(Guid connectionId, TimeSpan windowLength)
    {
        var seconds = (long)Math.Max(1, windowLength.TotalSeconds);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"reference-slot:{connectionId:N}"));
        return TimeSpan.FromSeconds(BitConverter.ToUInt64(digest, 0) % (ulong)seconds);
    }

    // The connection's latest slot at or before now (yesterday's while today's has not come yet).
    public static DateTimeOffset LatestSlot(DateTimeOffset now, Guid connectionId, TimeSpan windowStart, TimeSpan windowLength)
    {
        var local = now.ToOffset(TurkeyClock.Offset);
        var day = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, TurkeyClock.Offset);
        var slot = day + windowStart + Offset(connectionId, windowLength);
        return (slot <= now ? slot : slot.AddDays(-1)).ToUniversalTime();
    }

    // Due once per slot: not queued since the latest slot began (a longer interval than a day also waits that long, less
    // half a day so a read that ended late does not skip its slot).
    public static bool IsDue(DateTimeOffset? lastScheduledAt, DateTimeOffset slot, int intervalSeconds, DateTimeOffset now) =>
        lastScheduledAt is not { } last
        || last < slot && now - last >= TimeSpan.FromSeconds(Math.Max(intervalSeconds, 86_400)) - TimeSpan.FromDays(1) + FreshBeforeSlot;

    // The lists to read at this slot, in the given order: those never fetched and those fetched before the slot (less half a
    // day; a longer interval than a day keeps a list fresh that much longer).
    public static IReadOnlyList<string> StaleResources(IReadOnlyList<string> resources, IReadOnlyDictionary<string, DateTimeOffset> fetchedAt, DateTimeOffset slot, int intervalSeconds)
    {
        var freshFrom = slot - FreshBeforeSlot - (TimeSpan.FromSeconds(Math.Max(intervalSeconds, 86_400)) - TimeSpan.FromDays(1));
        return resources.Where(resource => !fetchedAt.TryGetValue(resource, out var at) || at < freshFrom).ToArray();
    }

    // The dedup bucket of a slot: one job per list and slot (consecutive slots of a connection are a day apart).
    public static long Bucket(DateTimeOffset slot) => slot.ToUnixTimeSeconds() / 86_400;
}

// A worker start used to queue every policy that fell due while it was down in one pass (Worker.RecoverAtStartupAsync), so
// a deploy started all the background reads in the same second. The background reads among them (priority above the hot
// lane, not an order read, not an invoice job or a write) are spread over the next few minutes at a stable offset per
// connection and job type. Order reads, webhook ingest, user-started jobs, invoice jobs and price/stock writes start at once.
public static class StartupSpreadPolicy
{
    public const int DefaultSeconds = 180;

    public static bool Spreads(string jobType, int priority, int hotPriorityCeiling, bool isOrderRead, bool isWrite) =>
        priority > hotPriorityCeiling && !isOrderRead && !isWrite && !IsInvoiceJob(jobType) && !IsStockOrPriceJob(jobType);

    public static TimeSpan Delay(Guid? connectionId, string jobType, int spreadSeconds)
    {
        if (spreadSeconds <= 0) return TimeSpan.Zero;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"startup:{connectionId:N}:{jobType}"));
        return TimeSpan.FromSeconds(BitConverter.ToUInt32(digest, 0) % (uint)spreadSeconds);
    }

    private static bool IsInvoiceJob(string jobType) => jobType.Contains("INVOICE", StringComparison.Ordinal) || jobType.StartsWith("EFATURAM_", StringComparison.Ordinal);

    private static bool IsStockOrPriceJob(string jobType) =>
        jobType is MarketplaceJobTypes.StockReconciliation or MarketplaceJobTypes.StockProjectionDispatch or MarketplaceJobTypes.PriceInventorySync;
}

// Item 3 of queue phase 1A: while a connection's marketplace is failing (a scheduled read of the connection waits for its next
// try after no answer, a timeout or a 5xx) the scheduler queues no new background poll for it; a quota refusal of a scheduled
// read, or a quota backoff of the connection + lock group, holds the polls of that lock group only. The scheduler queues no new background poll for it. Those polls would only fail on top of the retry chain (live
// 27 Nov 2026: PttAVM's return and recovery reads were queued during the order read's retries). Order reads keep their own
// rule (one queued per connection, also while it retries), so a new-order read is never held back here. The held polls stay
// due and are queued on the first pass after the retry ends.
// Review of 27 Nov 2026: only a retrying SCHEDULED READ tells that the marketplace is down. A user's write or an invoice
// delivery retrying on its own endpoint, or a quota refusal (which can defer for a day), says nothing about the reads; a
// quota refusal holds only the polls of its own lock group (as connection_backoffs do).
public static class FailingConnectionPollPolicy
{
    // No answer, a timeout or a 5xx from the marketplace: the connection's reads would fail too.
    public static bool IsMarketplaceOutage(string? errorCode) =>
        errorCode is not null && !ApiHealthSignalPolicy.IsRateLimited(errorCode)
        && (ApiHealthSignalPolicy.IsRemoteSide(errorCode) || ConnectionRetestPolicy.IsTransient(errorCode));

    // A quota refusal: holds the polls of the same lock group only.
    public static bool IsQuotaRefusal(string? errorCode) => ApiHealthSignalPolicy.IsRateLimited(errorCode);

    public static bool Holds(bool isOrderRead, bool isWrite) => !isOrderRead && !isWrite;
}
