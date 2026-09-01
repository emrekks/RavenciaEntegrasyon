using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class DashboardMetricPolicyTests
{
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
    [InlineData(ReturnClaimStatus.Requested)]
    [InlineData(ReturnClaimStatus.AwaitingShipment)]
    [InlineData(ReturnClaimStatus.InTransit)]
    [InlineData(ReturnClaimStatus.ActionRequired)]
    public void ActionableReturnFlowIsPending(ReturnClaimStatus status) => Assert.True(DashboardMetricPolicy.IsPendingReturn(status));

    [Theory]
    [InlineData(ReturnClaimStatus.Approved)]
    [InlineData(ReturnClaimStatus.Rejected)]
    [InlineData(ReturnClaimStatus.Disputed)]
    [InlineData(ReturnClaimStatus.Completed)]
    [InlineData(ReturnClaimStatus.Cancelled)]
    public void ResolvedOrSeparateReturnQueuesAreNotPending(ReturnClaimStatus status) => Assert.False(DashboardMetricPolicy.IsPendingReturn(status));
}
