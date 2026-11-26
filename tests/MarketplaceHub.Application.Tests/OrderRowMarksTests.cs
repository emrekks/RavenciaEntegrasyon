using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OrderRowMarksTests
{
    // The Trendyol order read keeps the package's micro, deliveryAddressType and fastDeliveryType in the customer
    // snapshot, where the order row's marks read them.
    [Fact]
    public void Trendyol_order_snapshot_keeps_the_fields_the_row_marks_read()
    {
        const string json = """
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,
          "micro":true,"deliveryAddressType":"CollectionPoint","fastDelivery":true,"fastDeliveryType":"SameDayShipping",
          "invoiceAddress":{"countryCode":"RO"},"shipmentAddress":{"countryCode":"TR"},"lines":[
          {"lineId":"1","stockCode":"SKU-1","productName":"Bluz","quantity":1,"amount":100.00,"discount":0,"price":100.00,"vatRate":10}
        ]}]}
        """;

        var order = Assert.Single(TrendyolJsonMapper.Orders(json).Items);

        using var snapshot = JsonDocument.Parse(order.CustomerSnapshotJson);
        Assert.Equal("SameDayShipping", snapshot.RootElement.GetProperty("fastDeliveryType").GetString());
        Assert.Equal("CollectionPoint", snapshot.RootElement.GetProperty("deliveryAddressType").GetString());
        Assert.True(snapshot.RootElement.GetProperty("micro").GetBoolean());
        var marks = OrderSnapshotReader.RowMarks("TRENDYOL", "RO", order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson);
        Assert.Equal(new(true, "RO", true, null, "SameDayShipping"), marks);
    }

    [Theory]
    [InlineData("TodayDelivery", "TodayDelivery")]
    [InlineData("SameDayShipping", "SameDayShipping")]
    [InlineData("FastDelivery", "FastDelivery")]
    [InlineData("", null)]
    [InlineData("SomethingNew", null)]
    public void Fast_delivery_mark_takes_only_the_documented_values(string value, string? expected)
    {
        var marks = OrderSnapshotReader.RowMarks("TRENDYOL", "TR", $$"""{"fastDelivery":true,"fastDeliveryType":"{{value}}"}""", "{}");
        Assert.Equal(expected, marks?.FastDeliveryType);
    }

    // A snapshot written before the field was read has no fastDeliveryType: unknown, so no mark.
    [Fact]
    public void Missing_fields_leave_every_mark_out()
    {
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", "TR", """{"orderNumber":"1"}""", "{}"));
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", null, "", ""));
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", null, "not json", "{}"));
    }

    [Fact]
    public void Fast_delivery_type_with_fast_delivery_false_gets_no_mark()
    {
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", "TR", """{"fastDelivery":false,"fastDeliveryType":"FastDelivery"}""", "{}"));
        Assert.Equal("FastDelivery", OrderSnapshotReader.RowMarks("TRENDYOL", "TR", """{"fastDeliveryType":"FastDelivery"}""", "{}")?.FastDeliveryType);
    }

    // micro and countryCode are independent: a foreign country without a micro-export signal is no micro export. The mark
    // follows the one micro-export rule (MicroExportRule, owner 27 Nov 2026): only an order it calls micro export is
    // marked; "true" as text, or a domestic flag under a micro-export storefront, is unknown and gets no mark.
    [Fact]
    public void Micro_export_mark_follows_the_one_rule()
    {
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", "DE", """{"micro":false}""", """{"countryCode":"RO"}"""));
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", "RO", """{"micro":false}""", """{"countryCode":"RO"}"""));
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", "RO", """{"micro":"true"}""", """{"countryCode":"RO"}"""));
        Assert.Equal(new(true, "RO", false, null, null), OrderSnapshotReader.RowMarks("TRENDYOL", "RO", """{"micro":false,"3pByTrendyol":true}""", "{}"));
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", "TR", """{"micro":false,"3pByTrendyol":true}""", "{}"));
    }

    // The shipment address of a micro order is the Istanbul depot; the invoice address names the country, the
    // storefront is the fallback, and an unknown country still shows the micro mark without a flag.
    [Theory]
    [InlineData("""{"countryCode":"AZ"}""", null, "AZ")]
    [InlineData("""{"countryCode":"AZ"}""", "DE", "AZ")]
    [InlineData("""{"countryCode":"bg"}""", "BG", "BG")]
    [InlineData("{}", "SA", "SA")]
    [InlineData("""{"countryCode":"TR"}""", "GR", "GR")]
    [InlineData("""{"countryCode":"TR"}""", null, null)]
    [InlineData("{}", null, null)]
    public void Micro_export_country_comes_from_the_invoice_address_then_the_storefront(string invoiceAddress, string? storeFront, string? expected)
    {
        var marks = OrderSnapshotReader.RowMarks("TRENDYOL", storeFront, """{"micro":true}""", invoiceAddress);
        Assert.NotNull(marks);
        Assert.True(marks!.MicroExport);
        Assert.Equal(expected, marks.MicroExportCountryCode);
    }

    [Fact]
    public void Delivery_point_mark_reads_trendyol_collection_point_and_n11_point_types()
    {
        Assert.True(OrderSnapshotReader.RowMarks("TRENDYOL", "TR", """{"deliveryAddressType":"CollectionPoint"}""", "{}")?.DeliveryPoint);
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", "TR", """{"deliveryAddressType":"Shipment"}""", "{}"));
        Assert.Equal(new(false, null, true, "KTN", null), OrderSnapshotReader.RowMarks("N11", null, """{"deliveryAddressType":"ktn"}""", "{}"));
        Assert.Null(OrderSnapshotReader.RowMarks("N11", null, """{"deliveryAddressType":""}""", "{}"));
        Assert.Null(OrderSnapshotReader.RowMarks("N11", null, """{"deliveryAddressType":null}""", "{}"));
        // n11 sends no fast delivery facts and other platforms no delivery point; only Trendyol has micro export
        // (MicroExportRule): a micro field on another marketplace's package marks nothing.
        Assert.Null(OrderSnapshotReader.RowMarks("N11", null, """{"fastDeliveryType":"FastDelivery"}""", "{}"));
        Assert.Null(OrderSnapshotReader.RowMarks("N11", null, """{"micro":true,"fastDeliveryType":"FastDelivery"}""", "{}"));
        Assert.Null(OrderSnapshotReader.RowMarks("HEPSIBURADA", null, """{"deliveryAddressType":"CollectionPoint"}""", "{}"));
        Assert.Null(OrderSnapshotReader.RowMarks("HEPSIBURADA", null, """{"micro":true,"deliveryAddressType":"CollectionPoint"}""", "{}"));
    }

    // Delivery point, fast delivery and micro export read the package's own fields only: a same-named field deeper in the
    // snapshot gives no mark (for micro export it makes the order unknown, MicroExportRule).
    [Fact]
    public void Delivery_marks_read_top_level_fields_only()
    {
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", "TR", """{"lines":[{"fastDeliveryType":"FastDelivery"}]}""", "{}"));
        Assert.Null(OrderSnapshotReader.RowMarks("TRENDYOL", "TR", """{"lines":[{"micro":true}]}""", "{}"));
        Assert.Equal("FastDelivery", OrderSnapshotReader.RowMarks("TRENDYOL", "TR", """{"micro":false,"fastDeliveryType":"FastDelivery","lines":[{"micro":true}]}""", "{}")?.FastDeliveryType);
        Assert.False(OrderSnapshotReader.RowMarks("TRENDYOL", "TR", """{"micro":false,"fastDeliveryType":"FastDelivery","lines":[{"micro":true}]}""", "{}")!.MicroExport);
    }
}
