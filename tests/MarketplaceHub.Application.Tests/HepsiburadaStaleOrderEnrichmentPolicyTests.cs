using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaStaleOrderEnrichmentPolicyTests
{
    [Fact]
    public void StaleOrderReadFillsMissingDetailsWithoutRegressingLifecycleData()
    {
        var modifiedAt = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
        var observedAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var order = new Order
        {
            ExternalOrderId = "HB-1",
            OrderNumber = "HB-1",
            Currency = "TRY",
            GrossAmount = 752m,
            DiscountAmount = 1m,
            NetAmount = 751m,
            OrderedAt = modifiedAt.AddDays(-2),
            LastRemoteModifiedAt = modifiedAt,
            CustomerSnapshotJson = """{"name":"Existing buyer","marketplaceInvoiceStatus":"UNKNOWN"}""",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "SHIPPED",
            CreatedAt = modifiedAt.AddDays(-2),
            UpdatedAt = modifiedAt,
            Version = 4
        };
        var line = new OrderLine
        {
            ExternalLineId = "line-1",
            Sku = "SKU-1",
            TitleSnapshot = "SKU-1",
            SourceSnapshotJson = """{"color":"Blue","imageUrl":""}""",
            OrderedQuantity = 2,
            ShippedQuantity = 1,
            UnitPrice = 376m,
            RawStatus = "Shipped",
            Version = 3
        };
        var remote = new RemoteOrder(
            "HB-1", "HB-1", order.OrderedAt, modifiedAt.AddHours(-1), "TRY", 999m, 20m, 979m,
            """{"name":"Older buyer","marketplaceInvoiceStatus":"NOT_INVOICED","marketplaceCargoProviderName":"Carrier"}""",
            """{"city":"Istanbul"}""",
            """{"taxOffice":"Test office"}""",
            [new RemoteOrderLine("line-1", "SKU-1", "869000000001", "Readable product name", 99m, 1m, 0m, "Open", """{"imageUrl":"https://images.example.test/item.jpg","productCode":"MODEL-1"}""")],
            [],
            "{}",
            observedAt.AddDays(1));

        var updatedEntities = HepsiburadaStaleOrderEnrichmentPolicy.Apply(order, [line], remote, observedAt);

        Assert.Equal(2, updatedEntities);
        Assert.Equal(modifiedAt, order.LastRemoteModifiedAt);
        Assert.Equal("SHIPPED", order.DerivedStatus);
        Assert.Equal(752m, order.GrossAmount);
        Assert.Equal(751m, order.NetAmount);
        Assert.Equal(observedAt.AddDays(1), order.ShipmentDueAt);
        Assert.Equal(observedAt, order.UpdatedAt);
        Assert.Equal(5, order.Version);

        using var customerSnapshot = JsonDocument.Parse(order.CustomerSnapshotJson);
        Assert.Equal("Existing buyer", customerSnapshot.RootElement.GetProperty("name").GetString());
        Assert.Equal("NOT_INVOICED", customerSnapshot.RootElement.GetProperty("marketplaceInvoiceStatus").GetString());
        Assert.Equal("Carrier", customerSnapshot.RootElement.GetProperty("marketplaceCargoProviderName").GetString());
        using var shipmentAddress = JsonDocument.Parse(order.ShipmentAddressSnapshotJson);
        Assert.Equal("Istanbul", shipmentAddress.RootElement.GetProperty("city").GetString());

        Assert.Equal("Readable product name", line.TitleSnapshot);
        Assert.Equal("869000000001", line.Barcode);
        Assert.Equal(2m, line.OrderedQuantity);
        Assert.Equal(1m, line.ShippedQuantity);
        Assert.Equal(376m, line.UnitPrice);
        Assert.Equal("Shipped", line.RawStatus);
        Assert.Equal(4, line.Version);
        using var lineSnapshot = JsonDocument.Parse(line.SourceSnapshotJson!);
        Assert.Equal("https://images.example.test/item.jpg", lineSnapshot.RootElement.GetProperty("imageUrl").GetString());
        Assert.Equal("Blue", lineSnapshot.RootElement.GetProperty("color").GetString());
    }

    [Fact]
    public void StaleOrderReadDoesNotReplaceKnownImageWhenRemoteReadHasNoUsefulFields()
    {
        var modifiedAt = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
        var order = new Order
        {
            ExternalOrderId = "HB-1",
            OrderNumber = "HB-1",
            Currency = "TRY",
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "NEW",
            LastRemoteModifiedAt = modifiedAt,
            CreatedAt = modifiedAt,
            UpdatedAt = modifiedAt
        };
        var line = new OrderLine
        {
            ExternalLineId = "line-1",
            Sku = "SKU-1",
            TitleSnapshot = "Readable title",
            SourceSnapshotJson = """{"imageUrl":"https://images.example.test/current.jpg"}""",
            RawStatus = "Open"
        };
        var remote = new RemoteOrder(
            "HB-1", "HB-1", modifiedAt, modifiedAt.AddMinutes(-1), "TRY", 1m, 0m, 1m,
            "{}", "{}", "{}",
            [new RemoteOrderLine("line-1", "SKU-1", null, "SKU-1", 1m, 1m, 0m, "Open", """{"imageUrl":"https://images.example.test/older.jpg"}""")],
            [], "{}");

        var updatedEntities = HepsiburadaStaleOrderEnrichmentPolicy.Apply(order, [line], remote, modifiedAt.AddHours(1));

        Assert.Equal(0, updatedEntities);
        Assert.Equal(1, order.Version);
        using var lineSnapshot = JsonDocument.Parse(line.SourceSnapshotJson!);
        Assert.Equal("https://images.example.test/current.jpg", lineSnapshot.RootElement.GetProperty("imageUrl").GetString());
        Assert.Equal(1, line.Version);
    }

    [Fact]
    public void StaleOrderReadMatchesClaimReconstructedLineByItsOriginalLineItemId()
    {
        var modifiedAt = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
        var order = new Order
        {
            ExternalOrderId = "HB-CLAIM-1",
            OrderNumber = "HB-CLAIM-1",
            Currency = "TRY",
            DerivedStatus = "NEW",
            LastRemoteModifiedAt = modifiedAt,
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            CreatedAt = modifiedAt,
            UpdatedAt = modifiedAt
        };
        var line = new OrderLine
        {
            ExternalLineId = "claim-id",
            Sku = "HB-SKU-1",
            TitleSnapshot = "HB-SKU-1",
            SourceSnapshotJson = """{"id":"claim-id","lineItemId":"line-item-1","sku":"HB-SKU-1","MerchantSku":"SELLER-SKU-1","RequestedProduct":null}""",
            RawStatus = "ClaimCreated",
            Version = 1
        };
        var remoteLine = new RemoteOrderLine(
            "line-item-1", "SELLER-SKU-1", "869000000001", "Readable product", 1m, 751.75m, 0m,
            "Open", """{"id":"line-item-1","merchantSku":"SELLER-SKU-1","imageUrl":"https://images.example.test/hepsi.jpg"}""");
        var remote = new RemoteOrder(
            "HB-CLAIM-1", "HB-CLAIM-1", modifiedAt.AddDays(-2), modifiedAt.AddHours(-1), "TRY", 751.75m, 0m, 751.75m,
            "{}", "{}", "{}", [remoteLine], [], "{}");

        var updatedEntities = HepsiburadaStaleOrderEnrichmentPolicy.Apply(order, [line], remote, modifiedAt.AddHours(1));

        Assert.Equal(2, updatedEntities);
        Assert.Equal("line-item-1", line.ExternalLineId);
        Assert.Equal("Readable product", line.TitleSnapshot);
        Assert.Equal("869000000001", line.Barcode);
        using var snapshot = JsonDocument.Parse(line.SourceSnapshotJson!);
        Assert.Equal("https://images.example.test/hepsi.jpg", snapshot.RootElement.GetProperty("imageUrl").GetString());
    }

    [Fact]
    public void StaleOrderReadDoesNotGuessBetweenDuplicateProductLines()
    {
        var modifiedAt = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
        var order = new Order
        {
            ExternalOrderId = "HB-DUPLICATE-1",
            OrderNumber = "HB-DUPLICATE-1",
            Currency = "TRY",
            DerivedStatus = "NEW",
            LastRemoteModifiedAt = modifiedAt,
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            CreatedAt = modifiedAt,
            UpdatedAt = modifiedAt
        };
        var first = new OrderLine { Id = Guid.NewGuid(), ExternalLineId = "claim-a", Sku = "SAME-SKU", TitleSnapshot = "SAME-SKU", SourceSnapshotJson = "{}", RawStatus = "ClaimCreated", Version = 1 };
        var second = new OrderLine { Id = Guid.NewGuid(), ExternalLineId = "claim-b", Sku = "SAME-SKU", TitleSnapshot = "SAME-SKU", SourceSnapshotJson = "{}", RawStatus = "ClaimCreated", Version = 1 };
        var remoteLine = new RemoteOrderLine("line-a", "SAME-SKU", null, "Remote title", 1m, 1m, 0m, "Open", "{}");
        var remote = new RemoteOrder(
            "HB-DUPLICATE-1", "HB-DUPLICATE-1", modifiedAt.AddDays(-2), modifiedAt.AddHours(-1), "TRY", 2m, 0m, 2m,
            "{}", "{}", "{}", [remoteLine], [], "{}");

        var updatedEntities = HepsiburadaStaleOrderEnrichmentPolicy.Apply(order, [first, second], remote, modifiedAt.AddHours(1));

        Assert.Equal(0, updatedEntities);
        Assert.Equal("claim-a", first.ExternalLineId);
        Assert.Equal("claim-b", second.ExternalLineId);
        Assert.Equal("SAME-SKU", first.TitleSnapshot);
        Assert.Equal("SAME-SKU", second.TitleSnapshot);
    }

    [Fact]
    public void StaleOrderReadCanMatchAUniqueMerchantSkuWhenLineIdentityIsUnavailable()
    {
        var modifiedAt = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
        var order = new Order
        {
            ExternalOrderId = "HB-SKU-MATCH-1",
            OrderNumber = "HB-SKU-MATCH-1",
            Currency = "TRY",
            DerivedStatus = "NEW",
            LastRemoteModifiedAt = modifiedAt,
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            CreatedAt = modifiedAt,
            UpdatedAt = modifiedAt
        };
        var line = new OrderLine
        {
            ExternalLineId = "claim-id",
            Sku = "HB-SKU-1",
            TitleSnapshot = "HB-SKU-1",
            SourceSnapshotJson = """{"id":"claim-id","sku":"HB-SKU-1","MerchantSku":"SELLER-SKU-1"}""",
            RawStatus = "ClaimCreated",
            Version = 1
        };
        var remoteLine = new RemoteOrderLine(
            "order-line-id", "different-display-sku", null, "Matched by seller SKU", 1m, 5m, 0m,
            "Open", """{"sellerSku":"SELLER-SKU-1"}""");
        var remote = new RemoteOrder(
            "HB-SKU-MATCH-1", "HB-SKU-MATCH-1", modifiedAt.AddDays(-2), modifiedAt.AddHours(-1), "TRY", 5m, 0m, 5m,
            "{}", "{}", "{}", [remoteLine], [], "{}");

        var updatedEntities = HepsiburadaStaleOrderEnrichmentPolicy.Apply(order, [line], remote, modifiedAt.AddHours(1));

        Assert.Equal(2, updatedEntities);
        Assert.Equal("order-line-id", line.ExternalLineId);
        Assert.Equal("Matched by seller SKU", line.TitleSnapshot);
    }
}
