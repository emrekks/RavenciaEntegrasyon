using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class HepsiburadaOrderVisibility
{
    internal static IQueryable<Order> ExcludeStaleUnpackaged(
        this IQueryable<Order> query,
        AppDbContext db,
        Guid tenantId,
        DateTimeOffset now)
    {
        var verificationCutoff = OpenOrderLifecyclePolicy.HepsiburadaUnpackagedOrderVerificationCutoff(now);
        // Only an unpackaged order that still looks open can no longer be
        // verified after a month; one cancelled or delivered before packing
        // keeps its final status and stays listed.
        return query.Where(order =>
            !db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                && connection.Id == order.ConnectionId
                && connection.PlatformCode == "HEPSIBURADA")
            || !((order.DerivedStatus == "UNVERIFIED"
                    || order.OrderedAt < verificationCutoff && (order.DerivedStatus == "NEW" || order.DerivedStatus == "ON_HOLD"))
                && !db.ShipmentPackages.Any(package => package.TenantId == tenantId && package.OrderId == order.Id)));
    }
}
