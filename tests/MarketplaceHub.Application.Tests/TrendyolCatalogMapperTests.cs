using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolCatalogMapperTests
{
    [Fact]
    public void CatalogImportOrdering_CompletesEachModelBeforeMovingToTheNextOne()
    {
        static RemoteCatalogProduct Product(string externalId, string modelId, string sku) => new(
            externalId,
            modelId,
            modelId,
            "",
            null,
            null,
            null,
            null,
            [],
            [new RemoteCatalogVariant(sku, sku, null, modelId, new Dictionary<string, string>(), false, null, null, null, null, null, "{}")],
            "{}");

        var groups = CatalogImportOrdering.GroupByModel(new[]
        {
            Product("content-2", "model-a", "model-a-red"),
            Product("content-9", "model-b", "model-b-small"),
            Product("content-3", "model-a", "model-a-blue")
        }).ToList();

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "content-2", "content-3" }, groups[0].Select(x => x.ExternalProductId));
        Assert.Equal(new[] { "content-9" }, groups[1].Select(x => x.ExternalProductId));
    }

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
        Assert.Equal(new[] { "https://cdn.example.test/products/test-front.jpg", "https://cdn.example.test/products/test-back.jpg" }, product.ImageUrls);
        Assert.Equal("Ravencia", product.BrandName);
        Assert.Equal("Giyim", product.CategoryName);

        var small = Assert.Single(product.Variants, variant => variant.Sku == "SKU-001-S");
        Assert.Equal("869000000001", small.Barcode);
        Assert.Equal("S", small.Options["Beden"]);
        Assert.Equal("Siyah", small.Options["Renk"]);
        Assert.Equal("Unisex", small.Options["Cinsiyet"]);
        Assert.Equal(7m, small.StockQuantity);
        Assert.Equal(149.90m, small.SalePrice);
        Assert.Equal(199.90m, small.ListPrice);
        Assert.Equal("TRY", small.Currency);
    }

    [Fact]
    public void CatalogProductResponse_MapsVariantImagesAndPreservesOrderedUniqueUrls()
    {
        const string json = """
        {
          "content": [
            {
              "contentId": 800002,
              "productMainId": "PRODUCT-002",
              "title": "Görselli ürün",
              "images": [
                { "url": "https://cdn.example.test/shared.jpg" },
                { "url": "https://cdn.example.test/product.jpg" },
                { "url": "https://cdn.example.test/shared.jpg" }
              ],
              "variants": [
                {
                  "variantId": 820001,
                  "stockCode": "SKU-002-S",
                  "images": [
                    "https://cdn.example.test/shared.jpg",
                    { "url": "https://cdn.example.test/variant-s-front.jpg" },
                    { "imageUrl": "https://cdn.example.test/variant-s-back.jpg" }
                  ]
                },
                {
                  "variantId": 820002,
                  "stockCode": "SKU-002-M",
                  "imageUrl": "https://cdn.example.test/variant-m-front.jpg"
                }
              ]
            }
          ]
        }
        """;

        var result = TrendyolJsonMapper.CatalogProducts(json);
        var product = Assert.Single(result.Items);
        var small = Assert.Single(product.Variants, variant => variant.Sku == "SKU-002-S");
        var medium = Assert.Single(product.Variants, variant => variant.Sku == "SKU-002-M");

        Assert.Equal(
            new[]
            {
                "https://cdn.example.test/shared.jpg",
                "https://cdn.example.test/product.jpg",
                "https://cdn.example.test/variant-s-front.jpg",
                "https://cdn.example.test/variant-s-back.jpg",
                "https://cdn.example.test/variant-m-front.jpg"
            },
            product.ImageUrls);
        Assert.Equal(
            new[]
            {
                "https://cdn.example.test/shared.jpg",
                "https://cdn.example.test/variant-s-front.jpg",
                "https://cdn.example.test/variant-s-back.jpg"
            },
            small.ImageUrls);
        Assert.Equal(new[] { "https://cdn.example.test/variant-m-front.jpg" }, medium.ImageUrls);
    }

    [Fact]
    public void ApprovedProductResponse_UsesOneRepresentativeImagePerColorAcrossSizeVariants()
    {
        const string json = """
        {
          "content": [{
            "contentId": 800004,
            "productMainId": "PRODUCT-004",
            "title": "Haki kazak",
            "images": [
              { "url": "https://cdn.example.test/haki-front.jpg" },
              { "url": "https://cdn.example.test/haki-back.jpg" }
            ],
            "attributes": [
              { "attributeName": "Renk", "attributeValue": "Haki" }
            ],
            "variants": [
              {
                "variantId": 840001,
                "barcode": "869000000041",
                "stockCode": "SKU-004-S",
                "attributes": [{ "attributeName": "Beden", "attributeValue": "S" }]
              },
              {
                "variantId": 840002,
                "barcode": "869000000042",
                "stockCode": "SKU-004-M",
                "attributes": [{ "attributeName": "Beden", "attributeValue": "M" }]
              }
            ]
          }]
        }
        """;

        var product = Assert.Single(TrendyolJsonMapper.CatalogProducts(json).Items);
        var small = Assert.Single(product.Variants, variant => variant.Sku == "SKU-004-S");
        var medium = Assert.Single(product.Variants, variant => variant.Sku == "SKU-004-M");
        Assert.Equal("Haki", small.Options["Renk"]);
        Assert.Equal(new[] { "https://cdn.example.test/haki-front.jpg", "https://cdn.example.test/haki-back.jpg" }, small.ImageUrls);
        Assert.Empty(medium.ImageUrls!);
        Assert.Equal(new[] { "https://cdn.example.test/haki-front.jpg", "https://cdn.example.test/haki-back.jpg" }, product.ImageUrls);
    }

    [Fact]
    public void CatalogProductResponse_AssociatesTopLevelColorImagesWithMatchingVariants()
    {
        const string json = """
        {
          "content": [{
            "contentId": 800003,
            "title": "Renkli ürün",
            "images": [
              { "variantId": 830001, "link": "https://cdn.example.test/red.jpg" },
              { "variantId": 830002, "url": "https://cdn.example.test/blue.jpg" }
            ],
            "variants": [
              { "variantId": 830001, "stockCode": "SKU-003-RED" },
              { "variantId": 830002, "stockCode": "SKU-003-BLUE" }
            ]
          }]
        }
        """;

        var product = Assert.Single(TrendyolJsonMapper.CatalogProducts(json).Items);

        Assert.Equal(new[] { "https://cdn.example.test/red.jpg" }, Assert.Single(product.Variants, x => x.Sku == "SKU-003-RED").ImageUrls);
        Assert.Equal(new[] { "https://cdn.example.test/blue.jpg" }, Assert.Single(product.Variants, x => x.Sku == "SKU-003-BLUE").ImageUrls);
    }

    [Fact]
    public void CatalogProductResponse_SkipsAdditionalSizesForTheSameColorAndKeepsNextColor()
    {
        const string json = """
        {
          "content": [{
            "contentId": 800005,
            "productMainId": "PRODUCT-005",
            "title": "Renk ve bedenli ürün",
            "variants": [
              {
                "variantId": 850001,
                "stockCode": "SKU-005-BLACK-S",
                "attributes": [{ "attributeName": "Renk", "attributeValue": "Siyah" }, { "attributeName": "Beden", "attributeValue": "S" }],
                "images": ["https://cdn.example.test/black-front.jpg", "https://cdn.example.test/black-back.jpg"]
              },
              {
                "variantId": 850002,
                "stockCode": "SKU-005-BLACK-M",
                "attributes": [{ "attributeName": "Renk", "attributeValue": "Siyah" }, { "attributeName": "Beden", "attributeValue": "M" }],
                "images": ["https://cdn.example.test/black-front-m.jpg"]
              },
              {
                "variantId": 850003,
                "stockCode": "SKU-005-BLUE-S",
                "attributes": [{ "attributeName": "Renk", "attributeValue": "Mavi" }, { "attributeName": "Beden", "attributeValue": "S" }],
                "images": ["https://cdn.example.test/blue-front.jpg"]
              }
            ]
          }]
        }
        """;

        var product = Assert.Single(TrendyolJsonMapper.CatalogProducts(json).Items);

        Assert.Equal(new[] { "https://cdn.example.test/black-front.jpg", "https://cdn.example.test/black-back.jpg", "https://cdn.example.test/blue-front.jpg" }, product.ImageUrls);
        Assert.Equal(new[] { "https://cdn.example.test/black-front.jpg", "https://cdn.example.test/black-back.jpg" }, Assert.Single(product.Variants, x => x.Sku == "SKU-005-BLACK-S").ImageUrls);
        Assert.Empty(Assert.Single(product.Variants, x => x.Sku == "SKU-005-BLACK-M").ImageUrls!);
        Assert.Equal(new[] { "https://cdn.example.test/blue-front.jpg" }, Assert.Single(product.Variants, x => x.Sku == "SKU-005-BLUE-S").ImageUrls);
    }

    [Fact]
    public void CatalogProductResponse_ReadsCatalogOptionAliasesAndKeepsTheirOrder()
    {
        const string json = """
        {
          "content": [{
            "contentId": 800006,
            "productMainId": "PRODUCT-006",
            "title": "Takım",
            "variants": [{
              "variantId": 860001,
              "stockCode": "SKU-006",
              "variantAttributes": [
                { "name": "Renk", "value": "Siyah" },
                { "optionName": "Beden", "optionValue": "M" }
              ]
            }]
          }]
        }
        """;

        var variant = Assert.Single(Assert.Single(TrendyolJsonMapper.CatalogProducts(json).Items).Variants);

        Assert.Equal("Siyah", variant.Options["Renk"]);
        Assert.Equal("M", variant.Options["Beden"]);
        Assert.Equal("Renk: Siyah | Beden: M", string.Join(" | ", variant.Options.Select(x => $"{x.Key}: {x.Value}")));
    }

    [Fact]
    public void CatalogProductResponse_PrefersLocalColorOverWebColorWhenGroupingVariantImages()
    {
        const string json = """
        {
          "content": [{
            "contentId": 800007,
            "productMainId": "PRODUCT-007",
            "title": "Çok renkli takım",
            "variants": [
              {
                "variantId": 870001,
                "stockCode": "SKU-007-RABBIT",
                "attributes": [
                  { "attributeName": "Web Color", "attributeValue": "Çok Renkli" },
                  { "attributeName": "Renk", "attributeValue": "Tavşanlı Çok Renkli" },
                  { "attributeName": "Beden", "attributeValue": "S" }
                ],
                "images": ["https://cdn.example.test/rabbit.jpg"]
              },
              {
                "variantId": 870002,
                "stockCode": "SKU-007-FLOWER",
                "attributes": [
                  { "attributeName": "Web Color", "attributeValue": "Çok Renkli" },
                  { "attributeName": "Renk", "attributeValue": "Çiçekli Çok Renkli" },
                  { "attributeName": "Beden", "attributeValue": "S" }
                ],
                "images": ["https://cdn.example.test/flower.jpg"]
              }
            ]
          }]
        }
        """;

        var product = Assert.Single(TrendyolJsonMapper.CatalogProducts(json).Items);
        var rabbit = Assert.Single(product.Variants, variant => variant.Sku == "SKU-007-RABBIT");
        var flower = Assert.Single(product.Variants, variant => variant.Sku == "SKU-007-FLOWER");

        Assert.Equal("Tavşanlı Çok Renkli", rabbit.Options["Renk"]);
        Assert.Equal("Çok Renkli", rabbit.Options["Web Color"]);
        Assert.Equal(new[] { "https://cdn.example.test/rabbit.jpg" }, rabbit.ImageUrls);
        Assert.Equal(new[] { "https://cdn.example.test/flower.jpg" }, flower.ImageUrls);
        Assert.Equal(new[] { "https://cdn.example.test/rabbit.jpg", "https://cdn.example.test/flower.jpg" }, product.ImageUrls);
    }
}
