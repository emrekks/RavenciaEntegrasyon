using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaCatalogStructureTests
{
    [Fact]
    public void BuildsTheMerchantProductsEndpointWithPaging()
    {
        var connection = new PlatformConnection { PlatformCode = "HEPSIBURADA", Environment = "STAGE", DisplayName = "SIT", ExternalStoreId = "merchant/17", Status = "ACTIVE", ApiVersion = "v1" };
        var context = new HepsiburadaRequestContext(connection, new Uri("https://oms.example/"), new Uri("https://listing.example/"), "user", "secret");

        Assert.Equal("api/products/all-products-of-merchant/merchant%2F17?page=2&size=500", HepsiburadaHttpClient.MerchantProducts(context, 2, 500));
    }

    [Fact]
    public void ReadsCategoryAndAttributeListsFromAWrappedPage()
    {
        using var document = JsonDocument.Parse("""
        {
          "success": true,
          "totalElements": 3,
          "totalPages": 2,
          "data": [
            { "hbSku": "HBV1", "merchantSku": "SKU-1", "barcode": "869", "productName": "Bluz", "categoryId": 101, "categoryName": "Bluz",
              "baseAttributes": [ { "name": "Renk", "value": "Mavi" }, { "name": "Kumaş Tipi", "values": ["Pamuk", "Keten"] } ],
              "variantTypeAttributes": [ { "id": "beden_variant_property", "value": "M" } ] },
            { "hbSku": "HBV2", "merchantSku": "SKU-2", "category": { "id": "202", "name": "Elbise" }, "attributes": { "Renk": "Kırmızı" } }
          ]
        }
        """);

        var page = HepsiburadaJsonMapper.CatalogStructurePage(document.RootElement, 0, 2);

        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.Equal("1", page.NextCursor);
        Assert.Equal(3, page.TotalCount);
        var first = page.Items[0];
        Assert.Equal("101", first.CategoryExternalId);
        Assert.Equal("Bluz", first.CategoryName);
        var options = first.Variants[0].Options;
        Assert.Equal("Mavi", options["Renk"]);
        Assert.Equal("Pamuk, Keten", options["Kumaş Tipi"]);
        Assert.Equal("M", options["beden_variant_property"]);
        var second = page.Items[1];
        Assert.Equal("202", second.CategoryExternalId);
        Assert.Equal("Elbise", second.CategoryName);
        Assert.Equal("Kırmızı", second.Variants[0].Options["Renk"]);
    }

    [Fact]
    public void StopsPagingOnTheLastPage()
    {
        using var document = JsonDocument.Parse("""{ "totalPages": 1, "data": [ { "merchantSku": "A", "categoryId": 1 } ] }""");

        var page = HepsiburadaJsonMapper.CatalogStructurePage(document.RootElement, 0, 500);

        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void LeavesTheCategoryEmptyWhenTheResponseHasNone()
    {
        using var document = JsonDocument.Parse("""{ "data": [ { "merchantSku": "A", "productName": "Ürün" } ] }""");

        var page = HepsiburadaJsonMapper.CatalogStructurePage(document.RootElement, 0, 500);

        Assert.Null(page.Items[0].CategoryExternalId);
    }

    [Fact]
    public void RejectsAResponseWithoutAProductList()
    {
        using var document = JsonDocument.Parse("""{ "success": false, "message": "yetki yok" }""");

        Assert.Throws<JsonException>(() => HepsiburadaJsonMapper.CatalogStructurePage(document.RootElement, 0, 500));
    }
}
