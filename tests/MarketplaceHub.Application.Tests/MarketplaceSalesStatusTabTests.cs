using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSalesStatusTabTests
{
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
}
