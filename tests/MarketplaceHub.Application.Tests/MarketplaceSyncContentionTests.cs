using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSyncContentionTests
{
    [Fact]
    public void QuotaRejection_KeepsTheAttemptAndBacksOffTheConnection()
    {
        var limited = JobExecutionResult.FromAdapterError(new AdapterError(AdapterErrorClass.RateLimit, "REMOTE_RATE_LIMITED", "Platform hız sınırı yanıtı verdi.", 429, TimeSpan.FromSeconds(12), null));
        var limitedWithoutDelay = JobExecutionResult.FromAdapterError(new AdapterError(AdapterErrorClass.RateLimit, "REMOTE_RATE_LIMITED", "x", 429, null, null));
        var timeout = JobExecutionResult.FromAdapterError(new AdapterError(AdapterErrorClass.TransientNetwork, "REMOTE_TIMEOUT", "x", null, TimeSpan.FromSeconds(5), null));

        Assert.Equal(JobCompletionKind.Retry, limited.Kind);
        Assert.True(limited.RefundAttempt);
        Assert.True(limited.BackOffConnection);
        Assert.Equal(TimeSpan.FromSeconds(12), limited.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(30), limitedWithoutDelay.RetryAfter);
        Assert.False(timeout.RefundAttempt);
        Assert.False(timeout.BackOffConnection);
    }

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
    public void InvoiceReadsShareTheOrderLaneSoTheyNeverRaceOrderWrites()
    {
        // Trendyol's invoice check writes only invoice columns without a version check (audit 2026-10-20 G14).
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
    [InlineData(MarketplaceJobTypes.WebhookIngest, null)]
    [InlineData(MarketplaceJobTypes.ShopifyWebhookIngest, null)]
    [InlineData(MarketplaceJobTypes.HepsiburadaWebhookIngest, null)]
    [InlineData(MarketplaceJobTypes.ExternalEffectReconcile, null)]
    [InlineData(MarketplaceJobTypes.ExternalEffectReconcile, "user")]
    public void ShortJobsWaitForTheLockAndComeBackWithinSeconds(string jobType, string? triggerType)
    {
        // Audit 2026-10-24 Q1: simultaneous webhooks used to be deferred 30 s each and collide again.
        Assert.True(MarketplaceSyncExecutionLock.WaitsForLock(jobType));
        var result = MarketplaceSyncExecutionLock.ContentionResult(jobType, "{}", triggerType, sameTypeRunning: false);

        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.True(result.RefundAttempt);
        Assert.Equal("CONNECTION_OPERATION_BUSY", result.ErrorCode);
        Assert.Equal(MarketplaceSyncExecutionLock.ShortJobRetryDelay, result.RetryAfter);
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.OrderSync)]
    [InlineData(MarketplaceJobTypes.ShipmentAction)]
    [InlineData(MarketplaceJobTypes.ProductSync)]
    public void LongerJobsTryTheLockOnce(string jobType) =>
        Assert.False(MarketplaceSyncExecutionLock.WaitsForLock(jobType));

    [Fact]
    public void ADeferralIsSpreadUpwardsAndBounded()
    {
        var ids = Enumerable.Range(0, 50).Select(index => new Guid($"0199a5a1-1c00-7000-8000-{index:D12}")).ToArray();
        var shortDelays = ids.Select(id => JobLeaseService.DeferralDelay(TimeSpan.FromSeconds(1), id, 3)).ToArray();
        // One second asked: 1-4 s (at least a 3 s spread), never sooner than asked.
        Assert.All(shortDelays, delay => Assert.InRange(delay, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)));
        Assert.True(shortDelays.Distinct().Count() > 40);
        Assert.True(shortDelays.Max() - shortDelays.Min() > TimeSpan.FromSeconds(2));
        // 30 s asked: up to 30 % more.
        Assert.All(ids, id => Assert.InRange(JobLeaseService.DeferralDelay(TimeSpan.FromSeconds(30), id, 3), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(39)));
        // An hour asked: at most five minutes more.
        Assert.All(ids, id => Assert.InRange(JobLeaseService.DeferralDelay(TimeSpan.FromHours(1), id, 3), TimeSpan.FromHours(1), TimeSpan.FromMinutes(65)));
        // Deterministic for one job and version; a later deferral of the same job lands elsewhere.
        Assert.Equal(JobLeaseService.DeferralDelay(TimeSpan.FromSeconds(1), ids[0], 3), JobLeaseService.DeferralDelay(TimeSpan.FromSeconds(1), ids[0], 3));
        Assert.True(Enumerable.Range(1, 10).Select(version => JobLeaseService.DeferralDelay(TimeSpan.FromSeconds(1), ids[0], version)).Distinct().Count() > 5);
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

public sealed class UserActionLaneTests
{
    [Theory]
    [InlineData(MarketplaceJobTypes.ShipmentAction)]
    [InlineData(MarketplaceJobTypes.ReturnAction)]
    [InlineData(MarketplaceJobTypes.OrderSync)]
    [InlineData(InvoicingJobTypes.InvoiceSubmit)]
    public void UserRequestedActionsRunInTheUserLane(string jobType) =>
        Assert.Equal(JobPriorityPolicy.UserAction, JobPriorityPolicy.ForUser(jobType));

    [Fact]
    public void LongUserRequestedReadsKeepTheirOwnLanes() =>
        Assert.NotEqual(JobPriorityPolicy.UserAction, JobPriorityPolicy.ForUser(MarketplaceJobTypes.ProductSync));

    [Fact]
    public void AUserRequestIsDeferredRatherThanFoldedIntoTheRunningSync()
    {
        Assert.True(MarketplaceSyncExecutionLock.ContentionResult(MarketplaceJobTypes.OrderSync, "{}").Succeeded);
        var user = MarketplaceSyncExecutionLock.ContentionResult(MarketplaceJobTypes.OrderSync, "{}", "user");
        Assert.False(user.Succeeded);
        Assert.True(user.RefundAttempt);
    }
}
