using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShipmentPackageClassificationTests
{
    [Theory]
    [InlineData("order-creation")]
    [InlineData("split")]
    [InlineData("cancel")]
    [InlineData(null)]
    public void OriginPackageAlone_DoesNotMakePackageResend(string? createdBy)
    {
        Assert.False(ShipmentPackageClassification.IsResend(createdBy, "4144008059"));
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("resend")]
    [InlineData("replacement")]
    [InlineData("TRANSFER")]
    public void ExplicitCreatorMarker_MakesPackageResend(string createdBy)
    {
        Assert.True(ShipmentPackageClassification.IsResend(createdBy, "4144008059"));
    }

    [Fact]
    public void ExplicitCreatorMarkerWithoutOriginPackage_IsNotResend()
    {
        Assert.False(ShipmentPackageClassification.IsResend("transfer", null));
    }

    [Fact]
    public void TrendyolMapper_PreservesCreatedByForPackageClassification()
    {
        const string json = """
            {"content":[{"id":"pkg-1","orderNumber":"11587375142","status":"Delivered","lastModifiedDate":1760000000000,"originPackageIds":["parent-1"],"createdBy":"split","lines":[{"lineId":"line-1","stockCode":"SKU-1","productName":"Test","quantity":1,"lineUnitPrice":10,"vatRate":20}]}]}
            """;

        var package = Assert.Single(Assert.Single(TrendyolJsonMapper.Orders(json).Items).Packages);

        Assert.Equal("split", package.CreatedBy);
        Assert.False(ShipmentPackageClassification.IsResend(package.CreatedBy, package.OriginExternalPackageId));
    }
}
