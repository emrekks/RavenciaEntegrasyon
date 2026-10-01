using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaPackageStatusReconciliationPolicyTests
{
    [Fact]
    public void ShouldRun_WhenNeverCompleted()
    {
        Assert.True(HepsiburadaPackageStatusReconciliationPolicy.ShouldRun(null, false, DateTimeOffset.UtcNow, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void ShouldRun_ContinuesAnInProgressScanWithoutWaitingForNextInterval()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(HepsiburadaPackageStatusReconciliationPolicy.ShouldRun(now.AddMinutes(-1), true, now, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void ShouldRun_WaitsOneIntervalAfterCompletingTheScan()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(HepsiburadaPackageStatusReconciliationPolicy.ShouldRun(now.AddHours(-23), false, now, TimeSpan.FromDays(1)));
        Assert.True(HepsiburadaPackageStatusReconciliationPolicy.ShouldRun(now.AddDays(-1), false, now, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void StatusTimestamp_PreservesKnownEventDateWhenStatusAlreadyMatches()
    {
        var eventAt = DateTimeOffset.UtcNow.AddDays(-4);

        var result = HepsiburadaPackageStatusReconciliationPolicy.StatusTimestamp(
            ShipmentPackageStatus.Delivered,
            ShipmentPackageStatus.Delivered,
            eventAt,
            null,
            DateTimeOffset.UtcNow);

        Assert.Equal(eventAt, result);
    }

    [Fact]
    public void StatusTimestamp_UsesObservationTimeWhenChangedStateHasNoSourceEventDate()
    {
        var observedAt = DateTimeOffset.UtcNow;

        var result = HepsiburadaPackageStatusReconciliationPolicy.StatusTimestamp(
            ShipmentPackageStatus.OnHold,
            ShipmentPackageStatus.Delivered,
            observedAt.AddDays(-20),
            null,
            observedAt);

        Assert.Equal(observedAt, result);
    }
}
