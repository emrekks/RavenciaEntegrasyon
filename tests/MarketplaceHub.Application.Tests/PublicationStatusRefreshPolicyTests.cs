using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PublicationStatusRefreshPolicyTests
{
    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    [InlineData("SHOPIFY")]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    [InlineData("PTTAVM")]
    public void AnActiveProductionConnectionOfEveryPublishingMarketplaceMayRefresh(string platformCode)
    {
        Assert.Null(PublicationStatusRefreshPolicy.Refusal(Connection(platformCode, "ACTIVE")));
    }

    [Fact]
    public void AnInactivePazaramaConnectionIsNamedWithItsStatus()
    {
        var refusal = PublicationStatusRefreshPolicy.Refusal(Connection("PAZARAMA", "DISABLED", displayName: "Ravencia"));

        Assert.Equal("Pazarama · Ravencia bağlantısı etkin değil (durum: pasif); yayın durumu yalnız etkin veya doğrulanmış bağlantıda sorgulanabilir.", refusal);
        Assert.DoesNotContain("Trendyol, Hepsiburada", refusal);
    }

    [Fact]
    public void AStoreNamedAfterItsMarketplaceIsNamedOnce()
    {
        var refusal = PublicationStatusRefreshPolicy.Refusal(Connection("PTTAVM", "DRAFT", displayName: "PTT AVM"));

        Assert.StartsWith("PTT AVM bağlantısı etkin değil (durum: taslak)", refusal);
    }

    [Fact]
    public void AMarketplaceWithoutPublicationReadBackSaysSo()
    {
        var refusal = PublicationStatusRefreshPolicy.Refusal(Connection("TRENDYOL_EFATURAM", "ACTIVE", displayName: "E-Faturam"));

        Assert.Equal("TRENDYOL_EFATURAM · E-Faturam için yayın durumu sorgulanamaz; bu pazaryerinde yayın durumu okunmuyor.", refusal);
    }

    [Fact]
    public void AStageConnectionWhoseLastTestFailedAsksForANewTest()
    {
        var connection = Connection("PAZARAMA", "VERIFIED", environment: "STAGE");
        connection.LastErrorCode = "AUTH_FAILED";

        Assert.Equal("Pazarama bağlantısının son testi başarısız (AUTH_FAILED); bağlantıyı yeniden test edip tekrar deneyin.", PublicationStatusRefreshPolicy.Refusal(connection));
    }

    [Fact]
    public void AMissingConnectionIsRefused()
    {
        Assert.Equal("Bağlantı bulunamadı; yayın durumu sorgulanamadı.", PublicationStatusRefreshPolicy.Refusal(null));
    }

    private static PlatformConnection Connection(string platformCode, string status, string environment = "PRODUCTION", string? displayName = null) => new()
    {
        PlatformCode = platformCode,
        Environment = environment,
        DisplayName = displayName ?? PublicationStatusRefreshPolicy.PlatformName(platformCode),
        ExternalStoreId = "1",
        Status = status,
        ApiVersion = "v1"
    };
}
