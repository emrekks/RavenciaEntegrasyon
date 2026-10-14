using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSyncContentionTests
{
    [Fact]
    public void ProcessorJobTypes_ListEveryMarketplaceJobConstant()
    {
        // The lease query skips jobs whose lock group is busy using this list; a job type missing
        // from it would silently fall back to lease-then-defer.
        var constants = typeof(MarketplaceJobTypes).GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name != nameof(MarketplaceJobTypes.ActivationBootstrapPrefix))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet();

        Assert.Equal(constants.Order(), MarketplaceJobTypes.ProcessorJobTypes.Order());
        Assert.All(constants, jobType => Assert.True(MarketplaceJobTypes.IsMarketplaceProcessorJob(jobType)));
        Assert.False(MarketplaceJobTypes.IsMarketplaceProcessorJob(InvoicingJobTypes.InvoiceSubmit));
        Assert.False(MarketplaceJobTypes.IsMarketplaceProcessorJob(null));
    }

    [Fact]
    public void InvoiceReadsUseSeparateLaneWhileOrderLifecycleStaysSerializedWithOrderRefreshes()
    {
        Assert.Equal("order-invoices", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.OrderInvoiceReconciliation));
        Assert.Equal("order-cargo-info", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation));
        Assert.Equal("orders", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.OrderSync));
        Assert.Equal("orders", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation));
        Assert.Equal("orders", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation));
        Assert.Equal("orders", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.OrderStatusSync));
        Assert.Equal("orders", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.ShopifyOrderStatusSync));
        Assert.Equal("orders", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.HepsiburadaOrderStatusSync));
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderSync)]
    [InlineData(MarketplaceJobTypes.OrderSync)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderSync)]
    public void TargetedReadRetriesRatherThanReportingSuccessWithoutProcessing(string jobType)
    {
        var result = MarketplaceSyncExecutionLock.ContentionResult(jobType,
            "{\"externalOrderId\":\"4736002251\",\"packageNumber\":\"5467917398\"}");
        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.False(result.Succeeded);
        Assert.Equal("TARGETED_ORDER_SYNC_BUSY", result.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"externalOrderId\":null}")]
    [InlineData("{\"externalOrderId\":\" \"}")]
    public void GeneralScheduledReadsStillCoalesce(string payload)
    {
        Assert.True(MarketplaceSyncExecutionLock.ContentionResult(MarketplaceJobTypes.HepsiburadaOrderSync, payload).Succeeded);
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.ProductCreate)]
    [InlineData(MarketplaceJobTypes.ProductUpdate)]
    [InlineData(MarketplaceJobTypes.ProductArchive)]
    [InlineData(MarketplaceJobTypes.ProductApprovalReconcile)]
    [InlineData(MarketplaceJobTypes.PriceInventorySync)]
    [InlineData(MarketplaceJobTypes.StockProjectionDispatch)]
    [InlineData(MarketplaceJobTypes.ShipmentAction)]
    [InlineData(MarketplaceJobTypes.ReturnAction)]
    [InlineData(MarketplaceJobTypes.WebhookIngest)]
    [InlineData(MarketplaceJobTypes.ReferenceSync)]
    [InlineData(MarketplaceJobTypes.CatalogStructureImport)]
    public void WorkCarryingJobsAreDeferredInsteadOfReportedAsDone(string jobType)
    {
        var result = MarketplaceSyncExecutionLock.ContentionResult(jobType, "{}");

        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.False(result.Succeeded);
        Assert.True(result.RefundAttempt);
        Assert.Equal("CONNECTION_OPERATION_BUSY", result.ErrorCode);
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.OrderSync)]
    [InlineData(MarketplaceJobTypes.OrderStatusSync)]
    [InlineData(MarketplaceJobTypes.ReturnSync)]
    [InlineData(MarketplaceJobTypes.StockReconciliation)]
    [InlineData(MarketplaceJobTypes.ProductSync)]
    [InlineData(MarketplaceJobTypes.QuestionSync)]
    public void RecurringReadsCoalesceWithTheRunningSync(string jobType)
    {
        Assert.True(MarketplaceSyncExecutionLock.ContentionResult(jobType, "{}").Succeeded);
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.OrderInvoiceReconciliation)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation)]
    public void InvoiceReconciliationRetriesInsteadOfDroppingWorkWhenOrderLaneIsBusy(string jobType)
    {
        var result = MarketplaceSyncExecutionLock.ContentionResult(jobType, "{}");

        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.False(result.Succeeded);
        Assert.Equal("ORDER_INVOICE_RECONCILIATION_BUSY", result.ErrorCode);
        Assert.Equal(TimeSpan.FromMinutes(3), result.RetryAfter);
    }
}
