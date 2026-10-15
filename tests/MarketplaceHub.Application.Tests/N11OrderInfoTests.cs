using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11OrderInfoTests
{
    private const string Customer = """{"cargoSenderNumber":"6508248937060","cargoTrackingLink":"https://kargotakip.araskargo.com.tr/mainpage.aspx?code=6508248937060","deliveryAddressType":"Adres"}""";

    private static string Line(decimal mall, decimal seller, decimal commission, decimal campaign) =>
        $$"""{"price":625,"totalMallDiscountPrice":{{mall.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"totalSellerDiscountPrice":{{seller.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"commissionRate":{{commission.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"sellerCampaignCommissionRate":{{campaign.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""";

    [Fact]
    public void TrackingCommissionAndDiscountsAreListed()
    {
        var info = MarketplaceSalesService.N11OrderInfo([Line(60, 0, 20.34m, 0)], Customer);

        Assert.Equal(["Kargo takip no", "Teslimat tipi", "Komisyon oranı", "N11 indirimi"], info.Select(x => x.Label));
        Assert.Equal("6508248937060", info[0].Value);
        Assert.Equal("https://kargotakip.araskargo.com.tr/mainpage.aspx?code=6508248937060", info[0].Url);
        Assert.Equal("%20,34", info[2].Value);
        Assert.Equal("60,00 ₺", info[3].Value);
    }

    [Fact]
    public void CampaignCommissionIsSubtractedAndDiscountsAreSummedAcrossLines()
    {
        var info = MarketplaceSalesService.N11OrderInfo([Line(10, 5, 20, 2.5m), Line(20, 7.5m, 20, 2.5m)], "{}");

        Assert.Equal("%17,5", info.Single(x => x.Label == "Komisyon oranı").Value);
        Assert.Equal("30,00 ₺", info.Single(x => x.Label == "N11 indirimi").Value);
        Assert.Equal("12,50 ₺", info.Single(x => x.Label == "Satıcı indirimi").Value);
    }

    [Fact]
    public void DifferentLineRatesAreShownTogether()
    {
        var info = MarketplaceSalesService.N11OrderInfo([Line(0, 0, 12, 0), Line(0, 0, 20, 0)], "{}");

        Assert.Equal("%12 / %20", info.Single().Value);
    }

    [Theory]
    [InlineData("""{"cargoSenderNumber":"123","cargoTrackingLink":"http://insecure.example.com/x"}""")]
    [InlineData("""{"cargoSenderNumber":"123","cargoTrackingLink":"javascript:alert(1)"}""")]
    public void OnlyHttpsTrackingLinksAreOffered(string customer)
    {
        var info = MarketplaceSalesService.N11OrderInfo([], customer);

        Assert.Equal("123", info.Single().Value);
        Assert.Null(info.Single().Url);
    }

    [Fact]
    public void BrokenOrMissingSnapshotsAreIgnored()
    {
        Assert.Empty(MarketplaceSalesService.N11OrderInfo([null, "", "not json"], "{}"));
    }
}
