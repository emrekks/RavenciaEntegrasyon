using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSalesImageUrlTests
{
    [Theory]
    [InlineData("https://productimages.hepsiburada.net/s/777/{size}/110001632331020.jpg", "https://productimages.hepsiburada.net/s/777/500/110001632331020.jpg")]
    [InlineData("https://productimages.hepsiburada.net/s/777/{SIZE}/110001632331020.jpg", "https://productimages.hepsiburada.net/s/777/500/110001632331020.jpg")]
    [InlineData("https://cdn.example.test/image.jpg", "https://cdn.example.test/image.jpg")]
    public void NormalizeImageUrlResolvesHepsiburadaSizeToken(string source, string expected)
    {
        Assert.Equal(expected, MarketplaceSalesService.NormalizeImageUrl(source));
    }

    [Fact]
    public void NormalizeImageUrlRejectsNonHttpsImages()
    {
        Assert.Null(MarketplaceSalesService.NormalizeImageUrl("http://productimages.hepsiburada.net/s/777/{size}/image.jpg"));
    }

    [Theory]
    [InlineData("productImageUrlFormat")]
    [InlineData("emaproductImageUrlFormat")]
    public void SourceImageUrlReadsHepsiburadaFormattedImageFields(string fieldName)
    {
        var snapshot = $$"""{"{{fieldName}}":"https://productimages.hepsiburada.net/s/777/{size}/image.jpg"}""";

        Assert.Equal("https://productimages.hepsiburada.net/s/777/{size}/image.jpg", MarketplaceSalesService.SourceImageUrl(snapshot));
        Assert.Equal("https://productimages.hepsiburada.net/s/777/500/image.jpg", MarketplaceSalesService.NormalizeImageUrl(MarketplaceSalesService.SourceImageUrl(snapshot)));
    }

    [Fact]
    public void SourceImageUrlReadsImagesNestedInAnOrderSnapshot()
    {
        const string snapshot = """{"product":{"images":[{"url":"https://productimages.hepsiburada.net/s/777/{size}/image.jpg"}]}}""";

        Assert.Equal("https://productimages.hepsiburada.net/s/777/500/image.jpg", MarketplaceSalesService.NormalizeImageUrl(MarketplaceSalesService.SourceImageUrl(snapshot)));
    }

    // Live 2026-10-29: a Hepsiburada line built from a claim lists the customer's photo (Reports[].ImageUrl) before the
    // product's image; the line showed that unloadable claim photo and its frame stayed empty.
    [Fact]
    public void SourceImageUrlSkipsClaimEvidencePhotosListedBeforeTheProductImage()
    {
        const string snapshot = """
            {"status":"Refunded","MerchantSku":"KRLHN517805",
             "Reports":[{"ImageUrl":"https://images.hepsiburada.net/oms/claim/2026/10/11/photo.jpeg","ReportedBy":"Customer"}],
             "productBarcode":"00KRLHN517805",
             "productImageUrlFormat":"https://productimages.hepsiburada.net/s/777/{size}/110000846862267.jpg",
             "imageUrl":"https://productimages.hepsiburada.net/s/777/{size}/110000846862267.jpg"}
            """;

        Assert.Equal("https://productimages.hepsiburada.net/s/777/500/110000846862267.jpg", MarketplaceSalesService.NormalizeImageUrl(MarketplaceSalesService.SourceImageUrl(snapshot)));
    }

    [Fact]
    public void SourceImageUrlIgnoresASnapshotWhoseOnlyImageIsAClaimPhoto()
    {
        const string snapshot = """{"Reports":[{"ImageUrl":"https://images.hepsiburada.net/oms/claim/photo.jpeg"}],"images":[]}""";

        Assert.Null(MarketplaceSalesService.SourceImageUrl(snapshot));
    }

    [Fact]
    public void SourceImageUrlPrefersTheProductImageFieldOverALaterGenericOne()
    {
        const string snapshot = """{"imageUrl":"https://cdn.example.test/generic.jpg","productImageUrl":"https://cdn.example.test/product.jpg"}""";

        Assert.Equal("https://cdn.example.test/product.jpg", MarketplaceSalesService.SourceImageUrl(snapshot));
    }

    [Fact]
    public void SourceImageUrlSkipsAnUnusableAddressForTheNextField()
    {
        const string snapshot = """{"productImageUrl":"http://insecure.example.test/a.jpg","imageUrl":"https://cdn.example.test/b.jpg"}""";

        Assert.Equal("https://cdn.example.test/b.jpg", MarketplaceSalesService.SourceImageUrl(snapshot));
    }

    [Fact]
    public void LineImageUrlsUseTheCatalogPhotoWhenTheMarketplaceGaveNone()
    {
        Assert.Equal<(string?, string?)>(("/api/v1/files/product-media/1/content", null), MarketplaceSalesService.LineImageUrls(null, "/api/v1/files/product-media/1/content"));
    }

    [Fact]
    public void LineImageUrlsKeepTheCatalogPhotoAsFallbackBehindTheMarketplaceImage()
    {
        Assert.Equal<(string?, string?)>(("https://cdn.example.test/a.jpg", "https://cdn.example.test/catalog.jpg"), MarketplaceSalesService.LineImageUrls("https://cdn.example.test/a.jpg", "https://cdn.example.test/catalog.jpg"));
        Assert.Equal<(string?, string?)>(("https://cdn.example.test/a.jpg", null), MarketplaceSalesService.LineImageUrls("https://cdn.example.test/a.jpg", "https://cdn.example.test/a.jpg"));
    }

    [Fact]
    public void LineImageUrlsStayEmptyWhenNothingIsKnown()
    {
        Assert.Equal<(string?, string?)>((null, null), MarketplaceSalesService.LineImageUrls(null, null));
    }

    [Fact]
    public void CatalogLookupKeysRemoveHepsiburadaZeroPaddingForAlphaNumericBarcodes()
    {
        Assert.Equal(["0000MZ029YD22", "MZ029YD22"], MarketplaceSalesService.CatalogLookupKeys("0000MZ029YD22"));
    }

    [Theory]
    [InlineData("00001234567890")]
    [InlineData("000000")]
    public void CatalogLookupKeysKeepNumericBarcodesUnchanged(string barcode)
    {
        Assert.Equal([barcode], MarketplaceSalesService.CatalogLookupKeys(barcode));
    }
}
