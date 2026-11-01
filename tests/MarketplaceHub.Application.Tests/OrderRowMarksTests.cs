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
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Created","lastModifiedDate":1760000000000,
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
        var marks = MarketplaceSalesService.RowMarks("TRENDYOL", "RO", order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson);
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
        var marks = MarketplaceSalesService.RowMarks("TRENDYOL", "TR", $$"""{"fastDelivery":true,"fastDeliveryType":"{{value}}"}""", "{}");
        Assert.Equal(expected, marks?.FastDeliveryType);
    }

    // A snapshot written before the field was read has no fastDeliveryType: unknown, so no mark.
    [Fact]
    public void Missing_fields_leave_every_mark_out()
    {
        Assert.Null(MarketplaceSalesService.RowMarks("TRENDYOL", "TR", """{"orderNumber":"1"}""", "{}"));
        Assert.Null(MarketplaceSalesService.RowMarks("TRENDYOL", null, "", ""));
        Assert.Null(MarketplaceSalesService.RowMarks("TRENDYOL", null, "not json", "{}"));
    }

    [Fact]
    public void Fast_delivery_type_with_fast_delivery_false_gets_no_mark()
    {
        Assert.Null(MarketplaceSalesService.RowMarks("TRENDYOL", "TR", """{"fastDelivery":false,"fastDeliveryType":"FastDelivery"}""", "{}"));
        Assert.Equal("FastDelivery", MarketplaceSalesService.RowMarks("TRENDYOL", "TR", """{"fastDeliveryType":"FastDelivery"}""", "{}")?.FastDeliveryType);
    }

    // micro and countryCode are independent: a foreign country without micro=true is no micro export.
    [Fact]
    public void Micro_export_mark_needs_micro_true()
    {
        Assert.Null(MarketplaceSalesService.RowMarks("TRENDYOL", "RO", """{"micro":false}""", """{"countryCode":"RO"}"""));
        Assert.Null(MarketplaceSalesService.RowMarks("TRENDYOL", "RO", """{"micro":"true"}""", """{"countryCode":"RO"}"""));
        Assert.Null(MarketplaceSalesService.RowMarks("TRENDYOL", "TR", """{"3pByTrendyol":true}""", "{}"));
    }

    // The shipment address of a micro order is the Istanbul depot; the invoice address names the country, the
    // storefront is the fallback, and an unknown country still shows the micro mark without a flag.
    [Theory]
    [InlineData("""{"countryCode":"AZ"}""", "TR", "AZ")]
    [InlineData("""{"countryCode":"bg"}""", "BG", "BG")]
    [InlineData("{}", "SA", "SA")]
    [InlineData("""{"countryCode":"TR"}""", "GR", "GR")]
    [InlineData("{}", "TR", null)]
    [InlineData("{}", null, null)]
    public void Micro_export_country_comes_from_the_invoice_address_then_the_storefront(string invoiceAddress, string? storeFront, string? expected)
    {
        var marks = MarketplaceSalesService.RowMarks("TRENDYOL", storeFront, """{"micro":true}""", invoiceAddress);
        Assert.NotNull(marks);
        Assert.True(marks!.MicroExport);
        Assert.Equal(expected, marks.MicroExportCountryCode);
    }

    [Fact]
    public void Delivery_point_mark_reads_trendyol_collection_point_and_n11_point_types()
    {
        Assert.True(MarketplaceSalesService.RowMarks("TRENDYOL", "TR", """{"deliveryAddressType":"CollectionPoint"}""", "{}")?.DeliveryPoint);
        Assert.Null(MarketplaceSalesService.RowMarks("TRENDYOL", "TR", """{"deliveryAddressType":"Shipment"}""", "{}"));
        Assert.Equal(new(false, null, true, "KTN", null), MarketplaceSalesService.RowMarks("N11", null, """{"deliveryAddressType":"ktn"}""", "{}"));
        Assert.Null(MarketplaceSalesService.RowMarks("N11", null, """{"deliveryAddressType":""}""", "{}"));
        Assert.Null(MarketplaceSalesService.RowMarks("N11", null, """{"deliveryAddressType":null}""", "{}"));
        // n11 sends no micro or fast delivery facts; other platforms get no marks at all.
        Assert.Null(MarketplaceSalesService.RowMarks("N11", null, """{"micro":true,"fastDeliveryType":"FastDelivery"}""", "{}"));
        Assert.Null(MarketplaceSalesService.RowMarks("HEPSIBURADA", null, """{"micro":true,"deliveryAddressType":"CollectionPoint"}""", "{}"));
    }

    // Only the package's own fields count, not a same-named field deeper in the snapshot.
    [Fact]
    public void Marks_read_top_level_fields_only()
    {
        Assert.Null(MarketplaceSalesService.RowMarks("TRENDYOL", "TR", """{"lines":[{"fastDeliveryType":"FastDelivery","micro":true}]}""", "{}"));
    }
}
