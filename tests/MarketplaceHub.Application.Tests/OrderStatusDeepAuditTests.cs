using MarketplaceHub.Api.Marketplace;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Deep audit 2026-10-20, orders and shipments: order status of split orders (S3), Shopify's own
// statuses (S4), deadline warnings (S9), allocation rows (S5), the order-level picking target
// (S15), Created echoes after an instant picking (S28) and terminal conflicts (G12).
public sealed class OrderStatusDeepAuditTests
{
    [Theory]
    [InlineData(ShipmentPackageStatus.Delivered, ShipmentPackageStatus.New, ShipmentPackageStatus.New)]
    [InlineData(ShipmentPackageStatus.Shipped, ShipmentPackageStatus.Processing, ShipmentPackageStatus.Processing)]
    [InlineData(ShipmentPackageStatus.Delivered, ShipmentPackageStatus.ReadyToShip, ShipmentPackageStatus.ReadyToShip)]
    [InlineData(ShipmentPackageStatus.ReadyToShip, ShipmentPackageStatus.New, ShipmentPackageStatus.New)]
    [InlineData(ShipmentPackageStatus.Returned, ShipmentPackageStatus.OnHold, ShipmentPackageStatus.OnHold)]
    [InlineData(ShipmentPackageStatus.Delivered, ShipmentPackageStatus.PaymentPending, ShipmentPackageStatus.PaymentPending)]
    [InlineData(ShipmentPackageStatus.PaymentPending, ShipmentPackageStatus.New, ShipmentPackageStatus.New)]
    [InlineData(ShipmentPackageStatus.Delivered, ShipmentPackageStatus.Shipped, ShipmentPackageStatus.Delivered)]
    [InlineData(ShipmentPackageStatus.Cancelled, ShipmentPackageStatus.Delivered, ShipmentPackageStatus.Delivered)]
    public void Aggregate_returns_the_earliest_open_status_while_a_package_needs_work(ShipmentPackageStatus first, ShipmentPackageStatus second, ShipmentPackageStatus expected)
    {
        Assert.Equal(expected, ShipmentPackageStatusPolicy.Aggregate([first, second]));
        Assert.Equal(expected, ShipmentPackageStatusPolicy.Aggregate([second, first]));
    }

    [Fact]
    public void Aggregate_keeps_manual_review_first()
    {
        Assert.Equal(ShipmentPackageStatus.ManualReview, ShipmentPackageStatusPolicy.Aggregate([ShipmentPackageStatus.New, ShipmentPackageStatus.ManualReview]));
    }

    [Theory]
    [InlineData("UNFULFILLED", ShipmentPackageStatus.New)]
    [InlineData("PROCESSING", ShipmentPackageStatus.Processing)]
    [InlineData("FULFILLMENT_FAILED", ShipmentPackageStatus.ManualReview)]
    [InlineData("UNKNOWN", ShipmentPackageStatus.ManualReview)]
    [InlineData("SHIPPED", ShipmentPackageStatus.Shipped)]
    [InlineData("DELIVERED", ShipmentPackageStatus.Delivered)]
    [InlineData("CANCELLED", ShipmentPackageStatus.Cancelled)]
    public void Shopify_statuses_map_to_the_package_lifecycle(string rawStatus, ShipmentPackageStatus expected)
    {
        Assert.Equal(expected, ShopifyShipmentPackageStatusPolicy.FromRemote(rawStatus));
    }

    [Theory]
    [InlineData("NEW", true)]
    [InlineData("PROCESSING", true)]
    [InlineData("READY_TO_SHIP", true)]
    [InlineData("PARTIALLY_CANCELLED", true)]
    [InlineData("RETURNED", false)]
    [InlineData("RETURN_IN_TRANSIT", false)]
    [InlineData("MANUAL_REVIEW", false)]
    [InlineData("ON_HOLD", false)]
    [InlineData("SHIPPED", false)]
    [InlineData("UNDELIVERED", false)]
    [InlineData("PAYMENT_PENDING", false)]
    public void Deadline_warning_is_only_for_orders_still_to_ship(string status, bool expected)
    {
        var now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");

        Assert.Equal(expected, OpenOrderLifecyclePolicy.ShouldShowShipmentDeadlineWarning("TRENDYOL", status, now.AddDays(-40), now));
    }

