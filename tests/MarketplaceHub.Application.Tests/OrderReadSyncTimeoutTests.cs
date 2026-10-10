using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OrderReadSyncTimeoutTests
{
    [Theory]
    [InlineData(MarketplaceJobTypes.OrderSync)]
    [InlineData(MarketplaceJobTypes.OrderRecoverySync)]
    [InlineData(MarketplaceJobTypes.OrderStatusSync)]
    [InlineData(MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation)]
    [InlineData(MarketplaceJobTypes.OrderReconciliation)]
    [InlineData(MarketplaceJobTypes.OrderInvoiceReconciliation)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderSync)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderRecoverySync)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderStatusSync)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderReconciliation)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderSync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderRecoverySync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderStatusSync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation)]
    public void OrderReadJobs_UseBoundedExecution(string jobType)
    {
        Assert.True(MarketplaceHub.Worker.Worker.IsOrderReadSyncJob(jobType));
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.OrderInvoiceReconciliation)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation)]
    public void InvoiceReadJobs_UseTheirLongerBoundedExecution(string jobType)
    {
        Assert.True(MarketplaceHub.Worker.Worker.IsOrderInvoiceReconciliationJob(jobType));
        Assert.True(MarketplaceHub.Worker.Worker.StopLeaseHeartbeatOnExecutionTimeout(jobType));
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.OrderStatusSync)]
    [InlineData(MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation)]
    [InlineData(MarketplaceJobTypes.OrderSync)]
    [InlineData(MarketplaceJobTypes.OrderInvoiceReconciliation)]
    public void TimedOutOrderReadJobs_LeaveTimeForTheNextRead(string jobType)
    {
        Assert.Equal(TimeSpan.FromMinutes(3), MarketplaceHub.Worker.Worker.RetryDelayAfterOrderReadTimeout(jobType));
    }

    [Fact]
    public void TimedOutWriteJobs_DoNotReceiveOrderReadBackoff()
    {
        Assert.Null(MarketplaceHub.Worker.Worker.RetryDelayAfterOrderReadTimeout(MarketplaceJobTypes.StageTestOrder));
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.StageTestOrder)]
    [InlineData(MarketplaceJobTypes.ShipmentAction)]
    [InlineData(MarketplaceJobTypes.ReturnAction)]
    public void MarketplaceWriteJobs_AreNotOrderReadTimeoutJobs(string jobType)
    {
        Assert.False(MarketplaceHub.Worker.Worker.IsOrderReadSyncJob(jobType));
        Assert.False(MarketplaceHub.Worker.Worker.StopLeaseHeartbeatOnExecutionTimeout(jobType));
    }
}
