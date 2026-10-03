using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class DashboardOrderVisibilityTests
{
    [Fact]
    public void Pending_and_operational_dashboard_queries_exclude_stale_unpacked_Hepsiburada_orders()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;
        using var db = new AppDbContext(options);
        var tenantId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

        var pendingSql = DashboardReadService.PendingOrderMetricsQuery(db, tenantId, now).ToQueryString();
        var operationalSql = DashboardReadService.OperationalOrderMetricsQuery(db, tenantId, now).ToQueryString();

        foreach (var sql in new[] { pendingSql, operationalSql })
        {
            Assert.Contains("HEPSIBURADA", sql, StringComparison.Ordinal);
            Assert.Contains("NOT EXISTS", sql, StringComparison.Ordinal);
            Assert.Contains("OrderedAt", sql, StringComparison.Ordinal);
            Assert.Contains("shipment_packages", sql, StringComparison.Ordinal);
        }
    }
}