    [Fact]
    public void Latest_allocation_row_per_package_line_wins_over_older_sync_events()
    {
        var package = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        var rows = new[]
        {
            new MarketplaceSalesService.PackageLineAllocationRow(package, line, "P1:1760000000000", 2, 0),
            new MarketplaceSalesService.PackageLineAllocationRow(package, line, "P1:1760000500000", 1, 1),
            new MarketplaceSalesService.PackageLineAllocationRow(package, line, "seed", 9, 0),
            new MarketplaceSalesService.PackageLineAllocationRow(other, line, "P2:1760000000001", 1, 0)
        };

        var latest = MarketplaceSalesService.LatestPackageLineAllocations(rows);

        Assert.Equal(2, latest.Count);
        Assert.Equal("P1:1760000500000", Assert.Single(latest, row => row.PackageId == package).SourceEventId);
        Assert.Equal(2m, latest.Sum(row => row.AllocatedQuantity));
    }

    [Fact]
    public void Order_level_picking_targets_the_new_package_of_a_split_order()
    {
        var orderId = Guid.CreateVersion7();
        var shipped = new ShipmentView(Guid.CreateVersion7(), orderId, "1", "A", "SHIPPED", "Shipped", "T1", DateTimeOffset.UtcNow, 3);
        var fresh = new ShipmentView(Guid.CreateVersion7(), orderId, "1", "B", "NEW", "Created", null, DateTimeOffset.UtcNow, 1);

        Assert.Equal(fresh.Id, MarketplaceEndpoints.PickingTarget([shipped, fresh])!.Id);
        Assert.Equal(shipped.Id, MarketplaceEndpoints.PickingTarget([shipped])!.Id);
        Assert.Null(MarketplaceEndpoints.PickingTarget([]));
    }

    [Fact]
    public void A_created_read_right_after_an_instant_picking_is_an_echo()
    {
        var now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");

        Assert.True(PackageIngestionSafety.IsCreatedEchoAfterInstantPicking(ShipmentPackageStatus.Processing, ShipmentPackageStatus.New, now.AddMinutes(-5), now));
        Assert.False(PackageIngestionSafety.IsCreatedEchoAfterInstantPicking(ShipmentPackageStatus.Processing, ShipmentPackageStatus.New, now.AddHours(-2), now));
        Assert.False(PackageIngestionSafety.IsCreatedEchoAfterInstantPicking(ShipmentPackageStatus.Processing, ShipmentPackageStatus.New, null, now));
        Assert.False(PackageIngestionSafety.IsCreatedEchoAfterInstantPicking(ShipmentPackageStatus.Processing, ShipmentPackageStatus.Shipped, now.AddMinutes(-5), now));
    }

    [Theory]
    [InlineData(ShipmentPackageStatus.Cancelled, ShipmentPackageStatus.Delivered, false)]
    [InlineData(ShipmentPackageStatus.Returned, ShipmentPackageStatus.Cancelled, false)]
    [InlineData(ShipmentPackageStatus.Shipped, ShipmentPackageStatus.Cancelled, true)]
    [InlineData(ShipmentPackageStatus.Processing, ShipmentPackageStatus.Shipped, false)]
    public void Terminal_conflict_never_reopens_a_closed_package(ShipmentPackageStatus current, ShipmentPackageStatus incoming, bool expected)
    {
        Assert.Equal(expected, PackageIngestionSafety.IsTerminalConflict(current, incoming, incoming.ToString()));
    }

    // S22: a Hepsiburada package the panel unpacked is superseded history, not a live cancelled package.
    [Fact]
    public void Locally_unpacked_package_no_longer_decides_the_order()
    {
        var orderId = Guid.CreateVersion7();
        ShipmentPackage Package(string id, ShipmentPackageStatus status, string raw) => new() { Id = Guid.CreateVersion7(), OrderId = orderId, ExternalPackageId = id, Status = status, RawStatus = raw };
        var unpacked = Package("HB1", ShipmentPackageStatus.Cancelled, ShipmentPackageSupersession.LocallyUnpackedRawStatus);
        var other = Package("HB2", ShipmentPackageStatus.Shipped, "Shipped");

        Assert.Contains("HB1", ShipmentPackageSupersession.SupersededExternalIds([unpacked, other]));
        Assert.Equal([ShipmentPackageStatus.Shipped], ShipmentPackageSupersession.StatusSources([unpacked, other]));
        Assert.Empty(ShipmentPackageSupersession.StatusSources([unpacked])!);
        Assert.Equal(ShipmentPackageStatus.New, ShipmentPackageStatusPolicy.Aggregate(ShipmentPackageSupersession.StatusSources([unpacked])!));
    }

