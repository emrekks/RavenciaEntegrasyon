using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A brand name typed for a Hepsiburada, n11 or Shopify connection replaces the panel brand name; a missing or blank
// one leaves the panel name (the publication then behaves as before).
public sealed class BrandNameMappingTests
{
    [Theory]
    [InlineData("  Ravencia Shop ", "Panel Marka", "Ravencia Shop")]
    [InlineData(null, " Panel Marka ", "Panel Marka")]
    [InlineData("   ", "Panel Marka", "Panel Marka")]
    public void TypedNameWinsOverThePanelName(string? typed, string panel, string expected) =>
        Assert.Equal(expected, BrandNameMappings.Pick(typed, panel));

    [Fact]
    public void OnlyTheFreeTextBrandMarketplacesTakeATypedName() =>
        Assert.Equal(["HEPSIBURADA", "N11", "SHOPIFY"], BrandNameMappings.Platforms);

    // Hepsiburada: the typed name is the title prefix, and the adapter's check (title starts with Marka) holds for it.
    [Fact]
    public void HepsiburadaTitleOpensWithTheTypedName()
    {
        var brand = BrandNameMappings.Pick("Ravencia Shop", "Panel Marka");
        var title = HepsiburadaProductPublicationComposer.TitleWithBrand("Yonca Bluz", brand);
        Assert.Equal("Ravencia Shop Yonca Bluz", title);
        Assert.True(HepsiburadaProductPublicationComposer.StartsWithBrand(title, brand));
        Assert.False(HepsiburadaProductPublicationComposer.StartsWithBrand(title, "Panel Marka"));
        Assert.Equal("Panel Marka Yonca Bluz", HepsiburadaProductPublicationComposer.TitleWithBrand("Yonca Bluz", BrandNameMappings.Pick(null, "Panel Marka")));
    }

    // n11: attribute 1 carries the brand as customValue.
    [Fact]
    public void N11BrandAttributeCarriesTheTypedName()
    {
        var brand = BrandNameMappings.Pick("Ravencia Shop", "Panel Marka");
        var sku = new N11SkuDraft("Yonca Bluz", "<p>Açıklama</p>", 1002123, "BS7429", 3, "Kargo Şablonu 1", "BS7429-40", "8690000000001", 12,
            ["https://cdn.example.com/a.jpg"], [new(1, null, brand)], 499m, 599m, 20);
        using var json = JsonDocument.Parse(N11ProductPublicationComposer.Payload("Ravencia", [sku]));
        var attribute = json.RootElement.GetProperty("payload").GetProperty("skus")[0].GetProperty("attributes")[0];
        Assert.Equal(1, attribute.GetProperty("id").GetInt64());
        Assert.Equal("Ravencia Shop", attribute.GetProperty("customValue").GetString());
    }
}
