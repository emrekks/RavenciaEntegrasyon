using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class DashboardMetricPolicyTests
{
    [Fact]
    public void InvoiceIsDueSoonOnlyDuringFiveToSevenDayReminderWindow()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.True(DashboardMetricPolicy.IsInvoiceDueSoon(now.AddDays(-5), now));
        Assert.True(DashboardMetricPolicy.IsInvoiceDueSoon(now.AddDays(-6), now));
        Assert.False(DashboardMetricPolicy.IsInvoiceDueSoon(now.AddDays(-7), now));
        Assert.False(DashboardMetricPolicy.IsInvoiceDueSoon(now.AddDays(-8), now));
        Assert.False(DashboardMetricPolicy.IsInvoiceDueSoon(now.AddDays(-4), now));
    }

    [Theory]
    [InlineData("NEW")]
    [InlineData("PROCESSING")]
    [InlineData("ON_HOLD")]
    [InlineData("READY_TO_SHIP")]
    [InlineData("PARTIALLY_CANCELLED")]
    [InlineData("MANUAL_REVIEW")]
    public void OnlyUnshippedOrdersCanBeLate(string status) => Assert.True(DashboardMetricPolicy.IsLateOrderStatus(status));

    [Theory]
    [InlineData("SHIPPED")]
    [InlineData("UNDELIVERED")]
    [InlineData("DELIVERED")]
    [InlineData("RETURNED")]
    public void ShippedOrClosedOrdersAreNotLateFulfillmentOrders(string status) => Assert.False(DashboardMetricPolicy.IsLateOrderStatus(status));

    [Theory]
    [InlineData("NEW")]
    [InlineData("PROCESSING")]
    [InlineData("ON_HOLD")]
    [InlineData("READY_TO_SHIP")]
    [InlineData("PARTIALLY_CANCELLED")]
    [InlineData("MANUAL_REVIEW")]
    public void PendingOrdersAreLimitedToWarehouseActionStatuses(string status) => Assert.True(DashboardMetricPolicy.IsPendingOrderStatus(status));

    [Theory]
    [InlineData("SHIPPED")]
    [InlineData("UNDELIVERED")]
    [InlineData("DELIVERED")]
    [InlineData("RETURNED")]
    [InlineData("CANCELLED")]
    public void TransportAndTerminalOrdersAreNotPending(string status) => Assert.False(DashboardMetricPolicy.IsPendingOrderStatus(status));

    [Theory]
    [InlineData(ReturnClaimStatus.ActionRequired)]
    public void OnlyActionRequiredReturnsArePending(ReturnClaimStatus status) => Assert.True(DashboardMetricPolicy.IsPendingReturn(status));

    [Theory]
    [InlineData(ReturnClaimStatus.Requested)]
    [InlineData(ReturnClaimStatus.AwaitingShipment)]
    [InlineData(ReturnClaimStatus.InTransit)]
    [InlineData(ReturnClaimStatus.Approved)]
    [InlineData(ReturnClaimStatus.Rejected)]
    [InlineData(ReturnClaimStatus.Disputed)]
    [InlineData(ReturnClaimStatus.Completed)]
    [InlineData(ReturnClaimStatus.Cancelled)]
    public void ResolvedOrSeparateReturnQueuesAreNotPending(ReturnClaimStatus status) => Assert.False(DashboardMetricPolicy.IsPendingReturn(status));

    [Theory]
    [InlineData(ShipmentPackageStatus.New)]
    [InlineData(ShipmentPackageStatus.Processing)]
    [InlineData(ShipmentPackageStatus.Shipped)]
    [InlineData(ShipmentPackageStatus.Undelivered)]
    [InlineData(ShipmentPackageStatus.Delivered)]
    [InlineData(ShipmentPackageStatus.Returned)]
    [InlineData(ShipmentPackageStatus.ManualReview)]
    public void NonCancelledPackageCanBeInvoiceEligible(ShipmentPackageStatus status) => Assert.True(DashboardMetricPolicy.IsInvoiceEligiblePackage(status));

    [Theory]
    [InlineData(ShipmentPackageStatus.Cancelled)]
    public void CancelledPackagesAreNotInvoiceEligible(ShipmentPackageStatus status) => Assert.False(DashboardMetricPolicy.IsInvoiceEligiblePackage(status));
}
