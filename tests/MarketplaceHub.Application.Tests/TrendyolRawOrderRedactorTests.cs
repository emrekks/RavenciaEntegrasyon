using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The resend diagnostic (11 Nov 2026) keeps only the order's packages of a getShipmentPackages answer and never the
// buyer's personal fields; the shape follows the documented response (trendyol-api-son.md, getShipmentPackages).
public sealed class TrendyolRawOrderRedactorTests
{
    private const string Page = """
        {"totalElements":2,"totalPages":1,"page":0,"size":200,"nextCursor":"abc","hasMore":true,
         "content":[
          {"shipmentAddress":{"firstName":"A","lastName":"B","fullAddress":"X"},"invoiceAddress":{"fullName":"A B"},
           "orderNumber":"11805932340","id":4337242198,"shipmentPackageId":4337242198,"customerFirstName":"A","customerEmail":"a@b.c","customerId":1,
           "identityNumber":"1","createdBy":"order-creation","originPackageIds":null,"status":"Created","cargoTrackingNumber":7330038976334420,
           "lines":[{"lineId":6191132914,"stockCode":"RYP004-11","productName":"Bluz","quantity":1,"lineItemPrice":371.9}]},
          {"orderNumber":"11800000000","id":1,"customerFirstName":"C","lines":[]}
         ]}
        """;

    [Fact]
    public void KeepsOnlyTheOrdersPackagesWithoutPersonalFields()
    {
        using var kept = JsonDocument.Parse(TrendyolRawOrderRedactor.Keep(Page, "11805932340", []));
        var root = kept.RootElement;
        Assert.Equal(2, root.GetProperty("packagesOnPage").GetInt32());
        Assert.Equal("abc", root.GetProperty("nextCursor").GetString());
        var content = root.GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        var package = content[0];
        Assert.Equal("order-creation", package.GetProperty("createdBy").GetString());
        Assert.Equal(7330038976334420, package.GetProperty("cargoTrackingNumber").GetInt64());
        Assert.Equal(6191132914, package.GetProperty("lines")[0].GetProperty("lineId").GetInt64());
        Assert.Equal("Bluz", package.GetProperty("lines")[0].GetProperty("productName").GetString());
        foreach (var personal in new[] { "shipmentAddress", "invoiceAddress", "customerFirstName", "customerEmail", "customerId", "identityNumber" })
            Assert.False(package.TryGetProperty(personal, out _), personal);
    }

    [Fact]
    public void KeepsAPackageNamedByIdUnderAnotherOrderNumber()
    {
        using var kept = JsonDocument.Parse(TrendyolRawOrderRedactor.Keep(Page, "99999999999", ["1"]));
        Assert.Equal(1, kept.RootElement.GetProperty("content").GetArrayLength());
        Assert.False(kept.RootElement.GetProperty("content")[0].TryGetProperty("customerFirstName", out _));
    }

    [Fact]
    public void ReadsTheStreamCursorOnlyWhileMoreFollows()
    {
        Assert.Equal("abc", TrendyolRawOrderRedactor.NextCursor(Page));
        Assert.Null(TrendyolRawOrderRedactor.NextCursor("""{"nextCursor":"abc","hasMore":false,"content":[]}"""));
    }
}
