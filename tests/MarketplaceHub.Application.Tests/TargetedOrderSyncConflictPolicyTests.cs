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
    [InlineData(MarketplaceJobTypes.OrderSync)]
    public void TargetedReadDoesNotOverwriteOtherOrderWork(string conflictingType)
    {
        var resolution = TargetedOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderSync,
            "4146480888",
            conflictingType,
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
