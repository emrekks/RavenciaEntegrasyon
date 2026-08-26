using System.Net;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MandatorySynchronizationScenariosTests
{
    [Fact]
    public void Scenario01_NormalOrder_UsesThirtySecondHotCadenceAndImmediateReservation()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SynchronizationCadence.HotOrders);
        Assert.Equal(2m, OrderInventoryReservationPolicy.DesiredQuantity(2m, 0m));
    }

    [Fact]
    public void Scenario02_SameOrderTenTimes_ProducesStableEventIdentity()
    {
        var occurredAt = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        Assert.Single(Enumerable.Range(0, 10).Select(_ => PackageIngestionSafety.EventId("package-1", occurredAt)).Distinct());
    }

    [Fact]
    public void Scenario03_WorkerOfflineFourHours_RecoversFromTenMinuteOverlap()
    {
        var last = DateTimeOffset.Parse("2026-08-26T14:00:00Z");
        var now = DateTimeOffset.Parse("2026-08-26T18:00:00Z");
        var window = SynchronizationWindowPolicy.Incremental(last, now, TimeSpan.FromMinutes(10));
        Assert.Equal(DateTimeOffset.Parse("2026-08-26T13:50:00Z"), window.Start);
        Assert.Equal(now, window.End);
    }

    [Fact]
    public void Scenario04_WorkerOfflineFortyFiveDays_IsCoveredByChunksWhileHotLaneRemainsSeparate()
    {
        var start = DateTimeOffset.Parse("2026-07-12T00:00:00Z");
        var chunks = SynchronizationWindowPolicy.ForwardChunks(start, start.AddDays(45), TimeSpan.FromDays(14));
        Assert.Equal(4, chunks.Count);
        Assert.All(chunks, chunk => Assert.True(chunk.End - chunk.Start <= TimeSpan.FromDays(14)));
        Assert.NotEqual(MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.OrderRecoverySync);
    }

    [Fact]
    public void Scenario05_PartialCancel_ReleasesOnlyCancelledLine()
    {
        Assert.Equal(new[] { 1m, 0m, 1m }, new[]
        {
            OrderInventoryReservationPolicy.DesiredQuantity(1m, 0m),
            OrderInventoryReservationPolicy.DesiredQuantity(1m, 1m),
            OrderInventoryReservationPolicy.DesiredQuantity(1m, 0m)
        });
    }

    [Fact]
    public void Scenario06_PackageRecreation_KeepsOrderIdentityAndDistinctPackageEvents()
    {
        var at = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        Assert.NotEqual(PackageIngestionSafety.EventId("old-package", at), PackageIngestionSafety.EventId("new-package", at));
    }

    [Fact]
    public void Scenario07_ShippedToDelivered_IsAcceptedAndTerminal()
    {
        Assert.True(ShipmentPackageStateMachine.CanTransition(ShipmentPackageStatus.Shipped, ShipmentPackageStatus.Delivered));
        Assert.False(OpenOrderLifecyclePolicy.ShouldPoll(ShipmentPackageStatus.Delivered));
    }

    [Fact]
    public void Scenario08_NewReturnAfterFortyFiveDays_IsInsideHotReturnFlow()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), SynchronizationCadence.HotReturns);
        Assert.True(OpenReturnLifecyclePolicy.ShouldPoll(ReturnClaimStatus.Requested));
    }

    [Fact]
    public void Scenario09_ReturnOpenFortyFiveDays_RemainsTrackedUntilFinal()
    {
        Assert.True(OpenReturnLifecyclePolicy.ShouldPoll(ReturnClaimStatus.Disputed));
        Assert.False(OpenReturnLifecyclePolicy.ShouldPoll(ReturnClaimStatus.Completed));
        Assert.False(OpenReturnLifecyclePolicy.ShouldPoll(ReturnClaimStatus.Cancelled));
    }

    [Fact]
    public void Scenario10_Api500_IsRetryableWithDurableBackoff()
    {
        var error = TrendyolErrorMapper.FromStatus(HttpStatusCode.InternalServerError, null, "request-1");
        Assert.Equal(AdapterErrorClass.Remote5xx, error.Class);
        Assert.InRange(JobRetryPolicy.DelayAfterAttempt(1, Guid.Empty), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.4));
    }

    [Fact]
    public void Scenario11_Api429_PreservesRetryAfterForQueueScheduling()
    {
        var retryAfter = TimeSpan.FromMinutes(12);
        var error = TrendyolErrorMapper.FromStatus(HttpStatusCode.TooManyRequests, retryAfter, "request-2");
        Assert.Equal(AdapterErrorClass.RateLimit, error.Class);
        Assert.Equal(retryAfter, error.RetryAfter);
    }

    [Fact]
    public void Scenario12_CrashAfterCommit_UsesDeterministicPersistentStockOutboxKey()
    {
        var connection = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var variant = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var key = StockProjectionOutboxPolicy.DedupKey(connection, variant, 9);
        Assert.Equal(key, StockProjectionOutboxPolicy.DedupKey(connection, variant, 9));
        Assert.Contains("v9", key);
    }

    [Fact]
    public void Scenario13_LocalProductSave_IsDirtyWithoutImplicitMarketplaceJobType()
    {
        Assert.True(ProductImportMergePolicy.PreserveLocalChanges(8, 7));
        Assert.NotEqual(MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ProductUpdate);
    }

    [Fact]
    public void Scenario14_ManualMarketplaceUpdate_UsesExplicitQueueType()
    {
        Assert.Equal("TRENDYOL_PRODUCT_UPDATE", MarketplaceJobTypes.ProductUpdate);
        Assert.NotEqual(MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.ProductSync);
    }

    [Fact]
    public void Scenario15_Reimport_DoesNotOverwriteUnpublishedLocalChanges()
    {
        Assert.True(ProductImportMergePolicy.PreserveLocalChanges(12, 10));
        Assert.False(ProductImportMergePolicy.PreserveLocalChanges(10, 10));
    }

    [Fact]
    public void ProductUpdateStatusPolling_UsesAdaptiveTenThirtySixtyMinuteWindows()
    {
        var submitted = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        Assert.Equal(TimeSpan.FromMinutes(2), ProductUpdatePollingPolicy.Delay(submitted, submitted.AddMinutes(9)));
        Assert.Equal(TimeSpan.FromMinutes(5), ProductUpdatePollingPolicy.Delay(submitted, submitted.AddMinutes(10)));
        Assert.Equal(TimeSpan.FromMinutes(15), ProductUpdatePollingPolicy.Delay(submitted, submitted.AddMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(30), ProductUpdatePollingPolicy.Delay(submitted, submitted.AddMinutes(60)));
    }

    [Fact]
    public void RetrySchedule_IsExactlyTwoFiveFifteenThirtySixtySecondsBeforeJitter()
    {
        Assert.Equal(new[] { 2d, 5d, 15d, 30d, 60d }, JobRetryPolicy.DefaultSchedule.Select(x => x.TotalSeconds));
    }
}
