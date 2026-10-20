using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class DashboardOrderVisibilityTests
{
    internal static AppDbContext MetadataOnlyContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
        .Options);

    [Fact]
    public void Pending_and_late_dashboard_queries_exclude_stale_unpacked_Hepsiburada_orders()
    {
        using var db = MetadataOnlyContext();
        var tenantId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

        var pendingSql = DashboardReadService.PendingOrderMetricsQuery(db, tenantId, now).ToQueryString();
        var lateSql = DashboardReadService.LateOrderMetricsQuery(db, tenantId, now).ToQueryString();

        foreach (var sql in new[] { pendingSql, lateSql })
        {
            Assert.Contains("HEPSIBURADA", sql, StringComparison.Ordinal);
            Assert.Contains("NOT EXISTS", sql, StringComparison.Ordinal);
            Assert.Contains("OrderedAt", sql, StringComparison.Ordinal);
            Assert.Contains("shipment_packages", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Late_orders_query_shifts_the_Trendyol_ship_by_time_by_its_stored_offset()
    {
        using var db = MetadataOnlyContext();
        var sql = DashboardReadService.LateOrderMetricsQuery(db, Guid.NewGuid(), new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)).ToQueryString();

        Assert.Contains("ShipmentDueAt", sql, StringComparison.Ordinal);
        // Both "now" and "now + 3 h" are compared; the shifted one only for the wall-clock platforms.
        Assert.Contains("2026-10-20T12:00:00", sql, StringComparison.Ordinal);
        Assert.Contains("2026-10-20T15:00:00", sql, StringComparison.Ordinal);
        Assert.Contains("TRENDYOL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_returns_query_applies_the_Hepsiburada_return_history_rule()
    {
        using var db = MetadataOnlyContext();
        var sql = DashboardReadService.PendingReturnMetricsQuery(db, Guid.NewGuid(), new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)).ToQueryString();

        Assert.Contains("HEPSIBURADA", sql, StringComparison.Ordinal);
        Assert.Contains("OrderedAt", sql, StringComparison.Ordinal);
        // 30 days of Hepsiburada return history before now.
        Assert.Contains("2026-09-20T12:00:00", sql, StringComparison.Ordinal);
    }
}
