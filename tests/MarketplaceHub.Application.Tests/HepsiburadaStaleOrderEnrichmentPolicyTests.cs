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
}
