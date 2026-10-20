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
        // keeps its final status and stays listed. A fresh claim-only order
        // (UNVERIFIED) stays listed too until that month has passed. Unpackaged means without a live
        // package: one the panel unpacked (UNPACKED_BY_PANEL) or a cancelled one does not count (O14),
        // the same rule as the order tabs.
        var currentPackages = MarketplaceSalesService.CurrentShipmentPackages(db);
        return query.Where(order =>
            !db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                && connection.Id == order.ConnectionId
                && connection.PlatformCode == "HEPSIBURADA")
            || !(order.OrderedAt < verificationCutoff
                    && (order.DerivedStatus == "UNVERIFIED" || order.DerivedStatus == "NEW" || order.DerivedStatus == "ON_HOLD")
                && !currentPackages.Any(package => package.TenantId == tenantId && package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)));
    }
}
