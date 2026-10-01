using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSalesStatusTabTests
{
    [Theory]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderRecoverySync, 2)]
    [InlineData(MarketplaceJobTypes.OrderRecoverySync, 6)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderRecoverySync, 6)]
    public void Hepsiburada_manual_full_sync_uses_the_hot_queue(string jobType, int expectedPriority)
    {
        Assert.Equal(expectedPriority, MarketplaceSalesService.Priority(jobType));
    }

    [Theory]
    [InlineData("CANCELLED", ShipmentPackageStatus.Cancelled)]
    [InlineData("SHIPPED", ShipmentPackageStatus.Shipped)]
    [InlineData("DELIVERED", ShipmentPackageStatus.Delivered)]
    public void Package_status_tabs_match_the_status_counter(string tab, ShipmentPackageStatus expected)
    {
        var statuses = MarketplaceSalesService.PackageStatusesForOrderTab(tab);

        Assert.NotNull(statuses);
        Assert.Contains(expected, statuses!);
    }

    [Fact]
    public void Processing_tab_includes_ready_to_ship_packages()
    {
        var statuses = MarketplaceSalesService.PackageStatusesForOrderTab("PROCESSING");

        Assert.Equal([ShipmentPackageStatus.Processing, ShipmentPackageStatus.ReadyToShip], statuses);
    }

    [Fact]
    public void Pending_order_tab_matches_the_dashboard_pending_statuses()
    {
        var statuses = MarketplaceSalesService.DerivedStatusesForOrderTab("PENDING");

        Assert.Equal(DashboardMetricPolicy.PendingOrderStatuses, statuses);
    }

    [Fact]
    public void New_order_filter_includes_unpacked_Hepsiburada_orders_without_package_rows()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;
        using var db = new AppDbContext(options);
        var service = new MarketplaceSalesService(db, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        var tenantId = Guid.NewGuid();
        IQueryable<Order> query = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId);

        service.ApplyOrderFilters(ref query, new OrderListQuery(Status: "NEW"), tenantId);

        var sql = query.ToQueryString();
        Assert.Contains("HEPSIBURADA", sql, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("NEW", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void On_hold_filter_includes_unpacked_Hepsiburada_hold_orders_without_package_rows()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;
        using var db = new AppDbContext(options);
        var service = new MarketplaceSalesService(db, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        var tenantId = Guid.NewGuid();
        IQueryable<Order> query = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId);

        service.ApplyOrderFilters(ref query, new OrderListQuery(Status: "ON_HOLD"), tenantId);

        var sql = query.ToQueryString();
        Assert.Contains("HEPSIBURADA", sql, StringComparison.Ordinal);
        Assert.Contains("ON_HOLD", sql, StringComparison.Ordinal);
        Assert.Contains("Undelivered", sql, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Hepsiburada_undelivered_package_filter_uses_the_hold_tab_instead_of_shipped()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;
        using var db = new AppDbContext(options);
        var service = new MarketplaceSalesService(db, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        var tenantId = Guid.NewGuid();
        IQueryable<Order> shippedQuery = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId);
        IQueryable<Order> holdQuery = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId);

        service.ApplyOrderFilters(ref shippedQuery, new OrderListQuery(Status: "SHIPPED"), tenantId);
        service.ApplyOrderFilters(ref holdQuery, new OrderListQuery(Status: "ON_HOLD"), tenantId);

        var shippedSql = shippedQuery.ToQueryString();
        var holdSql = holdQuery.ToQueryString();
        Assert.Contains("HEPSIBURADA", shippedSql, StringComparison.Ordinal);
        Assert.Contains("Undelivered", shippedSql, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", shippedSql, StringComparison.Ordinal);
        Assert.Contains("HEPSIBURADA", holdSql, StringComparison.Ordinal);
        Assert.Contains("Undelivered", holdSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Delivered_filter_includes_unpacked_Hepsiburada_delivered_orders_but_not_holds()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;
        using var db = new AppDbContext(options);
        var service = new MarketplaceSalesService(db, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        var tenantId = Guid.NewGuid();
        IQueryable<Order> query = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId);

        service.ApplyOrderFilters(ref query, new OrderListQuery(Status: "DELIVERED"), tenantId);

        var sql = query.ToQueryString();
        Assert.Contains("shipment_packages", sql, StringComparison.Ordinal);
        Assert.Contains("HEPSIBURADA", sql, StringComparison.Ordinal);
        Assert.Contains("DELIVERED", sql, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ON_HOLD", sql, StringComparison.Ordinal);
    }
}
