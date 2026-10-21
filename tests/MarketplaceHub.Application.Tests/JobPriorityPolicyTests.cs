using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
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

    [Fact]
    public void BulkUserWorkSharesTheUserLaneButIsLeasedAfterSingleActions()
    {
        // Lower runs first: a single click is leased before any queued bulk job, and bulk work never
        // falls into the initial-data-sync lane (-1).
        Assert.True(JobPriorityPolicy.UserAction < JobPriorityPolicy.BulkUserAction);
        Assert.True(JobPriorityPolicy.BulkUserAction < JobPriorityPolicy.InitialDataSync);
        Assert.Equal("user", WorkerLanes.LaneFor(InvoicingJobTypes.InvoiceSubmit, JobPriorityPolicy.UserAction));
        Assert.Equal("user", WorkerLanes.LaneFor(InvoicingJobTypes.InvoiceSubmit, JobPriorityPolicy.BulkUserAction));
        Assert.Equal("user", WorkerLanes.LaneFor(InvoicingJobTypes.MarketplaceDelivery, JobPriorityPolicy.BulkUserAction));
    }

    [Fact]
    public void ASingleInvoiceRequestKeepsTheUserPriorityAndABulkOneYields()
    {
        Assert.Equal(JobPriorityPolicy.UserAction, InvoicingBillingService.InvoiceJobPriority(InvoicingJobTypes.InvoiceSubmit));
        Assert.Equal(JobPriorityPolicy.UserAction, InvoicingBillingService.InvoiceJobPriority(InvoicingJobTypes.MarketplaceDelivery, bulk: false));
        Assert.Equal(JobPriorityPolicy.BulkUserAction, InvoicingBillingService.InvoiceJobPriority(InvoicingJobTypes.InvoiceSubmit, bulk: true));
        Assert.Equal(JobPriorityPolicy.BulkUserAction, InvoicingBillingService.InvoiceJobPriority(InvoicingJobTypes.MarketplaceDelivery, bulk: true));
        Assert.Equal(0, InvoicingBillingService.InvoiceJobPriority(InvoicingJobTypes.InvoiceReconcile, bulk: true));
    }

    [Theory]
    [InlineData(JobPriorityPolicy.UserAction, JobPriorityPolicy.UserAction)]
    [InlineData(JobPriorityPolicy.BulkUserAction, JobPriorityPolicy.BulkUserAction)]
    [InlineData(null, JobPriorityPolicy.BulkUserAction)]
    public void AnInvoiceFollowUpKeepsThePaceOfItsSubmission(int? submitPriority, int expected)
    {
        Assert.Equal(expected, JobPriorityPolicy.ForFollowUp(submitPriority));
    }
}
