using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// Settles idempotency records past their window, for every tenant at once. A completed record is
/// deleted: its replay window is over. An unfinished one (IN_PROGRESS, UNKNOWN) is evidence that the
/// endpoint may have committed before the process or client failed, so it is kept as UNKNOWN for
/// another seven days instead of being deleted and retried blindly. This used to run as a DELETE and
/// an UPDATE on every keyed mutation; it now runs on an hourly tick.
/// An UNKNOWN record is deleted once it is older than <see cref="UnknownMaximumAge"/>: each sweep used to push its
/// window on by seven days, so it was kept for ever (scan 25 Nov 2026, E-5).
/// </summary>
public static class ApiIdempotencyRetention
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static readonly TimeSpan UnknownRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan UnknownMaximumAge = TimeSpan.FromDays(30);

    public static async Task<(int Deleted, int MarkedUnknown)> SweepAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deleted = await db.ApiIdempotencyRecords
            .Where(x => x.ExpiresAt <= now && x.State == "COMPLETED")
            .ExecuteDeleteAsync(cancellationToken);
        var unknownCutoff = now.Subtract(UnknownMaximumAge).ToUniversalTime();
        deleted += await db.ApiIdempotencyRecords
            .Where(x => x.ExpiresAt <= now && x.State == "UNKNOWN" && x.CreatedAt <= unknownCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        var unknownUntil = now.Add(UnknownRetention);
        var markedUnknown = await db.ApiIdempotencyRecords
            .Where(x => x.ExpiresAt <= now && x.State != "COMPLETED")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, "UNKNOWN")
                .SetProperty(x => x.ExpiresAt, unknownUntil), cancellationToken);
        return (deleted, markedUnknown);
    }
}
