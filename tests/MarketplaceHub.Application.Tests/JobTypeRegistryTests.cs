using MarketplaceHub.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobTypeRegistryTests
{
    private static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    [Theory]
    [InlineData(MarketplaceJobTypes.ProductSync, 60)]
    [InlineData(MarketplaceJobTypes.CatalogStructureImport, 60)]
    [InlineData(MarketplaceJobTypes.OrderInvoiceReconciliation, 15)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation, 15)]
    [InlineData(MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation, 30)]
    [InlineData(MarketplaceJobTypes.OrderStatusSync, 20)]
    [InlineData(MarketplaceJobTypes.OrderSync, 10)]
    [InlineData(MarketplaceJobTypes.PazaramaOrderStatusSync, 10)]
    [InlineData(MarketplaceJobTypes.StageTestOrder, 60)]
    public void EachJobTypeKeepsItsTimeout(string jobType, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), JobTypeRegistry.TimeoutFor(jobType, Empty));

    [Fact]
    public void SettingsChangeTheTimeoutWithinItsBounds()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Worker:OrderStatusSyncTimeoutMinutes"] = "7",
            ["Worker:DefaultJobTimeoutMinutes"] = "1"
        }).Build();

        Assert.Equal(TimeSpan.FromMinutes(7), JobTypeRegistry.TimeoutFor(MarketplaceJobTypes.N11OrderSync, configuration));
        Assert.Equal(TimeSpan.FromMinutes(5), JobTypeRegistry.TimeoutFor(MarketplaceJobTypes.StageTestOrder, configuration));
    }

    [Fact]
    public void AProfileGathersLockGroupPriorityAndTimeoutBehaviour()
    {
        var invoice = JobTypeRegistry.For(MarketplaceJobTypes.OrderInvoiceReconciliation);
        Assert.Equal("order-invoices", invoice.LockGroup);
        Assert.True(invoice.StopHeartbeatOnTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), invoice.RetryAfterTimeout);

        var webhook = JobTypeRegistry.For(MarketplaceJobTypes.WebhookIngest);
        Assert.Equal("orders", webhook.LockGroup);
        Assert.False(webhook.StopHeartbeatOnTimeout);
        Assert.Null(webhook.RetryAfterTimeout);
    }

    [Theory]
    [InlineData(InvoicingJobTypes.InvoiceSubmit)]
    [InlineData(InvoicingJobTypes.InvoiceCancellation)]
    [InlineData(InvoicingJobTypes.MarketplaceDelivery)]
    [InlineData(InvoicingJobTypes.ManualOrderInvoiceDelivery)]
    [InlineData(MarketplaceJobTypes.ShipmentAction)]
    [InlineData(MarketplaceJobTypes.ReturnAction)]
    [InlineData(MarketplaceJobTypes.CommonLabel)]
    [InlineData(MarketplaceJobTypes.ProductCreate)]
    [InlineData(MarketplaceJobTypes.ProductUpdate)]
    [InlineData(MarketplaceJobTypes.ProductArchive)]
    [InlineData(MarketplaceJobTypes.PriceInventorySync)]
    [InlineData(MarketplaceJobTypes.StageTestOrder)]
    public void ExternalWritesFinishOnShutdown(string jobType)
    {
        Assert.True(JobTypeRegistry.IsShutdownProtected(jobType));
        Assert.True(JobTypeRegistry.For(jobType).FinishOnShutdown);
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.OrderSync)]
    [InlineData(MarketplaceJobTypes.ProductSync)]
    [InlineData(MarketplaceJobTypes.OrderInvoiceReconciliation)]
    [InlineData(MarketplaceJobTypes.WebhookIngest)]
    [InlineData(MarketplaceJobTypes.QuestionSync)]
    [InlineData(MarketplaceJobTypes.StockProjectionDispatch)]
    [InlineData(MarketplaceJobTypes.StockReconciliation)]
    [InlineData(MarketplaceJobTypes.ExternalEffectReconcile)]
    [InlineData(MarketplaceJobTypes.ProductApprovalReconcile)]
    [InlineData(InvoicingJobTypes.InvoiceReconcile)]
    [InlineData(InvoicingJobTypes.InvoiceDocumentFetch)]
    [InlineData(InvoicingJobTypes.InvoiceDueScan)]
    [InlineData("IMPORT_APPLY")]
    [InlineData("UNKNOWN_JOB_TYPE")]
    public void ReadsAndScansAreCancelledOnShutdown(string jobType)
    {
        Assert.False(JobTypeRegistry.IsShutdownProtected(jobType));
        Assert.False(JobTypeRegistry.For(jobType).FinishOnShutdown);
    }

    [Fact]
    public void EveryShutdownProtectedJobTypeHasAProcessor()
    {
        string[] invoicingWrites = [InvoicingJobTypes.InvoiceSubmit, InvoicingJobTypes.InvoiceCancellation, InvoicingJobTypes.MarketplaceDelivery, InvoicingJobTypes.ManualOrderInvoiceDelivery];
        Assert.All(JobTypeRegistry.ShutdownProtectedJobTypes, jobType =>
            Assert.True(MarketplaceJobTypes.IsMarketplaceProcessorJob(jobType) || invoicingWrites.Contains(jobType), jobType));
        Assert.Equal(JobTypeRegistry.ShutdownProtectedJobTypes.Count, JobTypeRegistry.ShutdownProtectedJobTypes.Distinct().Count());
    }
}
