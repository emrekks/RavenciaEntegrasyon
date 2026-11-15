using MarketplaceHub.Infrastructure.Adapters.N11;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Sibling of ADAPTOR-TY-HB-5 for n11: an epoch beyond what DateTimeOffset can hold is an unknown date; it never throws
// past the package loop (which catches only JsonException) and drops the other packages of the page.
public sealed class N11EpochRangeTests
{
    private const long Absurd = 99_999_999_999_999_999;

    private static string Package(string id, string histories, string extra) => $$"""
        {"orderNumber":"{{id}}","id":"P{{id}}",{{extra}}
         "lines":[{"orderLineId":"L{{id}}","quantity":1,"productName":"Bluz","stockCode":"BLZ-1","price":100,"sellerInvoiceAmount":100,"totalSellerDiscountPrice":0}],
         "packageHistories":{{histories}},"shipmentPackageStatus":"Created"}
        """;

    private const string Created = """[{"createdDate":1724274054460,"status":"Created"}]""";

    [Fact]
    public void AnOutOfRangeChangeOrDeliveryDateIsUnknownAndThePageStillMaps()
    {
        var page = $$"""
            {"page":0,"totalPages":1,"content":[
              {{Package("1", Created, $"\"lastModifiedDate\":{Absurd},\"agreedDeliveryDate\":{Absurd},")}},
              {{Package("2", Created, "\"lastModifiedDate\":1724323386203,")}}
            ]}
            """;

        var result = N11OrderMapper.Packages(page);

        Assert.Equal(2, result.Items.Count);
        var broken = result.Items.Single(order => order.ExternalOrderId == "1");
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1724274054460), broken.LastModifiedAt);
        Assert.Null(broken.ShipmentDueAt);
    }

    [Fact]
    public void AnOutOfRangeCreationDateRefusesOnlyItsOwnPackage()
    {
        var page = $$"""
            {"page":0,"totalPages":1,"content":[
              {{Package("1", "[{\"createdDate\":" + Absurd + ",\"status\":\"Created\"}]", "")}},
              {{Package("2", Created, "")}}
            ]}
            """;

        var result = N11OrderMapper.Packages(page);

        Assert.Equal("2", Assert.Single(result.Items).ExternalOrderId);
        Assert.Contains(result.Issues!, issue => issue.Identity == "P1" && issue.RereadOrderId == "1");
    }

    [Fact]
    public void AnOutOfRangeInvoiceDateIsAMissingDateNotAnException()
    {
        var request = N11HttpClient.InvoiceLinkRequest($$"""{"invoiceLink":"https://panel.example.com/a.pdf","orderNumber":"1","invoiceNumber":"X","invoiceDateTime":{{Absurd}}}""");

        Assert.NotNull(request.Error);
        Assert.Null(request.InvoiceDateTime);
    }
}
