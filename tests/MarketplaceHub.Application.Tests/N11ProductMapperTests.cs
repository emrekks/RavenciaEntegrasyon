using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11ProductMapperTests
{
    // Shape and values follow the GetProductQuery example in the n11 documentation.
    private const string DocumentedPage = """
        {"content":[{"n11ProductId":123456789,"sellerId":9876543,"sellerNickname":"testMagaza","stockCode":"TestSKU123","title":"Test Ürünü Kontrol Amaçlı Gri",
          "description":"Açıklama bilgisi uzun açıklama","categoryId":1231231,"productMainId":"GrupKoduModellemeİçin","status":"Active","saleStatus":"On_Sale",
          "preparingDay":3,"shipmentTemplate":"1","catalogId":987654321,"barcode":null,"groupId":12345678,"currencyType":"TL","salePrice":10000.0,"listPrice":10000,"quantity":2,
          "attributes":[{"attributeId":1,"attributeName":"Marka","attributeValue":"Diğer"},{"attributeId":429,"attributeName":"Renk","attributeValue":"Gri"},{"attributeId":845,"attributeName":"Ölçüler","attributeValue":"0.5 CMx 1 M"}],
          "imageUrls":["https://n11scdn3.akamaized.net/a1/org/07/76/32/73/IMG-4395477787604309691.jpg"],"vatRate":10,"commissionRate":8}],
         "pageable":{"pageNumber":0,"pageSize":20},"last":true,"totalElements":1,"totalPages":1,"first":true,"number":0,"numberOfElements":1,"size":20,"empty":false}
        """;

    [Fact]
    public void DocumentedItemMapsToOneProductWithItsStockCodeVariant()
    {
        using var json = JsonDocument.Parse(DocumentedPage);

        var page = N11ProductMapper.Page(json.RootElement);

        var product = Assert.Single(page.Items);
        Assert.False(page.HasMore);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("main:GrupKoduModellemeİçin", product.ExternalProductId);
        Assert.Equal("GrupKoduModellemeİçin", product.ProductMainId);
        Assert.Equal("Test Ürünü Kontrol Amaçlı Gri", product.Title);
        Assert.Equal("Diğer", product.BrandName);
        Assert.Equal("1231231", product.CategoryExternalId);
        Assert.False(product.IsPendingApproval);
        var variant = Assert.Single(product.Variants);
        Assert.Equal("TestSKU123", variant.ExternalVariantId);
        Assert.Equal("TestSKU123", variant.Sku);
        Assert.Null(variant.Barcode);
        Assert.Equal("GrupKoduModellemeİçin", variant.ModelCode);
        Assert.Equal(10000m, variant.SalePrice);
        Assert.Equal(2m, variant.StockQuantity);
        Assert.Equal(10m, variant.VatRate);
        Assert.Equal("TRY", variant.Currency);
        Assert.False(variant.Archived);
        Assert.Equal("Gri", variant.Options["Renk"]);
        Assert.False(variant.Options.ContainsKey("Marka"));
        Assert.Single(product.ImageUrls);
    }

    [Theory]
    [InlineData("Suspended", true, false)]
    [InlineData("InCatalogApproval", false, true)]
    [InlineData("Active", false, false)]
    public void StatusDecidesArchivedAndPendingApproval(string status, bool archived, bool pending)
    {
        using var json = JsonDocument.Parse($$"""{"n11ProductId":5,"stockCode":"S-5","status":"{{status}}"}""");

        var product = N11ProductMapper.Product(json.RootElement)!;

        Assert.Equal(archived, product.Variants[0].Archived);
        Assert.Equal(pending, product.IsPendingApproval);
        Assert.Equal("n11:5", product.ExternalProductId);
    }

    [Fact]
    public void PagesContinueByPageNumberAndItemsWithoutIdentityBecomeIssues()
    {
        using var json = JsonDocument.Parse("""{"content":[{"title":"Kimliksiz"}],"number":0,"totalPages":4,"totalElements":800}""");

        var page = N11ProductMapper.Page(json.RootElement);

        Assert.Empty(page.Items);
        Assert.True(page.HasMore);
        Assert.Equal("1", page.NextCursor);
        Assert.Single(page.Issues!);
    }

    [Fact]
    public void ProductQueryCarriesPageSizeAndLookupFilters()
    {
        Assert.Equal("ms/product-query?page=2&size=250", N11HttpClient.ProductPage(2, 250, new ProductReadFilter(null)));
        Assert.Equal("ms/product-query?page=0&size=250&productMainId=MODEL%201", N11HttpClient.ProductPage(0, 250, new ProductReadFilter(null, ProductMainId: "MODEL 1")));
    }
}
