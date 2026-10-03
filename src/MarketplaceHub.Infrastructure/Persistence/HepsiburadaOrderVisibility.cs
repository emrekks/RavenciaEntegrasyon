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
        return query.Where(order =>
            !db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                && connection.Id == order.ConnectionId
                && connection.PlatformCode == "HEPSIBURADA")
            || !((order.DerivedStatus == "UNVERIFIED" || order.OrderedAt < verificationCutoff)
                && !db.ShipmentPackages.Any(package => package.TenantId == tenantId && package.OrderId == order.Id)));
    }
}
