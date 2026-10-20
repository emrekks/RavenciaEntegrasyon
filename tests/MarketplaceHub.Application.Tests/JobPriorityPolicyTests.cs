using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobPriorityPolicyTests
{
    private const int HotLaneCeiling = 2;

    [Theory]
    [InlineData(MarketplaceJobTypes.ProductCreate)]
    [InlineData(MarketplaceJobTypes.ProductUpdate)]
    [InlineData(MarketplaceJobTypes.PriceInventorySync)]
    [InlineData(MarketplaceJobTypes.StockProjectionDispatch)]
    [InlineData(MarketplaceJobTypes.ShipmentAction)]
    [InlineData(MarketplaceJobTypes.CommonLabel)]
    [InlineData(MarketplaceJobTypes.OrderSync)]
    [InlineData(MarketplaceJobTypes.WebhookIngest)]
    public void InteractiveAndWriteJobsRunInTheHotLane(string jobType)
    {
        Assert.InRange(JobPriorityPolicy.For(jobType, userInitiated: true), 0, HotLaneCeiling);
        Assert.InRange(JobPriorityPolicy.For(jobType, userInitiated: false), 0, HotLaneCeiling);
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.ProductSync)]
    [InlineData(MarketplaceJobTypes.CatalogStructureImport)]
    [InlineData(MarketplaceJobTypes.OrderRecoverySync)]
    [InlineData(MarketplaceJobTypes.OrderReconciliation)]
    [InlineData(MarketplaceJobTypes.ProductApprovalReconcile)]
    public void LongRunningJobsStayOutOfTheHotLane(string jobType)
    {
        Assert.True(JobPriorityPolicy.For(jobType, userInitiated: true) > HotLaneCeiling);
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderRecoverySync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaReturnSync)]
    public void ManualHepsiburadaReadsAreFasterThanScheduledOnes(string jobType)
    {
        Assert.Equal(JobPriorityPolicy.Interactive, JobPriorityPolicy.For(jobType, userInitiated: true));
        Assert.Equal(JobPriorityPolicy.Default, JobPriorityPolicy.For(jobType, userInitiated: false));
    }

    [Fact]
    public void InvoiceDueScanRunsInTheReconciliationLane()
    {
        Assert.Equal(JobPriorityPolicy.Reconciliation, JobPriorityPolicy.For(InvoicingJobTypes.InvoiceDueScan, userInitiated: false));
        Assert.Equal(JobPriorityPolicy.UserAction, JobPriorityPolicy.ForUser(InvoicingJobTypes.ManualOrderInvoiceDelivery));
    }
}
