using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Bug scan 9 Nov 2026: a Hepsiburada package reported as Unpacked ("Paketi bozulan") was read as a hold (OnHold), so
// live order 4780777645, cancelled by the customer after packing, was CANCELLED but listed in Askıda through its
// package. Unpacked is a broken-up package that never ships: a superseded split parent, as on Trendyol.
public sealed class HepsiburadaUnpackedPackageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Unpacked")]
    [InlineData("UNPACKED")]
    [InlineData("UnPacked")]
    public void An_unpacked_package_is_a_superseded_split_parent_not_a_hold(string rawStatus)
    {
        Assert.Equal(ShipmentPackageStatus.Cancelled, HepsiburadaShipmentPackageStatusPolicy.FromRemote(rawStatus));
        Assert.True(ShipmentPackageSupersession.IsSplitParentRawStatus(rawStatus));
    }

    [Theory]
    [InlineData("Open", ShipmentPackageStatus.ReadyToShip)]
    [InlineData("InTransit", ShipmentPackageStatus.Shipped)]
    [InlineData("Delivered", ShipmentPackageStatus.Delivered)]
    [InlineData("Undelivered", ShipmentPackageStatus.Undelivered)]
    [InlineData("Cancelled", ShipmentPackageStatus.Cancelled)]
    [InlineData("Awaiting", ShipmentPackageStatus.OnHold)]
    [InlineData("Whatever", ShipmentPackageStatus.ManualReview)]
    public void Every_other_status_keeps_the_shared_mapping(string rawStatus, ShipmentPackageStatus expected)
    {
        Assert.Equal(expected, HepsiburadaShipmentPackageStatusPolicy.FromRemote(rawStatus));
        Assert.Equal(ShipmentPackageStatusPolicy.FromRemote(rawStatus), HepsiburadaShipmentPackageStatusPolicy.FromRemote(rawStatus));
    }

    // The stored OnHold package of 4780777645 moves on at the next package read; a delivered or returned one stays.
    [Theory]
    [InlineData(ShipmentPackageStatus.OnHold, true)]
    [InlineData(ShipmentPackageStatus.ReadyToShip, true)]
    [InlineData(ShipmentPackageStatus.New, true)]
    [InlineData(ShipmentPackageStatus.Delivered, false)]
    [InlineData(ShipmentPackageStatus.Returned, false)]
    public void The_read_supersedes_an_open_package_only(ShipmentPackageStatus current, bool accepted)
    {
        var target = HepsiburadaShipmentPackageStatusPolicy.FromRemote("Unpacked");

        Assert.Equal(accepted, HepsiburadaPackageStatusReconciliationPolicy.ShouldAcceptStatusObservation(current, Now.AddHours(-1), target, Now, isAuthoritativeObservation: true));
        Assert.False(PackageIngestionSafety.IsTerminalConflict(current, target, "Unpacked") && accepted);
    }

    // While the broken-up package is the order's only package, it does not decide the order: the order keeps the
    // status its lifecycle read gave it (cancelled, or new again until it is packed anew).
    [Fact]
    public void A_lone_unpacked_package_does_not_decide_the_order()
    {
        var package = new ShipmentPackage { ExternalPackageId = "5539921191", Status = HepsiburadaShipmentPackageStatusPolicy.FromRemote("Unpacked"), RawStatus = "Unpacked" };

        Assert.Null(ShipmentPackageSupersession.StatusSources([package]));
        Assert.False(ShipmentPackageStatusPolicy.NeedsWork(package.Status));
    }

    // A listed Unpacked package is closed: it needs no tracking read once it names its carrier.
    [Fact]
    public void A_listed_unpacked_package_has_an_end_status()
    {
        var remote = new RemotePackage("5539921191", null, "Unpacked", Now, "MNG", "TRK1", []);

        Assert.True(HepsiburadaHttpClient.HasTerminalListStatus(new RemoteOrderPackage("4780777645", remote)));
    }
}
