using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The "waiting for the seller" sets the order and return pages count, in one place so the
// dashboard, the navigation counters and the pages show the same numbers (deep audit
// 2026-10-20 P11, P12).
public static class OrderQueueQueries
{
    // Orders of an active connection whose packages still need work (the "Bekleyen" queue: a
    // split order counts while any package does, see ShipmentPackageStatusPolicy.Aggregate),
    // without the stale unpackaged Hepsiburada orders the order list hides.
    public static IQueryable<Order> PendingOrders(AppDbContext db, Guid tenantId, DateTimeOffset now) =>
        db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == order.ConnectionId
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                && DashboardMetricPolicy.PendingOrderStatuses.Contains(order.DerivedStatus))
            .ExcludeStaleUnpackaged(db, tenantId, now);

    // The return claims the Returns page lists: not cancelled, of an active connection, and for
    // Hepsiburada only within the claim history window its status reads cover.
    public static IQueryable<ReturnClaim> ListedReturns(AppDbContext db, Guid tenantId, DateTimeOffset now)
    {
        var lookbackStart = HepsiburadaReturnHistoryPolicy.InitialStatusChangeStart(now);
        return db.ReturnClaims.AsNoTracking()
            .Where(claim => claim.TenantId == tenantId
                && claim.Status != ReturnClaimStatus.Cancelled
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == claim.ConnectionId
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
            .Where(claim => !db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                    && connection.Id == claim.ConnectionId
                    && connection.PlatformCode == "HEPSIBURADA")
                || db.Orders.Any(order => order.TenantId == tenantId
                    && order.Id == claim.OrderId
                    && order.OrderedAt >= lookbackStart
                    && order.OrderedAt <= now));
    }

    // Listed returns waiting for the seller's decision.
    public static IQueryable<ReturnClaim> PendingReturns(AppDbContext db, Guid tenantId, DateTimeOffset now) =>
        ListedReturns(db, tenantId, now).Where(claim => DashboardMetricPolicy.PendingReturnStatuses.Contains(claim.Status));
}
