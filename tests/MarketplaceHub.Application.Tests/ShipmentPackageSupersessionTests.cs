using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShipmentPackageSupersessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ShipmentPackageStatus.New)]
    [InlineData(ShipmentPackageStatus.Processing)]
    [InlineData(ShipmentPackageStatus.ReadyToShip)]
    [InlineData(ShipmentPackageStatus.Shipped)]
    [InlineData(ShipmentPackageStatus.OnHold)]
    [InlineData(ShipmentPackageStatus.ManualReview)]
    public void Split_parent_leaves_every_open_state(ShipmentPackageStatus current)
    {
        var target = TrendyolShipmentPackageStatusPolicy.FromRemote("UnPacked");

        Assert.Equal(ShipmentPackageStatus.Cancelled, target);
        Assert.True(PackageIngestionSafety.ShouldAccept(current, Now.AddMinutes(-5), target, Now, "UnPacked"));
    }

    [Theory]
    [InlineData(ShipmentPackageStatus.Delivered)]
    [InlineData(ShipmentPackageStatus.Returned)]
    public void Split_parent_does_not_reopen_an_end_state(ShipmentPackageStatus current)
    {
        Assert.False(PackageIngestionSafety.ShouldAccept(current, Now.AddMinutes(-5), ShipmentPackageStatus.Cancelled, Now, "UnPacked"));
    }

    [Fact]
    public void A_real_cancellation_of_a_shipped_package_is_still_refused_and_is_a_terminal_conflict()
    {
        Assert.False(PackageIngestionSafety.ShouldAccept(ShipmentPackageStatus.Shipped, Now.AddMinutes(-5), ShipmentPackageStatus.Cancelled, Now, "Cancelled"));
        Assert.True(PackageIngestionSafety.IsTerminalConflict(ShipmentPackageStatus.Cancelled, "Cancelled"));
        Assert.False(PackageIngestionSafety.IsTerminalConflict(ShipmentPackageStatus.Cancelled, "UnPacked"));
        Assert.False(PackageIngestionSafety.IsTerminalConflict(ShipmentPackageStatus.New, "Created"));
    }

    [Fact]
    public void Conflict_raw_status_maps_to_manual_review_so_the_review_repair_does_not_flip_it_back()
    {
        var raw = PackageIngestionSafety.ConflictRawStatus("Delivered");

        Assert.Equal(ShipmentPackageStatus.ManualReview, TrendyolShipmentPackageStatusPolicy.FromRemote(raw));
        Assert.Equal(ShipmentPackageStatus.ManualReview, ShipmentPackageStatusPolicy.FromRemote(raw));
        Assert.Equal("REJECTED:P1:1000", PackageIngestionSafety.RejectedEventId("P1:1000"));
    }

    [Fact]
    public void Referenced_parent_does_not_decide_the_order_status()
    {
        var parent = Package("P0", ShipmentPackageStatus.Processing, "Picking");
        var child = Package("P1", ShipmentPackageStatus.Shipped, "Shipped", origin: "P0");

        var statuses = ShipmentPackageSupersession.StatusSources([parent, child]);

        Assert.NotNull(statuses);
        Assert.Equal([ShipmentPackageStatus.Shipped], statuses);
        Assert.Equal(ShipmentPackageStatus.Shipped, ShipmentPackageStatusPolicy.Aggregate(statuses!));
    }

    [Fact]
    public void Lone_split_parent_keeps_the_previous_order_status_until_children_arrive()
    {
        var parent = Package("P0", ShipmentPackageStatus.Cancelled, "UnPacked");

        Assert.Null(ShipmentPackageSupersession.StatusSources([parent]));
    }

    [Fact]
    public void Cancelled_children_cancel_the_order_once_the_parent_is_superseded()
    {
        var parent = Package("P0", ShipmentPackageStatus.Cancelled, "UnPacked");
        var first = Package("P1", ShipmentPackageStatus.Cancelled, "Cancelled", origin: "P0");
        var second = Package("P2", ShipmentPackageStatus.Cancelled, "Cancelled", origin: "P0");

        Assert.Equal(ShipmentPackageStatus.Cancelled, ShipmentPackageStatusPolicy.Aggregate(ShipmentPackageSupersession.StatusSources([parent, first, second])!));
    }

    [Fact]
    public void Line_projection_ignores_a_lone_split_parent_instead_of_cancelling_its_lines()
    {
        var parent = Package("P0", ShipmentPackageStatus.Cancelled, "UnPacked");
        var lineId = Guid.NewGuid();
        var allocation = new PackageLineAllocation { Id = Guid.NewGuid(), PackageId = parent.Id, OrderLineId = lineId, AllocatedQuantity = 0, CancelledQuantity = 2, SourceEventId = PackageIngestionSafety.EventId("P0", parent.StatusOccurredAt) };

        var projected = PackageLineProjectionPolicy.Recalculate([parent], [allocation]);

        Assert.False(projected.ContainsKey(lineId));
    }

    [Fact]
    public void Undelivered_package_keeps_its_shipped_quantity()
    {
        Assert.True(PackageIngestionSafety.TryNormalize(2, new RemotePackageAllocation("l1", 2, 0, 0, 0, 0), ShipmentPackageStatus.Undelivered, out var normalized));

        Assert.Equal(2, normalized.ShippedQuantity);
        Assert.Equal(0, normalized.DeliveredQuantity);
    }

    [Fact]
    public void Order_totals_sum_every_stored_sibling_package_not_only_the_page()
    {
        var first = Package("P1", ShipmentPackageStatus.New, "Created", gross: 300m, net: 280m);
        var second = Package("P2", ShipmentPackageStatus.New, "Created", gross: 200m, net: 200m);

        var totals = OrderAmountProjectionPolicy.FromPackages([first, second], new HashSet<string>(["P2"], StringComparer.Ordinal));

        Assert.Equal((500m, 20m, 480m), totals);
    }

    [Fact]
    public void Order_totals_leave_out_cancelled_and_superseded_packages()
    {
        var parent = Package("P0", ShipmentPackageStatus.Cancelled, "UnPacked", gross: 500m, net: 500m);
        var live = Package("P1", ShipmentPackageStatus.Processing, "Picking", gross: 300m, net: 300m, origin: "P0");
        var cancelled = Package("P2", ShipmentPackageStatus.Cancelled, "Cancelled", gross: 200m, net: 200m, origin: "P0");

        Assert.Equal((300m, 0m, 300m), OrderAmountProjectionPolicy.FromPackages([parent, live, cancelled], new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void Fully_cancelled_order_keeps_the_value_it_was_placed_with()
    {
        var cancelled = Package("P1", ShipmentPackageStatus.Cancelled, "Cancelled", gross: 200m, net: 200m);

        Assert.Equal((200m, 0m, 200m), OrderAmountProjectionPolicy.FromPackages([cancelled], new HashSet<string>(StringComparer.Ordinal)));
    }

    // 11763229493: the payment-pending package "0" and the real package are the same order; it counts once.
    [Fact]
    public void Order_totals_leave_out_the_trendyol_payment_placeholder_once_the_real_package_is_stored()
    {
        var placeholder = Package("0", ShipmentPackageStatus.PaymentPending, "Awaiting", gross: 899m, net: 865.01m);
        var real = Package("4287472684", ShipmentPackageStatus.New, "Created", gross: 899m, net: 865.01m);

        Assert.Equal((899m, 33.99m, 865.01m), OrderAmountProjectionPolicy.FromPackages([placeholder, real], new HashSet<string>(["4287472684"], StringComparer.Ordinal)));
        Assert.Equal((899m, 33.99m, 865.01m), OrderAmountProjectionPolicy.FromPackages([placeholder], new HashSet<string>(["0"], StringComparer.Ordinal)));
    }

    [Fact]
    public void Package_amounts_change_only_with_a_detailed_read_that_names_them()
    {
        var package = Package("P1", ShipmentPackageStatus.Delivered, "Delivered", gross: 1598m, net: 1598m);
        RemotePackage Remote(decimal gross, decimal discount, decimal net, bool observation = false) =>
            new("P1", null, "Delivered", DateTimeOffset.UnixEpoch, null, null, [], gross, discount, net, IsStatusObservation: observation);

        Assert.False(PackageAmountPolicy.Apply(package, Remote(0m, 0m, 0m)));
        Assert.False(PackageAmountPolicy.Apply(package, Remote(1598m, 80.98m, 1517.02m, observation: true)));
        Assert.Equal((1598m, 0m, 1598m), (package.GrossAmount, package.DiscountAmount, package.NetAmount));

        Assert.True(PackageAmountPolicy.Apply(package, Remote(1598m, 80.98m, 1517.02m)));
        Assert.Equal((1598m, 80.98m, 1517.02m), (package.GrossAmount, package.DiscountAmount, package.NetAmount));
        Assert.False(PackageAmountPolicy.Apply(package, Remote(1598m, 80.98m, 1517.02m)));
    }

    [Fact]
    public void Order_totals_are_not_rebuilt_from_a_stored_package_without_amounts()
    {
        var legacy = Package("P1", ShipmentPackageStatus.Shipped, "Shipped", gross: 0m, net: 0m);
        var read = Package("P2", ShipmentPackageStatus.New, "Created", gross: 200m, net: 200m);

        Assert.Null(OrderAmountProjectionPolicy.FromPackages([legacy, read], new HashSet<string>(["P2"], StringComparer.Ordinal)));
    }

    [Fact]
    public void N11_split_children_are_linked_to_the_stored_unpacked_parent_across_pages()
    {
        var parent = Package("P0", ShipmentPackageStatus.Cancelled, "UnPacked");
        var child = Package("P1", ShipmentPackageStatus.Processing, "Picking");
        var alreadyLinked = Package("P2", ShipmentPackageStatus.Processing, "Picking", origin: "P0");

        var linked = N11OrderMapper.LinkStoredSplitPackages([parent, child, alreadyLinked]);

        Assert.Equal([child], linked);
        Assert.Equal("P0", child.OriginExternalPackageId);
        Assert.Null(parent.OriginExternalPackageId);
        Assert.Contains("P0", ShipmentPackageSupersession.SupersededExternalIds([parent, child, alreadyLinked]));
    }

    [Fact]
    public void N11_link_is_not_guessed_when_two_packages_are_unpacked()
    {
        var first = Package("P0", ShipmentPackageStatus.Cancelled, "UnPacked");
        var second = Package("P1", ShipmentPackageStatus.Cancelled, "UnPacked");
        var child = Package("P2", ShipmentPackageStatus.Processing, "Picking");

        Assert.Empty(N11OrderMapper.LinkStoredSplitPackages([first, second, child]));
        Assert.Null(child.OriginExternalPackageId);
    }

    [Fact]
    public void Current_package_query_leaves_out_packages_named_as_an_origin()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;
        using var db = new AppDbContext(options);
        var service = new MarketplaceSalesService(db, null!, null!, null!, null!, null!, null!, TimeProvider.System);

        var sql = service.CurrentShipmentPackages().ToQueryString();

        Assert.Contains("OriginExternalPackageId", sql, StringComparison.Ordinal);
    }

    private static ShipmentPackage Package(string id, ShipmentPackageStatus status, string raw, decimal gross = 0m, decimal net = 0m, string? origin = null) => new()
    {
        Id = Guid.NewGuid(),
        ExternalPackageId = id,
        OriginExternalPackageId = origin,
        Status = status,
        RawStatus = raw,
        StatusOccurredAt = Now,
        GrossAmount = gross,
        DiscountAmount = gross - net,
        NetAmount = net
    };
}
