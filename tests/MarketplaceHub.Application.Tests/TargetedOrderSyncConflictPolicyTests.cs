using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TargetedOrderSyncConflictPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("different-package")]
    public void PackageRefreshPromotesPendingSameOrderReadWithoutMatchingPackage(string? existingPackage)
    {
        var result = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync, "4736002251",
            MarketplaceJobTypes.HepsiburadaOrderSync, "4736002251",
            JobStatus.Pending, false, "5467917398", existingPackage);
        Assert.Equal(TargetedOrderSyncConflictResolution.PromotePending, result);
    }

    [Theory]
    [InlineData(JobStatus.Leased, true)]
    [InlineData(JobStatus.RetryScheduled, true)]
    [InlineData(JobStatus.Pending, true)]
    public void PackageRefreshDoesNotReuseStartedOrderOnlyRead(JobStatus status, bool started)
    {
        var result = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync, "4736002251",
            MarketplaceJobTypes.HepsiburadaOrderSync, "4736002251",
            status, started, "5467917398", null);
        Assert.Equal(TargetedOrderSyncConflictResolution.Reject, result);
    }

    [Fact]
    public void PackageRefreshReusesMatchingOrderAndPackage()
    {
        var result = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync, "4736002251",
            MarketplaceJobTypes.HepsiburadaOrderSync, "4736002251",
            JobStatus.Leased, true, "5467917398", " 5467917398 ");
        Assert.Equal(TargetedOrderSyncConflictResolution.ReuseExisting, result);
    }

    [Fact]
    public void TargetedHepsiburadaReadPromotesAnUnstartedPendingIncrementalRead()
    {
        var resolution = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync,
            "4146480888",
            MarketplaceJobTypes.HepsiburadaOrderSync,
            null,
            JobStatus.Pending,
            conflictingJobHasStarted: false);

        Assert.Equal(TargetedOrderSyncConflictResolution.PromotePending, resolution);
    }

    [Fact]
    public void TargetedTrendyolReadPrefersPromotingPendingOrderScanOverLeasedLifecycleScan()
    {
        var conflicts = new (string JobType, string? ExternalOrderId, JobStatus Status, bool HasStarted, string? PackageNumber)[]
        {
            (MarketplaceJobTypes.OrderStatusSync, null, JobStatus.Leased, true, null),
            (MarketplaceJobTypes.OrderSync, null, JobStatus.Pending, false, null)
        };

        var preferredIndex = TargetedOrderSyncConflictPolicy.PreferredConflictIndex(
            conflicts,
            MarketplaceJobTypes.OrderSync,
            "11376153333");

        Assert.Equal(1, preferredIndex);
        Assert.Equal(
            TargetedOrderSyncConflictResolution.PromotePending,
            TargetedOrderSyncConflictPolicy.Resolve(
                MarketplaceJobTypes.OrderSync,
                "11376153333",
                conflicts[preferredIndex!.Value].JobType,
                conflicts[preferredIndex.Value].ExternalOrderId,
                conflicts[preferredIndex.Value].Status,
                conflicts[preferredIndex.Value].HasStarted));
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.OrderStatusSync)]
    [InlineData(MarketplaceJobTypes.OrderReconciliation)]
    public void TargetedTrendyolReadQueuesBehindActiveOrderLaneJob(string blockerType)
    {
        var resolution = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.OrderSync,
            "11376153333",
            blockerType,
            conflictingExternalOrderId: null,
            conflictingStatus: JobStatus.Leased,
            conflictingJobHasStarted: true);

        Assert.Equal(TargetedOrderSyncConflictResolution.QueueBehindActiveWork, resolution);
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.OrderRecoverySync)]
    [InlineData(MarketplaceJobTypes.OrderStatusSync)]
    [InlineData(MarketplaceJobTypes.OrderReconciliation)]
    public void TargetedTrendyolReadQueuesBehindPendingOrderLaneJob(string blockerType)
    {
        var resolution = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.OrderSync,
            "11376153333",
            blockerType,
            conflictingExternalOrderId: null,
            conflictingStatus: JobStatus.Pending,
            conflictingJobHasStarted: false);

        Assert.Equal(TargetedOrderSyncConflictResolution.QueueBehindActiveWork, resolution);
    }

    [Fact]
    public void TargetedReadReusesSameOrderBeforePromotingAnotherPendingRead()
    {
        var conflicts = new (string JobType, string? ExternalOrderId, JobStatus Status, bool HasStarted, string? PackageNumber)[]
        {
            (MarketplaceJobTypes.OrderSync, "11376153333", JobStatus.Leased, true, null),
            (MarketplaceJobTypes.OrderSync, null, JobStatus.Pending, false, null)
        };

        var preferredIndex = TargetedOrderSyncConflictPolicy.PreferredConflictIndex(
            conflicts,
            MarketplaceJobTypes.OrderSync,
            "11376153333");

        Assert.Equal(0, preferredIndex);
    }

    [Fact]
    public void TargetedReadReusesAnExistingReadForTheSameOrder()
    {
        var resolution = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync,
            "4146480888",
            MarketplaceJobTypes.HepsiburadaOrderSync,
            " 4146480888 ",
            JobStatus.Leased,
            conflictingJobHasStarted: true);

        Assert.Equal(TargetedOrderSyncConflictResolution.ReuseExisting, resolution);
    }

    [Fact]
    public void TargetedReadRejectsAConflictingDifferentOrderRead()
    {
        var resolution = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync,
            "4146480888",
            MarketplaceJobTypes.HepsiburadaOrderSync,
            "4146480889",
            JobStatus.Pending,
            conflictingJobHasStarted: false);

        Assert.Equal(TargetedOrderSyncConflictResolution.Reject, resolution);
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderRecoverySync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderStatusSync)]
    public void TargetedHepsiburadaReadQueuesBehindOtherPendingOrderWork(string conflictingType)
    {
        var resolution = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync,
            "4146480888",
            conflictingType,
            null,
            JobStatus.Pending,
            conflictingJobHasStarted: false);

        Assert.Equal(TargetedOrderSyncConflictResolution.QueueBehindActiveWork, resolution);
    }

    [Fact]
    public void TargetedReadRejectsACrossPlatformOrderConflict()
    {
        var resolution = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync,
            "4146480888",
            MarketplaceJobTypes.OrderSync,
            null,
            JobStatus.Pending,
            conflictingJobHasStarted: false);

        Assert.Equal(TargetedOrderSyncConflictResolution.Reject, resolution);
    }

    [Fact]
    public void TargetedReadRejectsAnAlreadyStartedGeneralRead()
    {
        var resolution = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync,
            "4146480888",
            MarketplaceJobTypes.HepsiburadaOrderSync,
            null,
            JobStatus.Pending,
            conflictingJobHasStarted: true);

        Assert.Equal(TargetedOrderSyncConflictResolution.Reject, resolution);
    }

    [Fact]
    public void TargetedReadDoesNotPromoteACompletedJob()
    {
        var resolution = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync,
            "4146480888",
            MarketplaceJobTypes.HepsiburadaOrderSync,
            null,
            JobStatus.Succeeded,
            conflictingJobHasStarted: false);

        Assert.Equal(TargetedOrderSyncConflictResolution.Reject, resolution);
    }
}
