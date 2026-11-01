using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class DashboardMetricPolicyTests
{
    [Theory]
    [InlineData("NEW")]
    [InlineData("PROCESSING")]
    [InlineData("READY_TO_SHIP")]
    [InlineData("PARTIALLY_CANCELLED")]
    [InlineData("MANUAL_REVIEW")]
    public void OnlyUnshippedOrdersCanBeLate(string status) => Assert.Contains(status, DashboardMetricPolicy.LateOrderStatuses);

    [Theory]
    [InlineData("SHIPPED")]
    [InlineData("UNDELIVERED")]
    [InlineData("ON_HOLD")]
    [InlineData("DELIVERED")]
    [InlineData("RETURNED")]
    public void ShippedOrClosedOrdersAreNotLateFulfillmentOrders(string status) => Assert.DoesNotContain(status, DashboardMetricPolicy.LateOrderStatuses);

    [Theory]
    [InlineData("NEW")]
    [InlineData("PROCESSING")]
    [InlineData("READY_TO_SHIP")]
    [InlineData("PARTIALLY_CANCELLED")]
    [InlineData("MANUAL_REVIEW")]
    public void PendingOrdersAreLimitedToWarehouseActionStatuses(string status) => Assert.Contains(status, DashboardMetricPolicy.PendingOrderStatuses);

    [Theory]
    [InlineData("SHIPPED")]
    [InlineData("UNDELIVERED")]
    [InlineData("ON_HOLD")]
    [InlineData("DELIVERED")]
    [InlineData("RETURNED")]
    [InlineData("CANCELLED")]
    public void TransportAndTerminalOrdersAreNotPending(string status) => Assert.DoesNotContain(status, DashboardMetricPolicy.PendingOrderStatuses);

    [Theory]
    [InlineData(ReturnClaimStatus.ActionRequired)]
    public void OnlyActionRequiredReturnsArePending(ReturnClaimStatus status) => Assert.Contains(status, DashboardMetricPolicy.PendingReturnStatuses);

    [Theory]
    [InlineData(ReturnClaimStatus.Requested)]
    [InlineData(ReturnClaimStatus.AwaitingShipment)]
    [InlineData(ReturnClaimStatus.InTransit)]
    [InlineData(ReturnClaimStatus.Approved)]
    [InlineData(ReturnClaimStatus.Rejected)]
    [InlineData(ReturnClaimStatus.Disputed)]
    [InlineData(ReturnClaimStatus.Completed)]
    [InlineData(ReturnClaimStatus.Cancelled)]
    public void ResolvedOrSeparateReturnQueuesAreNotPending(ReturnClaimStatus status) => Assert.DoesNotContain(status, DashboardMetricPolicy.PendingReturnStatuses);

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

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("canceled")]
    public void CancelledOrdersAreNotInvoiceEligible(string status) => Assert.False(DashboardMetricPolicy.IsInvoiceEligibleOrder(status));

    [Theory]
    [InlineData("NEW")]
    [InlineData("DELIVERED")]
    public void ActiveOrdersRemainInvoiceEligible(string status) => Assert.True(DashboardMetricPolicy.IsInvoiceEligibleOrder(status));

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("returned")]
    [InlineData(" UNDELIVERED ")]
    [InlineData("PAYMENT_PENDING")]
    public void ReturnedUndeliveredAndUnpaidOrdersGetNoNewSaleInvoice(string status) => Assert.True(DashboardMetricPolicy.IsSaleInvoiceBlockedOrder(status));

    [Theory]
    [InlineData("NEW")]
    [InlineData("SHIPPED")]
    [InlineData("DELIVERED")]
    [InlineData(null)]
    public void LiveOrdersCanGetASaleInvoice(string? status) => Assert.False(DashboardMetricPolicy.IsSaleInvoiceBlockedOrder(status));

    [Theory]
    [InlineData(ShipmentPackageStatus.Cancelled)]
    [InlineData(ShipmentPackageStatus.Returned)]
    [InlineData(ShipmentPackageStatus.Undelivered)]
    [InlineData(ShipmentPackageStatus.PaymentPending)]
    public void ReturnedUndeliveredAndUnpaidPackagesGetNoNewSaleInvoice(ShipmentPackageStatus status) => Assert.True(DashboardMetricPolicy.IsSaleInvoiceBlockedPackage(status));

    [Theory]
    [InlineData(ShipmentPackageStatus.New)]
    [InlineData(ShipmentPackageStatus.Shipped)]
    [InlineData(ShipmentPackageStatus.Delivered)]
    public void LivePackagesCanGetASaleInvoice(ShipmentPackageStatus status) => Assert.False(DashboardMetricPolicy.IsSaleInvoiceBlockedPackage(status));
}
