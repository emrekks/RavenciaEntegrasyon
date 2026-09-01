using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolCatalogMapperTests
{
    [Fact]
    public void ApprovedProductResponse_PreservesParentVariantsPricesStockAndOptions()
    {
        const string json = """
        {
          "totalPages": 1,
          "page": 0,
          "size": 100,
          "nextPageToken": null,
          "content": [
            {
              "contentId": 800001,
              "productMainId": "PRODUCT-001",
              "title": "Test tişört",
              "description": "Açıklama",
              "brand": { "id": 42, "name": "Ravencia" },
              "category": { "id": 99, "name": "Giyim" },
              "images": [
                { "url": "https://cdn.example.test/products/test-front.jpg" },
                { "url": "https://cdn.example.test/products/test-back.jpg" }
              ],
              "attributes": [
                { "attributeName": "Cinsiyet", "attributeValue": "Unisex" }
              ],
              "variants": [
                {
                  "variantId": 810001,
                  "barcode": "869000000001",
                  "stockCode": "SKU-001-S",
                  "attributes": [
                    { "attributeName": "Beden", "attributeValue": "S" },
                    { "attributeName": "Renk", "attributeValue": "Siyah" }
                  ],
                  "stock": { "quantity": 7 },
                  "price": { "currency": "TRY", "salePrice": 149.90, "listPrice": 199.90 },
                  "vatRate": 20,
                  "archived": false
                },
                {
                  "variantId": 810002,
                  "barcode": "869000000002",
                  "stockCode": "SKU-001-M",
                  "attributes": [
                    { "attributeName": "Beden", "attributeValue": "M" },
                    { "attributeName": "Renk", "attributeValue": "Siyah" }
                  ],
                  "stock": { "quantity": 3 },
                  "price": { "currency": "TRY", "salePrice": 159.90, "listPrice": 209.90 },
                  "vatRate": 20,
                  "archived": false
                }
              ]
            }
          ]
        }
        """;

        var result = TrendyolJsonMapper.CatalogProducts(json);

        var product = Assert.Single(result.Items);
        Assert.Equal("800001", product.ExternalProductId);
        Assert.Equal("PRODUCT-001", product.ProductMainId);
        Assert.Equal(2, product.Variants.Count);
        Assert.Equal(2, product.ImageUrls.Count);
        Assert.Equal("Ravencia", product.BrandName);
        Assert.Equal("Giyim", product.CategoryName);

        var small = Assert.Single(product.Variants, variant => variant.Sku == "SKU-001-S");
        Assert.Equal("869000000001", small.Barcode);
        Assert.Equal("S", small.Options["Beden"]);
        Assert.Equal("Siyah", small.Options["Renk"]);
        Assert.Equal(7m, small.StockQuantity);
        Assert.Equal(149.90m, small.SalePrice);
        Assert.Equal(199.90m, small.ListPrice);
        Assert.Equal("TRY", small.Currency);
    }
}