    // S37: carriers are matched by code or name, not by a substring of another carrier's name.
    [Theory]
    [InlineData("ARASMP", true, true)]
    [InlineData("Aras Kargo Marketplace", true, true)]
    [InlineData("TEXMP", true, false)]
    [InlineData("Trendyol Express Marketplace", true, false)]
    [InlineData("HepsiJet", false, true)]
    [InlineData("HEPSIJET KARGO", false, true)]
    [InlineData("Karasu Lojistik", false, false)]
    [InlineData("TEXTIL KARGO", false, false)]
    [InlineData("Yurtiçi Kargo", false, false)]
    [InlineData(null, false, false)]
    public void Common_label_carrier_is_matched_by_code_or_name(string? carrier, bool trendyol, bool hepsiburada)
    {
        Assert.Equal(trendyol, CommonLabelCarrierPolicy.Supports(carrier));
        Assert.Equal(hepsiburada, CommonLabelCarrierPolicy.SupportsHepsiburada(carrier));
    }

    // S7: the shipment detail offers only the actions the package state allows.
    [Theory]
    [InlineData(ShipmentPackageStatus.New, "PICKING", true)]
    [InlineData(ShipmentPackageStatus.Processing, "PICKING", false)]
    [InlineData(ShipmentPackageStatus.Processing, "CANCEL_ITEMS", true)]
    [InlineData(ShipmentPackageStatus.Shipped, "CANCEL_ITEMS", false)]
    [InlineData(ShipmentPackageStatus.Delivered, "MANUAL_DELIVER", false)]
    [InlineData(ShipmentPackageStatus.Shipped, "MANUAL_DELIVER", true)]
    [InlineData(ShipmentPackageStatus.New, "MANUAL_RETURN", false)]
    [InlineData(ShipmentPackageStatus.Undelivered, "MANUAL_RETURN", true)]
    [InlineData(ShipmentPackageStatus.Shipped, "ALTERNATIVE_DELIVERY", false)]
    [InlineData(ShipmentPackageStatus.Cancelled, "CHANGE_CARGO_PROVIDER", false)]
    public void Shipment_actions_follow_the_package_state(ShipmentPackageStatus status, string action, bool allowed)
    {
        var package = new ShipmentPackage { ExternalPackageId = "P", RawStatus = status.ToString(), Status = status };

        Assert.Equal(allowed, MarketplaceSalesService.ShipmentActionStateError(package, action) is null);
    }

    // S18: the shipments page filter takes the wire names; an unknown value is refused, not ignored.
    [Theory]
    [InlineData("READY_TO_SHIP", ShipmentPackageStatus.ReadyToShip)]
    [InlineData("ready_to_ship", ShipmentPackageStatus.ReadyToShip)]
    [InlineData("Shipped", ShipmentPackageStatus.Shipped)]
    [InlineData("RETURN_IN_TRANSIT", ShipmentPackageStatus.ReturnInTransit)]
    [InlineData("3", null)]
    [InlineData("LATE", null)]
    public void Shipment_status_filter_maps_wire_names(string value, ShipmentPackageStatus? expected)
    {
        Assert.Equal(expected, MarketplaceSalesService.ShipmentStatusFilter(value));
    }

    [Fact]
    public void Settled_package_reservation_moves_to_its_own_key()
    {
        var id = Guid.Parse("0199f5b4-0000-7000-8000-000000000001");

        Assert.Equal("HEPSIBURADA_PACKAGE_CREATE:a:b:reconciled:0199f5b4000070008000000000000001",
            MarketplaceSalesService.HepsiburadaPackageReservationSettledKey("HEPSIBURADA_PACKAGE_CREATE:a:b", id));
    }
}
