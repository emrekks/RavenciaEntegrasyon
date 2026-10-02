using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSalesStatusTabTests
{
    [Fact]
    public void Hepsiburada_unpacked_new_order_older_than_one_month_is_unverified_not_delivered()
    {
        var now = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.True(OpenOrderLifecyclePolicy.IsHepsiburadaOrderUnverifiedWithoutPackage(
            "HEPSIBURADA", "NEW", 0, now.AddMonths(-1).AddSeconds(-1), now));
        Assert.False(OpenOrderLifecyclePolicy.IsHepsiburadaOrderUnverifiedWithoutPackage(
            "HEPSIBURADA", "NEW", 0, now.AddMonths(-1).AddSeconds(1), now));
        Assert.False(OpenOrderLifecyclePolicy.IsHepsiburadaOrderUnverifiedWithoutPackage(
            "HEPSIBURADA", "NEW", 1, now.AddMonths(-2), now));
        Assert.False(OpenOrderLifecyclePolicy.IsHepsiburadaOrderUnverifiedWithoutPackage(
            "TRENDYOL", "NEW", 0, now.AddMonths(-2), now));
        Assert.False(OpenOrderLifecyclePolicy.IsHepsiburadaOrderUnverifiedWithoutPackage(
            "HEPSIBURADA", "DELIVERED", 0, now.AddMonths(-2), now));
    }

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
    public void Return_line_model_code_prefers_the_matched_variant_and_falls_back_to_source_snapshot()
    {
        var variant = new ProductVariant
        {
            Sku = "SKU-1",
            SkuNormalized = "SKU-1",
            OptionSignature = "Beden=XL",
            ModelCode = "MODEL-1"
        };

        Assert.Equal("MODEL-1", MarketplaceSalesService.ReturnLineModelCode(variant, "OLD-MODEL"));
        Assert.Equal("OLD-MODEL", MarketplaceSalesService.ReturnLineModelCode(null, "OLD-MODEL"));
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
        Assert.Contains("OrderedAt", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Unverified_order_filter_is_empty_for_Hepsiburada_rows()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;
        using var db = new AppDbContext(options);
        var service = new MarketplaceSalesService(db, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        var tenantId = Guid.NewGuid();
        IQueryable<Order> query = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId);

        service.ApplyOrderFilters(ref query, new OrderListQuery(Status: "UNVERIFIED"), tenantId);

        var sql = query.ToQueryString();
        Assert.Contains("FALSE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Order_list_hides_old_unpacked_Hepsiburada_rows_in_every_status_without_deleting_them()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;
        using var db = new AppDbContext(options);
        var service = new MarketplaceSalesService(db, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        var tenantId = Guid.NewGuid();
        IQueryable<Order> query = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId);

        query = service.ExcludeStaleUnpackagedHepsiburadaOrders(query, tenantId);

        var sql = query.ToQueryString();
        Assert.Contains("UNVERIFIED", sql, StringComparison.Ordinal);
        Assert.Contains("HEPSIBURADA", sql, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("OrderedAt", sql, StringComparison.Ordinal);
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
