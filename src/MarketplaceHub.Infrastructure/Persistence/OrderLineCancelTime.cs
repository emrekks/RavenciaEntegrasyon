using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// When an order line was cancelled. The time is the marketplace's own (a line's, or a cancelled package's that holds
// the line); without one it stays unknown and is never filled from a nearby date. Separately the panel notes when it
// saw the cancelled quantity grow on a line it already held, which places a cancellation of unknown time.
public static class OrderLineCancelTime
{
    // The cancellation time the read reports for a line: its own, else the latest of the packages that hold it.
    public static DateTimeOffset? Reported(string externalLineId, RemoteOrder remote)
    {
        var own = remote.Lines.Where(line => line.ExternalLineId == externalLineId && line.CancelledAt is not null).Max(line => line.CancelledAt);
        if (own is not null) return own.Value.ToUniversalTime();
        var packaged = remote.Packages
            .Where(package => package.CancelledAt is not null && package.Allocations.Any(allocation => allocation.ExternalLineId == externalLineId))
            .Max(package => package.CancelledAt);
        return packaged?.ToUniversalTime();
    }

    // previousCancelled is the cancelled quantity before this read, null for a line this read added.
    public static void Apply(OrderLine line, decimal? previousCancelled, DateTimeOffset? reported, DateTimeOffset now)
    {
        if (line.CancelledQuantity <= 0)
        {
            line.CancelledAt = null;
            line.CancelSeenAt = null;
            return;
        }
        if (line.CancelledQuantity > (previousCancelled ?? 0m))
        {
            // A new cancellation: its own time, or unknown. It was seen happening only on a line already held.
            line.CancelledAt = reported;
            if (previousCancelled is not null) line.CancelSeenAt = now.ToUniversalTime();
            return;
        }
        // The same cancellation read again: a time the marketplace reports later fills an unknown one, never replaces it.
        line.CancelledAt ??= reported;
    }
}
