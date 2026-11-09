using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Speed plan items 8, 9, 11 (10 Nov 2026): the pure rules behind the write reductions.
public sealed class SyncWriteReductionTests
{
    [Theory]
    [InlineData("""{"a":1,"b":"İstanbul"}""", """{ "b": "İstanbul", "a": 1 }""", true)]
    [InlineData("""{"a":{"x":[1,2]}}""", """{"a":{"x":[1,2]}}""", true)]
    [InlineData("""{"a":[1,2]}""", """{"a":[2,1]}""", false)]
    [InlineData("""{"a":1}""", """{"a":2}""", false)]
    [InlineData("""{"a":null}""", """{}""", false)]
    [InlineData("not json", "not json", true)]
    [InlineData("not json", "{}", false)]
    public void SnapshotJsonIsComparedByContent(string left, string right, bool same) =>
        Assert.Equal(same, SyncNoOpWrites.SameJson(left, right));

    [Fact]
    public void OnlyRuleReadTimesAreCheckStamps()
    {
        Assert.True(SyncNoOpWrites.IsBookkeeping(typeof(ShipmentPackage), nameof(ShipmentPackage.MarketplaceInvoiceObservedAt)));
        Assert.True(SyncNoOpWrites.IsBookkeeping(typeof(MarketplaceQuestion), nameof(MarketplaceQuestion.LastSyncedAt)));
        Assert.True(SyncNoOpWrites.IsBookkeeping(typeof(Order), nameof(Order.UpdatedAt)));
        Assert.False(SyncNoOpWrites.IsBookkeeping(typeof(Order), nameof(Order.DerivedStatus)));
        Assert.False(SyncNoOpWrites.IsBookkeeping(typeof(ShipmentPackage), nameof(ShipmentPackage.MarketplaceInvoiceStatus)));
        Assert.False(SyncNoOpWrites.IsBookkeeping(typeof(Order), nameof(Order.LastRemoteModifiedAt)));
    }

    [Fact]
    public void ReconcilePositionKeepsMicroseconds()
    {
        var orderedAt = new DateTimeOffset(2026, 11, 9, 21, 15, 3, TimeSpan.Zero).AddTicks(1234560);
        var id = Guid.CreateVersion7();
        Assert.Equal((orderedAt, id), MarketplaceJobProcessor.OrderReconcilePosition(MarketplaceJobProcessor.OrderReconcilePosition(orderedAt, id)));
        Assert.Null(MarketplaceJobProcessor.OrderReconcilePosition((string?)null));
        Assert.Null(MarketplaceJobProcessor.OrderReconcilePosition("{\"window\":1}"));
    }

    [Fact]
    public void OnlyScansThatFollowOpenPackagesWaitForOne()
    {
        Assert.True(ScheduledJobProducer.LifecycleFollowsOpenPackagesOnly(MarketplaceJobTypes.PazaramaOrderStatusSync));
        Assert.True(ScheduledJobProducer.LifecycleFollowsOpenPackagesOnly(MarketplaceJobTypes.PttAvmOrderStatusSync));
        Assert.True(ScheduledJobProducer.LifecycleFollowsOpenPackagesOnly(MarketplaceJobTypes.ShopifyOrderStatusSync));
        Assert.False(ScheduledJobProducer.LifecycleFollowsOpenPackagesOnly(MarketplaceJobTypes.OrderStatusSync));
        Assert.False(ScheduledJobProducer.LifecycleFollowsOpenPackagesOnly(MarketplaceJobTypes.HepsiburadaOrderStatusSync));
        Assert.False(ScheduledJobProducer.LifecycleFollowsOpenPackagesOnly(MarketplaceJobTypes.PazaramaOrderSync));

        var tenantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        ShipmentPackage Package(ShipmentPackageStatus status, Guid? connection = null) => new() { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connection ?? connectionId, OrderId = Guid.NewGuid(), ExternalPackageId = Guid.NewGuid().ToString("N"), Status = status, RawStatus = status.ToString() };
        var settled = new[] { Package(ShipmentPackageStatus.Delivered), Package(ShipmentPackageStatus.Cancelled), Package(ShipmentPackageStatus.Returned), Package(ShipmentPackageStatus.New, Guid.NewGuid()) };
        Assert.Empty(MarketplaceJobProcessor.OpenPackages(settled.AsQueryable(), tenantId, connectionId));
        Assert.Single(MarketplaceJobProcessor.OpenPackages(settled.Append(Package(ShipmentPackageStatus.Shipped)).AsQueryable(), tenantId, connectionId));
    }

    [Fact]
    public void BrandRowsAreComparedColumnForColumn()
    {
        var item = new RemoteReferenceItem("BRANDS", "101", null, "Ravencia", "Ravencia", 0, false, true, """{"id":101}""");
        var digest = MarketplaceJobProcessor.RemoteBrandDigest(item);
        Assert.Equal(digest, MarketplaceJobProcessor.RemoteBrandDigest(item with { }));
        Assert.NotEqual(digest, MarketplaceJobProcessor.RemoteBrandDigest(item with { Name = "Ravencia " }));
        Assert.NotEqual(digest, MarketplaceJobProcessor.RemoteBrandDigest(item with { IsActive = false }));
        Assert.NotEqual(digest, MarketplaceJobProcessor.RemoteBrandDigest(item with { RawJson = """{"id":101,"x":1}""" }));
        var stored = new MarketplaceJobProcessor.StoredBrandList(Guid.NewGuid(), "hash", [digest]);
        Assert.True(MarketplaceJobProcessor.SameBrandRow(stored, 0, item));
        Assert.False(MarketplaceJobProcessor.SameBrandRow(stored, 1, item));
    }
}
