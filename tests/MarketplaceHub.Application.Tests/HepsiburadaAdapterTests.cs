using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaAdapterTests
{
    [Fact]
    public void PackageAndShipmentEndpointsUseOfficialMerchantAndPackageScopedRoutes()
    {
        Assert.Equal("packages/merchantid/merchant%2F17/packagenumber/PKG%2F1/changablecargocompanies", HepsiburadaHttpClient.ChangeableCargoCompanies("merchant/17", "PKG/1"));
        Assert.Equal("packages/merchantid/merchant%2F17/packagenumber/PKG%2F1/changecargocompany", HepsiburadaHttpClient.ChangeCargoCompany("merchant/17", "PKG/1"));
        Assert.Equal("packages/merchantid/merchant%2F17/packagenumber/PKG%2F1/unpack", HepsiburadaHttpClient.UnpackPackage("merchant/17", "PKG/1"));
        Assert.Equal("packages/merchantid/merchant%2F17/packagenumber/PKG%2F1", HepsiburadaHttpClient.PackageTrackingInfo("merchant/17", "PKG/1"));
        Assert.Equal("lineitems/merchantid/merchant%2F17/packageablewith/lineitemid/line%2F1", HepsiburadaHttpClient.PackageableLineItems("merchant/17", "line/1"));
        Assert.Equal("packages/merchantid/merchant%2F17", HepsiburadaHttpClient.CreatePackage("merchant/17"));
        Assert.Equal("packages/merchantid/merchant%2F17/packagenumber/PKG%2F1/labels?format=zpl", HepsiburadaHttpClient.PackageLabel("merchant/17", "PKG/1"));
        Assert.Equal("packages/merchantid/merchant%2F17/packagenumber/PKG%2F1/labels?format=base64zpl", HepsiburadaHttpClient.PackageLabel("merchant/17", "PKG/1", "BASE64ZPL"));
        Assert.Equal("packages/merchantid/merchant%2F17/packagenumber/PKG%2F1/labels?format=pdf", HepsiburadaHttpClient.PackageLabel("merchant/17", "PKG/1", "PDF"));
        Assert.Equal("packages/merchantid/merchant%2F17/packagenumber/PKG%2F1/labels?format=png", HepsiburadaHttpClient.PackageLabel("merchant/17", "PKG/1", "PNG"));
        Assert.Equal("packages/merchantid/merchant%2F17/packagenumber/PKG%2F1/labels?format=jpg", HepsiburadaHttpClient.PackageLabel("merchant/17", "PKG/1", "JPG"));
    }

    [Fact]
    public void PackageMappersAndCreatePayloadKeepEligibleLinesAndOfficialFields()
    {
        using var carriersJson = JsonDocument.Parse("""{"data":[{"shortName":"HEPSIJET","name":"HepsiJet"},{"shortName":"ARAS","name":"Aras Kargo"}]}""");
        using var linesJson = JsonDocument.Parse("""{"items":[{"lineItemId":"line-2","quantity":3}]}""");
        using var packageJson = JsonDocument.Parse("""{"data":{"packageNumber":"PKG-91"}}""");
        var carriers = HepsiburadaJsonMapper.ChangeableCargoCompanies(carriersJson.RootElement);
        var lines = HepsiburadaJsonMapper.PackageableLineItems(linesJson.RootElement);
        var result = HepsiburadaJsonMapper.PackageNumber(packageJson.RootElement);
        var command = new CreateOrderPackageCommand("BARCODE-9", "ARAS", "carrier-1", "SELLER", 2, 1, "warehouse-address", "standard", [new("line-1", 1), new("line-2", 3)]);
        using var payload = JsonDocument.Parse(HepsiburadaHttpClient.CreatePackagePayload(command));

        Assert.Equal(new[] { "HEPSIJET", "ARAS" }, carriers.Select(carrier => carrier.ShortName).ToArray());
        Assert.Equal("line-2", Assert.Single(lines).LineItemId);
        Assert.Equal(3, lines[0].Quantity);
        Assert.Equal("PKG-91", result);
        Assert.True(payload.RootElement.TryGetProperty("lineItemRequests", out var requests));
        Assert.Equal("line-1", requests[0].GetProperty("id").GetString());
        Assert.Equal(3, requests[1].GetProperty("quantity").GetInt32());
        Assert.Equal("warehouse-address", payload.RootElement.GetProperty("warehouse").GetProperty("shippingAddressLabel").GetString());
        Assert.True(HepsiburadaHttpClient.ValidOrderPackageCommand(command));
        Assert.False(HepsiburadaHttpClient.ValidOrderPackageCommand(command with { LineItems = [new("line-1", 1), new("line-1", 2)] }));
    }

    [Fact]
    public void ProductMatchMapper_ComparesPreMatchedCatalogFieldsAndPagesByPageNumber()
    {
        using var json = JsonDocument.Parse("""
        {
          "data": {
            "items": [
              { "merchantSku": "SKU-ONE", "productStatus": "PRE_MATCHED", "matchedProduct": { "hbSku": "HB-1", "productName": "Katalog ürünü", "brandName": "Örnek", "image1": "https://cdn.example.com/item.jpg", "barcode": "4006381333931" } }
            ],
            "totalCount": 2
          }
        }
        """);

        var page = HepsiburadaJsonMapper.PendingProductMatches(json.RootElement, 0, 1);

        Assert.Equal(2, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.Equal("1", page.NextCursor);
        var match = Assert.Single(page.Items);
        Assert.Equal("SKU-ONE", match.MerchantSku);
        Assert.Equal("PRE_MATCHED", match.Status);
        Assert.Equal("HB-1", match.HepsiburadaSku);
        Assert.Equal("Katalog ürünü", match.ProductName);
        Assert.Equal("Örnek", match.BrandName);
        Assert.Equal("https://cdn.example.com/item.jpg", Assert.Single(match.ImageUrls));
        Assert.Equal("4006381333931", match.Barcode);
    }

    [Fact]
    public void ProductMatchMapper_RejectsRowsWithoutMerchantSkuAndRecognizesFailedDecisions()
    {
        using var missingSku = JsonDocument.Parse("""{"items":[{"productStatus":"PRE_MATCHED"}]}""");
        using var rejected = JsonDocument.Parse("""{"success":false,"message":"invalid sku"}""");
        using var accepted = JsonDocument.Parse("""{"success":true,"code":0}""");

        Assert.Throws<JsonException>(() => HepsiburadaJsonMapper.PendingProductMatches(missingSku.RootElement, 0, 50));
        Assert.False(HepsiburadaJsonMapper.ProductMatchDecisionAccepted(rejected.RootElement));
        Assert.True(HepsiburadaJsonMapper.ProductMatchDecisionAccepted(accepted.RootElement));
    }

    [Fact]
    public void ProductMatchEndpointsAndDecisionPayloadUseMerchantScopedContract()
    {
        var connection = new MarketplaceHub.Domain.PlatformConnection
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            PublicId = Guid.NewGuid(),
            PlatformCode = "HEPSIBURADA",
            Environment = "STAGE",
            DisplayName = "SIT",
            ExternalStoreId = "merchant/17",
            Status = "ACTIVE",
            ApiVersion = "v1"
        };
        var context = new HepsiburadaRequestContext(connection, new Uri("https://oms.example/"), new Uri("https://listing.example/"), "user", "secret");
        var payload = HepsiburadaHttpClient.ProductMatchDecisionPayload("merchant/17", ["SKU-ONE", "SKU-TWO"]);
        using var json = JsonDocument.Parse(payload);

        Assert.Equal("api/products/products-by-merchant-and-status?merchantId=merchant%2F17&productStatus=PRE_MATCHED&taskStatus=false&version=1&page=2&size=50", HepsiburadaHttpClient.PendingProductMatches(context, 2, 50));
        Assert.Equal("api/products/approve-prematch", HepsiburadaHttpClient.ProductMatchDecision(true));
        Assert.Equal("api/products/reject-prematch", HepsiburadaHttpClient.ProductMatchDecision(false));
        Assert.Equal("merchant/17", json.RootElement[0].GetProperty("merchant").GetString());
        Assert.Equal(new[] { "SKU-ONE", "SKU-TWO" }, json.RootElement[0].GetProperty("merchantSkuList").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.True(HepsiburadaHttpClient.ValidMatchDecisionSkus(["SKU-ONE", "SKU-TWO"]));
        Assert.False(HepsiburadaHttpClient.ValidMatchDecisionSkus(["SKU-ONE", "sku-one"]));
        Assert.False(HepsiburadaHttpClient.ValidMatchDecisionSkus(["SKU ONE"]));
    }

    [Fact]
    public void ProductUpdateEndpointAndPayloadRequireUniqueMerchantAndHepsiburadaSkus()
    {
        const string valid = """{"merchantId":"merchant-17","items":[{"hbSku":"HB-1","merchantSku":"SKU-ONE","productName":"Ürün","productDescription":"Açıklama","barcode":"4006381333931","attributes":{"attribute-11":"Mavi"},"image1":"https://cdn.example.com/new.jpg"}]}""";

        Assert.Equal("/ticket-api/api/integrator/import?version=1", HepsiburadaHttpClient.ProductUpdateUpload());
        Assert.Equal("/ticket-api/api/integrator/status/trace%2F1?version=1&page=2&size=100", HepsiburadaHttpClient.ProductUpdateStatus("trace/1", 2, 100));
        Assert.True(HepsiburadaHttpClient.ValidProductUpdatePayload(valid, "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductUpdatePayload(valid, "other-merchant"));
        Assert.False(HepsiburadaHttpClient.ValidProductUpdatePayload(valid.Replace("\"hbSku\":\"HB-1\"", "\"hbSku\":\" \"", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductUpdatePayload(valid.Replace("https://cdn.example.com/new.jpg", "http://cdn.example.com/new.jpg", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductUpdatePayload("not-json", "merchant-17"));
    }

    [Fact]
    public void InventoryUploadUsesMerchantScopedEndpointAndXmlWithBothRemoteIdentifiers()
    {
        var context = new HepsiburadaRequestContext(new MarketplaceHub.Domain.PlatformConnection
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), PublicId = Guid.NewGuid(), PlatformCode = "HEPSIBURADA",
            Environment = "STAGE", DisplayName = "SIT", ExternalStoreId = "merchant/17", Status = "ACTIVE", ApiVersion = "v1"
        }, new Uri("https://oms.example/"), new Uri("https://listing.example/"), "user", "secret");
        const string payload = """{"merchantId":"merchant/17","items":[{"hepsiburadaSku":"HB-1","merchantSku":"SKU-ONE","price":118.97,"availableStock":9}]}""";

        Assert.Equal("listings/merchantid/merchant%2F17/inventory-uploads", HepsiburadaHttpClient.InventoryUpload(context));
        Assert.Equal("listings/merchantid/merchant%2F17/inventory-uploads/id/upload%2F71", HepsiburadaHttpClient.InventoryUploadStatus(context, "upload/71"));
        Assert.True(HepsiburadaHttpClient.ValidPriceInventoryPayload(payload, "merchant/17"));
        var xml = HepsiburadaHttpClient.BuildInventoryUploadXml(payload);
        Assert.Contains("<HepsiburadaSku>HB-1</HepsiburadaSku>", xml, StringComparison.Ordinal);
        Assert.Contains("<MerchantSku>SKU-ONE</MerchantSku>", xml, StringComparison.Ordinal);
        Assert.Contains("<Price>118,97</Price>", xml, StringComparison.Ordinal);
        Assert.Contains("<AvailableStock>9</AvailableStock>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryUploadMapperTracksAsyncStatusAndMapsRejectedLinesByMerchantSku()
    {
        using var accepted = JsonDocument.Parse("""{"data":{"inventoryUploadId":71}}""");
        Assert.Equal("71", HepsiburadaJsonMapper.InventoryUploadId(accepted.RootElement));

        using var processing = JsonDocument.Parse("""{"status":"PROCESSING","errors":[]}""");
        Assert.Equal("IN_PROGRESS", HepsiburadaJsonMapper.InventoryUploadStatus(processing.RootElement, "71").Status);

        using var completed = JsonDocument.Parse("""{"status":"COMPLETED","errors":[{"merchantSku":"SKU-TWO","hepsiburadaSku":"HB-2","errors":[{"string":"OutOfPriceRange"}]}]}""");
        var mapped = HepsiburadaJsonMapper.InventoryUploadStatus(completed.RootElement, "71");

        Assert.Equal("COMPLETED", mapped.Status);
        var line = Assert.Single(mapped.Lines);
        Assert.Equal("SKU-TWO", line.ExternalKey);
        Assert.False(line.Succeeded);
        Assert.Equal("OutOfPriceRange", line.ErrorCode);
        Assert.Equal("LISTING_INVENTORY:71", mapped.ExternalOperationId);
    }

    [Fact]
    public void StageTestOrderUsesTheOfficialStubHostRouteAndMerchantBoundHBSku()
    {
        var context = new HepsiburadaRequestContext(new MarketplaceHub.Domain.PlatformConnection
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), PublicId = Guid.NewGuid(), PlatformCode = "HEPSIBURADA",
            Environment = "STAGE", DisplayName = "SIT", ExternalStoreId = "merchant/17", Status = "ACTIVE", ApiVersion = "v1"
        }, new Uri("https://oms.example/"), new Uri("https://listing.example/"), "user", "secret");
        using var payload = JsonDocument.Parse(HepsiburadaHttpClient.TestOrderPayload("merchant/17", "1234567890", "HB-1", DateTimeOffset.Parse("2026-09-30T12:00:00Z")));

        Assert.Equal("orders/merchantId/merchant%2F17", HepsiburadaHttpClient.StageTestOrder(context, "merchant/17"));
        Assert.True(HepsiburadaHttpClient.ValidTestOrderSku("HB-1"));
        Assert.False(HepsiburadaHttpClient.ValidTestOrderSku("HB SKU"));
        Assert.Equal("1234567890", payload.RootElement.GetProperty("OrderNumber").GetString());
        Assert.Equal("HB-1", payload.RootElement.GetProperty("LineItems")[0].GetProperty("Sku").GetString());
        Assert.Equal("merchant/17", payload.RootElement.GetProperty("LineItems")[0].GetProperty("MerchantId").GetString());
    }

    [Fact]
    public void PriceInventoryPayload_RequiresMerchantScopeUniqueSkusAndCentsPrecision()
    {
        const string valid = """{"merchantId":"merchant-17","items":[{"hepsiburadaSku":"HB-1","merchantSku":"SKU-ONE","price":14.5,"availableStock":0}]}""";
        const string duplicate = """{"merchantId":"merchant-17","items":[{"hepsiburadaSku":"HB-1","merchantSku":"SKU-ONE","price":14.5,"availableStock":0},{"hepsiburadaSku":"HB-2","merchantSku":"sku-one","price":15,"availableStock":2}]}""";
        Assert.True(HepsiburadaHttpClient.ValidPriceInventoryPayload(valid, "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidPriceInventoryPayload(valid, "other-merchant"));
        Assert.False(HepsiburadaHttpClient.ValidPriceInventoryPayload(valid.Replace("\"availableStock\":0", "\"availableStock\":-1", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidPriceInventoryPayload(valid.Replace("\"price\":14.5", "\"price\":14.555", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidPriceInventoryPayload(duplicate, "merchant-17"));
    }

    [Fact]
    public void ListingMapper_UsesMerchantIdentifiersAndKeepsCatalogObservations()
    {
        using var json = JsonDocument.Parse("""
        {
          "items": [{
            "productId": "product-17",
            "hbSku": "hb-17",
            "merchantSku": "merchant-17",
            "barcode": "8690000000170",
            "productName": "Test product",
            "status": "ACTIVE",
            "price": 199.5,
            "availableStock": 4
          }],
          "totalCount": 1
        }
        """);

        var page = HepsiburadaJsonMapper.ListingPage(json.RootElement);
        var product = HepsiburadaJsonMapper.CatalogProduct(page.Items.Single());

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("product-17", product.ExternalProductId);
        Assert.Equal("Test product", product.Title);
        Assert.Equal("hb-17", product.Variants.Single().ExternalVariantId);
        Assert.Equal("merchant-17", product.Variants.Single().Sku);
        Assert.Equal("8690000000170", product.Variants.Single().Barcode);
        Assert.Equal(199.5m, product.Variants.Single().SalePrice);
        Assert.Equal(4m, product.Variants.Single().StockQuantity);
    }

    [Fact]
    public void ProductImportMapper_TracksIdAndMapsPartialResultsByMerchantSku()
    {
        using var tracking = JsonDocument.Parse("""{"success":true,"data":{"trackingId":"trace-71"}}""");
        Assert.Equal("trace-71", HepsiburadaJsonMapper.ProductImportTrackingId(tracking.RootElement));

        using var result = JsonDocument.Parse("""
        {
          "importStatus": "SUCCESS",
          "totalElements": 2,
          "data": [
            { "merchantSku": "SKU-ONE", "productStatus": "İncelenecek", "hbSku": "HB-1" },
            { "merchantSku": "SKU-TWO", "productStatus": "MISSING_INFO", "validationResults": [{ "attributeName": "Renk", "message": "Renk zorunlu" }] }
          ]
        }
        """);

        var mapped = HepsiburadaJsonMapper.ProductImportStatus(result.RootElement, "trace-71");

        Assert.Equal("COMPLETED", mapped.Status);
        Assert.Equal(2, HepsiburadaJsonMapper.ProductImportTotalCount(result.RootElement));
        Assert.Equal("SKU-ONE", mapped.Lines[0].ExternalKey);
        Assert.True(mapped.Lines[0].Succeeded);
        Assert.Equal("HB-1", mapped.Lines[0].ExternalId);
        Assert.Equal("SKU-TWO", mapped.Lines[1].ExternalKey);
        Assert.False(mapped.Lines[1].Succeeded);
        Assert.Equal("Renk zorunlu", mapped.Lines[1].ErrorCode);
    }

    [Fact]
    public void ProductImportMapper_KeepsProcessingStateAndRejectsDuplicateMerchantSkus()
    {
        using var processing = JsonDocument.Parse("""{"importStatus":"PROCESSING","data":[]}""");
        Assert.Equal("IN_PROGRESS", HepsiburadaJsonMapper.ProductImportStatus(processing.RootElement, "trace-72").Status);

        using var duplicate = JsonDocument.Parse("""
        { "importStatus": "SUCCESS", "data": [
          { "merchantSku": "DUPLICATE", "productStatus": "WAITING" },
          { "merchantSku": "duplicate", "productStatus": "WAITING" }
        ] }
        """);
        Assert.Throws<JsonException>(() => HepsiburadaJsonMapper.ProductImportStatus(duplicate.RootElement, "trace-73"));
    }

    [Theory]
    [InlineData("İncelenecek", true)]
    [InlineData("Görev açılmış", true)]
    [InlineData("Ürün bilgileri eksik", false)]
    [InlineData("Satışa Hazır", true)]
    public void ProductImportMapper_RecognizesDocumentedLocalizedProductStatuses(string status, bool succeeded)
    {
        var jsonText = JsonSerializer.Serialize(new { importStatus = "SUCCESS", data = new[] { new { merchantSku = "SKU-ONE", productStatus = status } } });
        using var json = JsonDocument.Parse(jsonText);

        var line = HepsiburadaJsonMapper.ProductImportStatus(json.RootElement, "trace-status").Lines.Single();

        Assert.Equal(succeeded, line.Succeeded);
    }

    [Fact]
    public void ProductImportMapper_RejectsUnknownProductStatusForManualReview()
    {
        using var json = JsonDocument.Parse("""{"importStatus":"SUCCESS","data":[{"merchantSku":"SKU-ONE","productStatus":"unseen status"}]}""");

        Assert.Throws<JsonException>(() => HepsiburadaJsonMapper.ProductImportStatus(json.RootElement, "trace-unknown"));
    }

    [Fact]
    public void ProductImportPayload_RequiresExactMerchantAndSafePriceStockAndImageValues()
    {
        const string valid = """[{"categoryId":11,"merchant":"merchant-17","attributes":{"merchantSku":"SKU-ONE","VaryantGroupID":"MODEL-1","Barcode":"4006381333931","UrunAdi":"Nike Shoe","UrunAciklamasi":"Shoe description","Marka":"Nike","tax_vat_rate":"20","price":"14,50","stock":"2","Image1":"https://cdn.example.com/item.jpg"}}]""";
        Assert.True(HepsiburadaHttpClient.ValidProductImportPayload(valid, "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductImportPayload(valid.Replace("merchant-17", "other-merchant", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductImportPayload(valid.Replace("14,50", "0", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductImportPayload(valid.Replace("14,50", "14.50", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductImportPayload(valid.Replace("\"categoryId\":11", "\"categoryId\":\"11\"", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductImportPayload(valid.Replace("\"Image1\":\"https://cdn.example.com/item.jpg\"", "\"Image1\":[\"https://cdn.example.com/item.jpg\"]", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductImportPayload(valid.Replace("\"Nike Shoe\"", "\"Shoe by Nike\"", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductImportPayload(valid.Replace("4006381333931", "4006381333932", StringComparison.Ordinal), "merchant-17"));
        Assert.False(HepsiburadaHttpClient.ValidProductImportPayload(valid.Replace("https://cdn.example.com/item.jpg", "http://cdn.example.com/item.jpg", StringComparison.Ordinal), "merchant-17"));
    }

    [Theory]
    [InlineData("plain sku", "PLAIN-SKU")]
    [InlineData("  abc  123 ", "ABC-123")]
    [InlineData("", null)]
    public void MerchantSku_IsUppercaseAndWhitespaceFree(string input, string? expected) =>
        Assert.Equal(expected, HepsiburadaProductPublicationComposer.NormalizeMerchantSku(input));

    [Theory]
    [InlineData("4006381333931", true)]
    [InlineData("4006381333932", false)]
    [InlineData("8690000000170", false)]
    [InlineData("40063813339A1", false)]
    public void Ean13_ValidatesLengthDigitsAndCheckDigit(string value, bool expected) =>
        Assert.Equal(expected, HepsiburadaProductPublicationComposer.IsValidEan13(value));

    [Theory]
    [InlineData("12345", "attribute-12345")]
    [InlineData("color-size", "attribute-color-size")]
    [InlineData("", null)]
    [InlineData("bad/id", null)]
    public void AttributeImportKey_UsesTheRemoteAttributeIdentifier(string attributeId, string? expected) =>
        Assert.Equal(expected, HepsiburadaProductPublicationComposer.AttributeImportKey(attributeId));

    [Fact]
    public void ReferenceMapper_MapsActiveCategoryTreeAndCategoryAttributes()
    {
        using var categories = JsonDocument.Parse("""
        {
          "totalElements": 2,
          "totalPages": 1,
          "number": 0,
          "data": [
            { "categoryId": 10, "name": "Elektronik", "parentCategoryId": null, "paths": "Elektronik", "leaf": false, "status": "ACTIVE", "available": true },
            { "categoryId": 11, "name": "Telefonlar", "parentCategoryId": 10, "paths": "Elektronik > Telefonlar", "leaf": true, "status": "ACTIVE", "available": true }
          ]
        }
        """);
        using var attributes = JsonDocument.Parse("""
        {
          "data": [
            { "id": "color", "name": "Renk", "mandatory": true, "type": "enum", "multiValue": false },
            { "id": "material", "name": "Malzeme", "mandatory": false, "type": "string", "multiValue": true }
          ]
        }
        """);

        var categoryPage = HepsiburadaJsonMapper.References("CATEGORIES", categories.RootElement, null, 0, 1000);
        var attributePage = HepsiburadaJsonMapper.References("CATEGORY_ATTRIBUTES", attributes.RootElement, "11", 0, 1000);

        Assert.Equal(2, categoryPage.TotalCount);
        Assert.False(categoryPage.HasMore);
        Assert.Null(categoryPage.Items[1].ParentExternalId);
        Assert.Equal("Elektronik > Telefonlar", categoryPage.Items[1].Path);
        Assert.Equal(1, categoryPage.Items[1].Depth);
        Assert.True(categoryPage.Items[1].IsLeaf);
        Assert.True(categoryPage.Items[1].IsActive);
        Assert.True(attributePage.Items[0].IsRequired);
        Assert.False(attributePage.Items[0].AllowsCustomValue);
        Assert.False(attributePage.Items[0].AllowsMultipleValues);
        Assert.True(attributePage.Items[1].AllowsCustomValue);
        Assert.True(attributePage.Items[1].AllowsMultipleValues);
    }

    [Fact]
    public void ReferenceMapper_UsesEnumValueIdentityAndPaginatesByPageNumber()
    {
        using var json = JsonDocument.Parse("""
        {
          "totalElements": 1201,
          "totalPages": 2,
          "data": { "content": [
            { "id": "blue-id", "value": "Mavi" },
            { "id": "red-id", "value": "Kırmızı" }
          ] }
        }
        """);

        var page = HepsiburadaJsonMapper.References("ATTRIBUTE_VALUES", json.RootElement, "11/color", 0, 1000);

        Assert.Equal("blue-id", page.Items[0].ExternalId);
        Assert.Equal("Mavi", page.Items[0].Name);
        Assert.Equal("11/color", page.Items[0].ParentExternalId);
        Assert.Equal("1", page.NextCursor);
        Assert.True(page.HasMore);
        Assert.Equal(1201, page.TotalCount);
    }

    [Fact]
    public void OrderMapper_UsesOrderNumberAndDoesNotInventPackageTimestamp()
    {
        using var json = JsonDocument.Parse("""
        {
          "orderNumber": "HB-2026-17",
          "orderDate": "2026-09-28T12:15:00Z",
          "totalPrice": 120.0,
          "items": [{
            "id": "line-1",
            "merchantSku": "merchant-17",
            "barcode": "8690000000170",
            "name": "Test product",
            "quantity": 2,
            "price": 60.0,
            "vatRate": 20,
            "status": "Open"
          }],
          "packages": [{ "packageNumber": "pkg-1", "status": "Shipped", "items": [{ "orderLineId": "line-1", "quantity": 2 }] }]
        }
        """);

        var order = HepsiburadaJsonMapper.Order(json.RootElement, "HB-2026-17");

        Assert.Equal("HB-2026-17", order.ExternalOrderId);
        Assert.Equal(order.OrderedAt, order.LastModifiedAt);
        Assert.Equal("merchant-17", order.Lines.Single().Sku);
        Assert.Equal("pkg-1", order.Packages.Single().ExternalPackageId);
        Assert.Equal(order.OrderedAt, order.Packages.Single().OccurredAt);
        Assert.Equal(2m, order.Packages.Single().Allocations.Single().AllocatedQuantity);
    }

    [Fact]
    public void PaidOrderListMapperUsesDocumentedLineFieldsWithoutCreatingAPackage()
    {
        using var json = JsonDocument.Parse("""
        {
          "id": "line-19",
          "orderId": "hb-order-19",
          "orderNumber": "HB-2026-19",
          "orderDate": "2026-09-29T09:15:00Z",
          "lastStatusUpdateDate": "2026-09-29T09:16:00Z",
          "dueDate": "2026-10-01T12:00:00Z",
          "status": "Open",
          "sku": "HB-SKU-19",
          "merchantSku": "SELLER-19",
          "name": "Test product",
          "quantity": 2,
          "unitPrice": { "currency": "TRY", "amount": 45.5 },
          "totalPrice": { "currency": "TRY", "amount": 91.0 },
          "vatRate": 20,
          "imageUrl": "https://productimages.hepsiburada.net/test/hb-19.jpg",
          "cargoCompanyModel": { "name": "HepsiJet", "shortName": "HEPSIJET" },
          "hasInvoice": false,
          "customerName": "Ayşe Test",
          "shippingAddress": { "city": "İstanbul", "town": "Kadıköy" },
          "invoice": { "address": { "city": "İstanbul", "town": "Üsküdar" } }
        }
        """);

        var order = HepsiburadaJsonMapper.PaidOrderLine(json.RootElement);

        Assert.Equal("HB-2026-19", order.ExternalOrderId);
        Assert.Equal("TRY", order.Currency);
        Assert.Equal(91m, order.GrossAmount);
        Assert.Equal(91m, order.NetAmount);
        Assert.Equal("line-19", Assert.Single(order.Lines).ExternalLineId);
        Assert.Equal("SELLER-19", order.Lines[0].Sku);
        Assert.Equal(45.5m, order.Lines[0].UnitPrice);
        Assert.Empty(order.Packages);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), order.ShipmentDueAt);
        using var customer = JsonDocument.Parse(order.CustomerSnapshotJson);
        Assert.Equal("Ayşe Test", customer.RootElement.GetProperty("name").GetString());
        Assert.Equal("NOT_INVOICED", customer.RootElement.GetProperty("marketplaceInvoiceStatus").GetString());
        Assert.Equal("HepsiJet", customer.RootElement.GetProperty("marketplaceCargoProviderName").GetString());
        using var source = JsonDocument.Parse(order.Lines[0].SourceSnapshotJson);
        Assert.Equal("https://productimages.hepsiburada.net/test/hb-19.jpg", source.RootElement.GetProperty("imageUrl").GetString());
        Assert.Contains("Kadıköy", order.ShipmentAddressSnapshotJson, StringComparison.Ordinal);
        Assert.Contains("Üsküdar", order.InvoiceAddressSnapshotJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"imageUrl\":\"https://cdn.example.test/direct.jpg\"")]
    [InlineData("\"productImageUrl\":\"https://cdn.example.test/product.jpg\"")]
    [InlineData("\"images\":[{\"url\":\"https://cdn.example.test/array.jpg\"}]")]
    public void PaidOrderListMapperNormalizesImageUrlAliases(string imageField)
    {
        using var json = JsonDocument.Parse($$"""
        {
          "id": "line-20",
          "orderNumber": "HB-2026-20",
          "orderDate": "2026-09-29T09:15:00Z",
          "merchantSku": "SELLER-20",
          "name": "Test product",
          "quantity": 1,
          "unitPrice": { "currency": "TRY", "amount": 10.0 },
          {{imageField}}
        }
        """);

        var line = Assert.Single(HepsiburadaJsonMapper.PaidOrderLine(json.RootElement).Lines);
        using var snapshot = JsonDocument.Parse(line.SourceSnapshotJson);

        Assert.StartsWith("https://cdn.example.test/", snapshot.RootElement.GetProperty("imageUrl").GetString());
    }

    [Fact]
    public void SparseHepsiburadaRefreshPreservesPreviouslyReadImageAndInvoiceEvidence()
    {
        const string firstRead = """{"imageUrl":"https://cdn.example.test/item.jpg","marketplaceInvoiceStatus":"NOT_INVOICED"}""";
        const string sparseRefresh = """{"name":"Test product","quantity":1}""";

        var merged = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(sparseRefresh, firstRead);
        using var snapshot = JsonDocument.Parse(merged);

        Assert.Equal("https://cdn.example.test/item.jpg", snapshot.RootElement.GetProperty("imageUrl").GetString());
        Assert.Equal("NOT_INVOICED", snapshot.RootElement.GetProperty("marketplaceInvoiceStatus").GetString());
        Assert.Equal("Test product", snapshot.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void PackageMapper_RequiresExplicitOrderLinkAndPreservesLineAllocations()
    {
        using var json = JsonDocument.Parse("""
        {
          "items": [{
            "orderNumber": "HB-2026-18",
            "packageNumber": "5000031611",
            "status": "Open",
            "orderDate": "2026-09-28T12:15:00Z",
            "cargoCompany": "HepsiJet",
            "barcode": "cargo-18",
            "lineItems": [{ "lineItemId": "line-18", "merchantSku": "SKU-18", "name": "Test product", "quantity": 2, "price": { "currency": "TRY", "amount": 12.5 } }]
          }],
          "totalCount": 1
        }
        """);

        var page = HepsiburadaJsonMapper.PackagePage(json.RootElement);
        var item = HepsiburadaJsonMapper.OrderPackage(page.Items.Single());

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("HB-2026-18", item.ExternalOrderId);
        Assert.Equal("5000031611", item.Package.ExternalPackageId);
        Assert.Equal("HepsiJet", item.Package.CargoProviderExternalId);
        Assert.Null(item.Package.CargoTrackingNumber);
        Assert.Equal("line-18", item.Package.Allocations.Single().ExternalLineId);
        Assert.Equal(2m, item.Package.Allocations.Single().AllocatedQuantity);
        Assert.NotNull(item.OrderSnapshot);
        Assert.Equal("HB-2026-18", item.OrderSnapshot!.ExternalOrderId);
        Assert.Equal("line-18", Assert.Single(item.OrderSnapshot.Lines).ExternalLineId);
        Assert.Equal("5000031611", Assert.Single(item.OrderSnapshot.Packages).ExternalPackageId);
    }

    [Fact]
    public void PackageTrackingInfoMapperUsesTrackingCodeAndMatchesRequestedPackage()
    {
        using var json = JsonDocument.Parse("""
        {
          "data": [{
            "packageNumber": "5000031611",
            "barcode": "cargo-barcode-18",
            "status": "InTransit",
            "cargoCompany": "HepsiJet",
            "trackingInfoCode": "tracking-18"
          }]
        }
        """);

        var tracking = HepsiburadaJsonMapper.PackageTrackingInfo(json.RootElement, "5000031611");

        Assert.Equal("InTransit", tracking.Status);
        Assert.Equal("HepsiJet", tracking.CargoCompany);
        Assert.Equal("tracking-18", tracking.TrackingInfoCode);
        Assert.Throws<JsonException>(() => HepsiburadaJsonMapper.PackageTrackingInfo(json.RootElement, "another-package"));
    }

    [Fact]
    public async Task PackageTrackingReadUsesReadOnlyEndpointAndEnrichesOrderSnapshot()
    {
        using var packageJson = JsonDocument.Parse("""
        {
          "orderNumber": "HB-2026-19",
          "packageNumber": "5000031612",
          "status": "Open",
          "orderDate": "2026-09-28T12:15:00Z",
          "barcode": "cargo-barcode-19",
          "lineItems": [{ "lineItemId": "line-19", "merchantSku": "SELLER-19", "name": "Test product", "quantity": 1 }]
        }
        """);
        var mappedPackage = HepsiburadaJsonMapper.OrderPackage(packageJson.RootElement);
        var handler = new CapturingHttpHandler("""
        [{
          "packageNumber": "5000031612",
          "barcode": "cargo-barcode-19",
          "status": "InTransit",
          "cargoCompany": "HepsiJet",
          "trackingInfoCode": "tracking-19"
        }]
        """);
        var client = CreateReadOnlyHepsiburadaClient(handler);
        var account = new HepsiburadaRequestContext(
            new MarketplaceHub.Domain.PlatformConnection
            {
                PlatformCode = "HEPSIBURADA",
                Environment = "STAGE",
                DisplayName = "fixture",
                ExternalStoreId = "merchant-19",
                Status = "ACTIVE",
                ApiVersion = "V1.0"
            },
            new Uri("https://oms.example/"), new Uri("https://listing.example/"), "integrator", "fixture-key")
        {
            IntegratorName = "ravencia_tests/1.0"
        };

        var result = await client.ReadPackageTrackingInfoAsync(account, mappedPackage, CancellationToken.None);

        Assert.Null(result.Issue);
        Assert.Equal("InTransit", result.Package.Package.RawStatus);
        Assert.Equal("HepsiJet", result.Package.Package.CargoProviderExternalId);
        Assert.Equal("tracking-19", result.Package.Package.CargoTrackingNumber);
        Assert.Equal("tracking-19", Assert.Single(result.Package.OrderSnapshot!.Packages).CargoTrackingNumber);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://oms.example/packages/merchantid/merchant-19/packagenumber/5000031612", request.Uri.AbsoluteUri);
        Assert.Equal("Basic", request.AuthorizationScheme);
    }

    [Fact]
    public void PackageMapperDoesNotGuessOrderFromInternalId()
    {
        using var json = JsonDocument.Parse("""
        { "id": "internal-order-id", "packageNumber": "5000031611", "status": "Open", "orderDate": "2026-09-28T12:15:00Z" }
        """);

        Assert.Throws<JsonException>(() => HepsiburadaJsonMapper.OrderPackage(json.RootElement));
    }

    [Fact]
    public void ClaimMapper_UsesClaimNumberOrderNumberAndLineItemId()
    {
        using var json = JsonDocument.Parse("""
        {
          "data": {
            "claims": [{
              "number": "HB-CLAIM-17",
              "status": "AwaitingAction",
              "claimType": "Return",
              "claimDate": "2026-09-28T12:15:00Z",
              "orderNumber": "HB-2026-17",
              "quantity": 2,
              "explanation": "İade talebi",
              "line": { "lineItemId": "line-17" },
              "delivery": { "code": "return-tracking-17" }
            }],
            "totalCount": 1
          }
        }
        """);

        var page = HepsiburadaJsonMapper.ClaimPage(json.RootElement);
        var claim = HepsiburadaJsonMapper.ReturnClaim(page.Items.Single());

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("HB-CLAIM-17", claim.ExternalClaimId);
        Assert.Equal("HB-2026-17", claim.ExternalOrderId);
        Assert.Equal("AwaitingAction", claim.RawStatus);
        Assert.Equal("Return", claim.ReasonCode);
        Assert.Equal("İade talebi", claim.ReasonText);
        Assert.Equal("line-17", claim.Lines.Single().ExternalOrderLineId);
        Assert.Equal(2m, claim.Lines.Single().Quantity);
        Assert.Equal("return-tracking-17", claim.CargoTrackingNumber);
    }

    [Fact]
    public void ClaimMapper_MapsTheFlatFieldsDocumentedByHepsiburada()
    {
        using var json = JsonDocument.Parse("""
        {
          "data": {
            "claims": [{
              "Number": "032985701",
              "Status": "AwaitingAction",
              "ClaimType": "Return",
              "Reason": "ProductIsBroken",
              "ClaimDate": "2026-09-28T12:15:00Z",
              "OrderNumber": "HB-2026-17",
              "Quantity": 2,
              "Explanation": "Ürün arızalı geldi",
              "lineItemId": "line-17",
              "AwaitingActionExpireDate": "2026-10-01T12:15:00Z",
              "MerchantSku": "merchant-17",
              "finalizedWith": "Refund"
            }],
            "totalCount": 1
          }
        }
        """);

        var page = HepsiburadaJsonMapper.ClaimPage(json.RootElement);
        var claim = HepsiburadaJsonMapper.ReturnClaim(page.Items.Single());

        Assert.Equal("032985701", claim.ExternalClaimId);
        Assert.Equal("HB-2026-17", claim.ExternalOrderId);
        Assert.Equal("ProductIsBroken", claim.ReasonCode);
        Assert.Equal("Ürün arızalı geldi", claim.ReasonText);
        Assert.Equal("line-17", claim.Lines.Single().ExternalOrderLineId);
        Assert.Equal(2m, claim.Lines.Single().Quantity);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T12:15:00Z"), claim.ActionDueAt);
    }

    [Fact]
    public void ClaimOrderMapper_ReconstructsOnlyTheHepsiburadaClaimLines()
    {
        const string payload = """
        {
          "claimNumber": "032985701",
          "status": "AwaitingAction",
          "claimType": "RenewProduct",
          "claimDate": "2026-09-28T12:15:00Z",
          "orderDate": "2026-09-20T10:00:00Z",
          "orderNumber": "0414141341",
          "customerId": "customer-17",
          "customerName": "Test Customer",
          "lineItemId": "line-17",
          "merchantSku": "merchant-sku-17",
          "hbSku": "HB-SKU-17",
          "productName": "Test Product",
          "quantity": 2,
          "price": { "amount": 25.50, "currency": "TRY" },
          "totalPrice": { "amount": 51.00, "currency": "TRY" }
        }
        """;

        var order = HepsiburadaJsonMapper.OrderFromReturnClaim(payload);

        Assert.NotNull(order);
        Assert.Equal("0414141341", order.OrderNumber);
        Assert.Equal(DateTimeOffset.Parse("2026-09-20T10:00:00Z"), order.OrderedAt);
        Assert.Equal("TRY", order.Currency);
        var line = Assert.Single(order.Lines);
        Assert.Equal("line-17", line.ExternalLineId);
        Assert.Equal("merchant-sku-17", line.Sku);
        Assert.Equal("Test Product", line.Title);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(25.50m, line.UnitPrice);
        Assert.Equal("ClaimCreated", line.RawStatus);
        Assert.Empty(order.Packages);
    }

    [Fact]
    public void ClaimOrderMapper_DoesNotReconstructWithoutOrderLineIdentity()
    {
        const string payload = """
        {
          "claimNumber": "032985702",
          "claimDate": "2026-09-28T12:15:00Z",
          "orderNumber": "0414141341",
          "quantity": 1,
          "merchantSku": "merchant-sku-17"
        }
        """;

        Assert.Null(HepsiburadaJsonMapper.OrderFromReturnClaim(payload));
    }

    [Fact]
    public void ClaimWebhookIdentity_IsStableAcrossJsonPropertyOrdering()
    {
        var first = System.Text.Encoding.UTF8.GetBytes("""
        { "claimNumber":"claim-17", "status":"AwaitingAction", "claimDate":"2026-09-28T12:15:00Z", "orderNumber":"order-17", "lineItemId":"line-17", "quantity":1 }
        """);
        var reordered = System.Text.Encoding.UTF8.GetBytes("""
        {
          "quantity": 1,
          "lineItemId": "line-17",
          "orderNumber": "order-17",
          "claimDate": "2026-09-28T12:15:00Z",
          "status": "AwaitingAction",
          "claimNumber": "claim-17"
        }
        """);

        var firstIdentity = HepsiburadaWebhookIdentity.Create(first);
        var reorderedIdentity = HepsiburadaWebhookIdentity.Create(reordered);

        Assert.NotNull(firstIdentity);
        Assert.NotNull(reorderedIdentity);
        Assert.Equal(firstIdentity.ExternalMessageId, reorderedIdentity.ExternalMessageId);
        Assert.NotEqual(firstIdentity.PayloadHash, reorderedIdentity.PayloadHash);
        Assert.Equal(MarketplaceJobTypes.HepsiburadaWebhookIngest, MarketplaceJobTypes.ForPlatform("HEPSIBURADA", MarketplaceJobTypes.WebhookIngest));
        Assert.Equal(MarketplaceJobTypes.HepsiburadaOrderStatusSync, MarketplaceJobTypes.ForPlatform("HEPSIBURADA", MarketplaceJobTypes.OrderStatusSync));
    }

    [Fact]
    public void ClaimWebhookIdentity_RejectsPayloadWithoutLineIdentity()
    {
        var raw = System.Text.Encoding.UTF8.GetBytes("""
        { "claimNumber":"claim-18", "status":"AwaitingAction", "claimDate":"2026-09-28T12:15:00Z", "orderNumber":"order-17" }
        """);

        Assert.Null(HepsiburadaWebhookIdentity.Create(raw));
    }

    [Fact]
    public void ClaimPackageMapper_ProjectsReplacementPackageAndClaimToItsOrder()
    {
        using var json = JsonDocument.Parse("""
        {
          "packageNumber": "HB-REPL-500",
          "status": "Intransit",
          "customerId": "customer-17",
          "barcode": "TRACK-500",
          "cargoCompany": "Aras",
          "recipientName": "Test Customer",
          "shippingAddressDetail": "Test Street 1",
          "shippingCountryCode": "TR",
          "shippingDistrict": "Kadikoy",
          "shippingTown": "Istanbul",
          "shippingCity": "Istanbul",
          "claims": [{
            "id": "claim-id-17",
            "number": "032985701",
            "status": "Accepted",
            "claimType": "RenewProduct",
            "claimDate": "2026-09-28T12:15:00Z",
            "quantity": 1,
            "orderNumber": "0414141341",
            "orderDate": "2026-09-20T10:00:00Z",
            "customerName": "Test Customer",
            "line": {
              "lineItemId": "line-17",
              "merchantSku": "merchant-sku-17",
              "hbSku": "HB-SKU-17",
              "productName": "Replacement Product",
              "quantity": 1,
              "price": 51.00
            }
          }],
          "direction": "MerchantToCustomer",
          "slot": "REPLACEMENT"
        }
        """);

        var order = HepsiburadaJsonMapper.ClaimPackageOrder(json.RootElement);
        var package = Assert.Single(order.Packages);

        Assert.Equal("0414141341", order.OrderNumber);
        Assert.Equal("HB-REPL-500", package.ExternalPackageId);
        Assert.Equal("Intransit", package.RawStatus);
        Assert.Equal("Aras", package.CargoProviderExternalId);
        Assert.Equal("TRACK-500", package.CargoTrackingNumber);
        Assert.Equal("REPLACEMENT", package.CreatedBy);
        Assert.Equal("line-17", Assert.Single(package.Allocations).ExternalLineId);
        Assert.Equal(1m, package.Allocations.Single().AllocatedQuantity);
        Assert.Equal("merchant-sku-17", Assert.Single(order.Lines).Sku);
        Assert.Equal("Replacement Product", order.Lines.Single().Title);
        Assert.True(HepsiburadaJsonMapper.IsClaimPackageNotification(json.RootElement));
    }

    [Fact]
    public void ClaimPackageWebhookIdentityUsesPackageNumberAndResourceType()
    {
        var raw = System.Text.Encoding.UTF8.GetBytes("""
        {
          "packageNumber": "HB-REPL-501",
          "status": "Open",
          "claims": [{
            "number": "claim-19",
            "status": "Accepted",
            "claimType": "RenewProduct",
            "claimDate": "2026-09-28T12:15:00Z",
            "orderNumber": "order-19",
            "orderDate": "2026-09-20T10:00:00Z",
            "line": { "lineItemId": "line-19", "quantity": 1, "merchantSku": "sku-19", "price": 9.99 }
          }]
        }
        """);

        var identity = HepsiburadaWebhookIdentity.Create(raw);

        Assert.NotNull(identity);
        Assert.Equal("CLAIM_PACKAGE", identity.ResourceType);
        Assert.StartsWith("claim_package:HB-REPL-501:", identity.ExternalMessageId);
    }

    [Fact]
    public void HepsiburadaWebhookAuth_RequiresConfiguredBasicOrApiKeyHeader()
    {
        var basic = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hb-user:hb-secret"));

        Assert.True(HepsiburadaWebhookVerifier.IsAuthenticated(
            "BASIC_AUTHENTICATION", "hb-user", "hb-secret", null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["authorization"] = basic }));
        Assert.False(HepsiburadaWebhookVerifier.IsAuthenticated(
            "BASIC_AUTHENTICATION", "hb-user", "different-secret", null,
            new Dictionary<string, string> { ["Authorization"] = basic }));
        Assert.True(HepsiburadaWebhookVerifier.IsAuthenticated(
            "API_KEY", null, null, "webhook-secret",
            new Dictionary<string, string> { ["X-API-Key"] = "webhook-secret" }));
        Assert.False(HepsiburadaWebhookVerifier.IsAuthenticated(
            "API_KEY", null, null, "webhook-secret",
            new Dictionary<string, string> { ["X-API-Key"] = "wrong-secret" }));
    }

    [Fact]
    public void HepsiburadaBasicAuthUsesIntegratorUsernameAndServiceKeyAsPassword()
    {
        var credentials = HepsiburadaAuthenticationHandler.ResolveBasicCredentials(" ravencia_dev ", " example-service-key\r\n");

        Assert.Equal("ravencia_dev", credentials.Username);
        Assert.Equal("example-service-key", credentials.Password);
    }

    [Fact]
    public void HepsiburadaBasicAuthCanRetryWithMerchantIdAndAvoidDuplicateFallback()
    {
        const string merchantId = "62201bf1-2e64-4d18-9aac-fc38d1ea040c";

        Assert.Equal(merchantId, HepsiburadaAuthenticationHandler.MerchantIdUsernameFallback("kodanka_dev", merchantId));
        Assert.Null(HepsiburadaAuthenticationHandler.MerchantIdUsernameFallback(merchantId, merchantId));
        Assert.Null(HepsiburadaAuthenticationHandler.MerchantIdUsernameFallback("kodanka_dev", " "));
    }

    [Fact]
    public void HepsiburadaAuthenticationErrorsDistinguishRejectedCredentialsFromForbiddenAccess()
    {
        var credentialsRejected = HepsiburadaHttpClient.Error(System.Net.HttpStatusCode.Unauthorized, null, null);
        var accessForbidden = HepsiburadaHttpClient.Error(System.Net.HttpStatusCode.Forbidden, null, null);

        Assert.Equal("HEPSIBURADA_CREDENTIALS_REJECTED", credentialsRejected.Code);
        Assert.Equal(401, credentialsRejected.HttpStatus);
        Assert.Equal("HEPSIBURADA_ACCESS_FORBIDDEN", accessForbidden.Code);
        Assert.Equal(403, accessForbidden.HttpStatus);
    }

    [Theory]
    [InlineData("kodanka_dev")]
    [InlineData("merchant-17")]
    public void HepsiburadaRequestUsesRegisteredIntegratorUserAgentWithEitherBasicUsername(string basicUsername)
    {
        var connection = new MarketplaceHub.Domain.PlatformConnection
        {
            PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "test",
            ExternalStoreId = "merchant-17", Status = "DRAFT", ApiVersion = "V1.0"
        };
        var context = new HepsiburadaRequestContext(connection, new Uri("https://oms.example/"), new Uri("https://listing.example/"), basicUsername, "example-key")
        { IntegratorName = "kodanka_dev" };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://oms.example/orders");

        Assert.True(HepsiburadaHttpClient.ApplyAuthentication(request, context));
        Assert.Equal("kodanka_dev", request.Headers.UserAgent.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal($"{basicUsername}:example-key", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
        Assert.False(HepsiburadaHttpClient.ApplyAuthentication(new HttpRequestMessage(), context with { IntegratorName = "invalid\r\nX-Header: value" }));
    }

    [Fact]
    public void ClaimMapper_RejectsClaimsWithoutOrderOrLineIdentity()
    {
        using var json = JsonDocument.Parse("""
        { "number": "HB-CLAIM-18", "status": "NewRequest", "claimDate": "2026-09-28T12:15:00Z", "line": { "quantity": 1 } }
        """);

        Assert.Throws<JsonException>(() => HepsiburadaJsonMapper.ReturnClaim(json.RootElement));
    }

    [Fact]
    public void HepsiburadaReturnJobIsPlatformRoutedAndClaimPathIsEscaped()
    {
        var context = new HepsiburadaRequestContext(
            new MarketplaceHub.Domain.PlatformConnection
            {
                PlatformCode = "HEPSIBURADA",
                Environment = "STAGE",
                DisplayName = "test",
                ExternalStoreId = "merchant/17",
                Status = "ACTIVE",
                ApiVersion = "V1.0"
            },
            new Uri("https://oms.example/"),
            new Uri("https://listing.example/"),
            "user",
            "secret");

        Assert.Equal(MarketplaceJobTypes.HepsiburadaReturnSync, MarketplaceJobTypes.ForPlatform("HEPSIBURADA", MarketplaceJobTypes.ReturnSync));
        Assert.Equal("claims/merchantId/merchant%2F17/status/awaitingpreapproval?offset=0&limit=10", HepsiburadaHttpClient.Claims(context, "awaitingpreapproval", "offset=0&limit=10"));
        Assert.Equal("claims/number/claim%2F17/preapprovalconfirm", HepsiburadaHttpClient.ConfirmClaimPreApproval(context, "claim/17"));
        Assert.Equal("offset=20&limit=100&beginDate=2026-09-28%2012%3A15&endDate=2026-09-29%2012%3A15", HepsiburadaHttpClient.ClaimQuery(20, 101, DateTimeOffset.Parse("2026-09-28T12:15:00Z"), DateTimeOffset.Parse("2026-09-29T12:15:00Z")));
        Assert.Equal("api/categories/get-all-categories?leaf=true&status=ACTIVE&available=true&version=1&page=3&size=1000", HepsiburadaHttpClient.Categories(3, 1000));
        Assert.Equal("api/categories/category%2F11/attributes?version=2", HepsiburadaHttpClient.CategoryAttributes("category/11"));
        Assert.Equal("api/categories/11/attribute/color/values?version=5&page=2&size=1000", HepsiburadaHttpClient.AttributeValues("11", "color", 2, 1000));
    }

    [Fact]
    public void HepsiburadaReferenceReadIsRegisteredAndCatalogBootstrapRequiresAConfiguredProductionUrl()
    {
        Assert.Contains(MarketplaceCapabilities.ReferenceRead, MarketplaceConnectionService.HepsiburadaCapabilityCodes);
        Assert.Contains(InvoicingCapabilities.InvoiceDeliver, MarketplaceConnectionService.HepsiburadaCapabilityCodes);
        Assert.True(MarketplaceConnectionService.ShouldBootstrapHepsiburadaCatalogReferences("STAGE", null));
        Assert.False(MarketplaceConnectionService.ShouldBootstrapHepsiburadaCatalogReferences("PRODUCTION", null));
        Assert.False(MarketplaceConnectionService.ShouldBootstrapHepsiburadaCatalogReferences("PRODUCTION", "http://catalog.example/product/"));
        Assert.True(MarketplaceConnectionService.ShouldBootstrapHepsiburadaCatalogReferences("PRODUCTION", "https://catalog.example/product/"));
    }

    [Fact]
    public void HepsiburadaActivationQueuesAFullReturnImport()
    {
        var connectionId = Guid.NewGuid();
        var bootstrap = MarketplaceConnectionService.CreateReturnActivationBootstrap("hepsiburada", connectionId);

        Assert.NotNull(bootstrap);
        Assert.Equal(MarketplaceJobTypes.HepsiburadaReturnSync, bootstrap.Value.JobType);
        using var payload = JsonDocument.Parse(bootstrap.Value.PayloadJson);
        Assert.Equal(connectionId, payload.RootElement.GetProperty("connectionId").GetGuid());
        Assert.True(payload.RootElement.GetProperty("forceFull").GetBoolean());
        Assert.Null(MarketplaceConnectionService.CreateReturnActivationBootstrap("TRENDYOL_EFATURAM", connectionId));
    }

    [Fact]
    public void RateLimitHeadersRetainLimitRemainingResetAndRetryAfter()
    {
        var adapter = new HepsiburadaHttpClient(
            null!,
            null!,
            Options.Create(new HepsiburadaOptions()),
            TimeProvider.System,
            new ConfigurationBuilder().Build(),
            NullLogger<HepsiburadaHttpClient>.Instance);
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "1000");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "12");
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(8));

        var rate = adapter.RateLimit(response);

        Assert.NotNull(rate);
        Assert.Equal(1000, rate.Limit);
        Assert.Equal(0, rate.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(8), rate.RetryAfter);
        Assert.NotNull(rate.ResetAt);

        using var resetOnlyResponse = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        resetOnlyResponse.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "12");
        var resetOnlyRate = adapter.RateLimit(resetOnlyResponse);
        var rateLimited = HepsiburadaHttpClient.Error(resetOnlyResponse.StatusCode, resetOnlyRate?.RetryAfter, null);

        Assert.Equal(TimeSpan.FromSeconds(12), rateLimited.RetryAfter);
    }

    [Fact]
    public async Task ProductAndInventoryWritesRemainDisabled()
    {
        var options = new HepsiburadaOptions();
        var adapter = new HepsiburadaHttpClient(
            null!,
            null!,
            Options.Create(options),
            TimeProvider.System,
            new ConfigurationBuilder().Build(),
            NullLogger<HepsiburadaHttpClient>.Instance);

        var writes = new[]
        {
            await adapter.CreateAsync(null!, null!, CancellationToken.None),
            await adapter.UpdateUnapprovedAsync(null!, null!, CancellationToken.None),
            await adapter.UpdateApprovedContentAsync(null!, null!, CancellationToken.None),
            await adapter.UpdateApprovedVariantsAsync(null!, null!, CancellationToken.None),
            await adapter.UpdateApprovedDeliveryAsync(null!, null!, CancellationToken.None),
            await adapter.ArchiveAsync(null!, "{}", CancellationToken.None),
            await adapter.PushPriceAndInventoryAsync(null!, "{}", CancellationToken.None)
        };

        Assert.All(writes, result => Assert.Equal(AdapterErrorClass.NotSupported, result.Error!.Class));
        Assert.Equal("UNVERIFIED", options.AuthenticationMode);
    }

    [Fact]
    public async Task HepsiburadaProductUpdateWriteRemainsAuthGated()
    {
        var adapter = new HepsiburadaHttpClient(
            null!,
            null!,
            Options.Create(new HepsiburadaOptions()),
            TimeProvider.System,
            new ConfigurationBuilder().Build(),
            NullLogger<HepsiburadaHttpClient>.Instance);
        var publication = new ProductUpdatePublication(Guid.NewGuid(), "APPROVED", "hash", "{}", "{}", "{}", "{}");

        var result = await adapter.UpdateApprovedContentAsync(null!, publication, CancellationToken.None);

        Assert.Equal(AdapterErrorClass.NotSupported, result.Error!.Class);
    }

    [Fact]
    public async Task HepsiburadaInvoiceDeliveryIsRoutedButRemainsDisabled()
    {
        var adapter = new HepsiburadaHttpClient(
            null!,
            null!,
            Options.Create(new HepsiburadaOptions()),
            TimeProvider.System,
            new ConfigurationBuilder().Build(),
            NullLogger<HepsiburadaHttpClient>.Instance);

        var delivery = await adapter.DeliverAsync(null!, new InvoiceDeliveryCommand("package-1", "LINK", "{}", "hash"), CancellationToken.None);
        var readback = await adapter.QueryDeliveryAsync(null!, new ExternalInvoiceDeliveryReference("package-1"), CancellationToken.None);
        var label = await adapter.CreateCommonLabelAsync(null!, null!, CancellationToken.None);
        var packageAction = await adapter.ExecutePackageActionAsync(null!, null!, CancellationToken.None);
        var testOrder = await adapter.CreateStageTestOrderAsync(null!, "barcode", CancellationToken.None);

        Assert.Equal(AdapterErrorClass.NotSupported, delivery.Error!.Class);
        Assert.Equal(AdapterErrorClass.NotSupported, readback.Error!.Class);
        Assert.Equal(AdapterErrorClass.NotSupported, label.Error!.Class);
        Assert.Equal(AdapterErrorClass.NotSupported, packageAction.Error!.Class);
        Assert.Equal(AdapterErrorClass.NotSupported, testOrder.Error!.Class);
    }

    [Fact]
    public void InvoiceDeliveryPolicy_RequiresMatchingPackageHttpsLinkDateAndSupportedContentType()
    {
        var command = new InvoiceDeliveryCommand("package-1", "LINK", """
        {
          "shipmentPackageId": "package-1",
          "orderNumber": "order-1",
          "invoiceLink": "https://files.example/invoice.pdf",
          "arrangementDate": "2026-09-29T12:00:00+03:00",
          "contentType": "Application/PDF"
        }
        """, "hash");

        Assert.True(HepsiburadaInvoiceDeliveryPolicy.TryCreate(command, out var request, out var error));
        Assert.Empty(error);
        Assert.NotNull(request);
        Assert.Equal("package-1", request.PackageNumber);
        Assert.Equal("order-1", request.OrderNumber);
        Assert.Equal("application/pdf", request.ContentType);
        Assert.Equal("https", request.InvoiceLink.Scheme);

        var mismatchedPackage = command with { PayloadJson = command.PayloadJson.Replace("package-1", "package-2", StringComparison.Ordinal) };
        Assert.False(HepsiburadaInvoiceDeliveryPolicy.TryCreate(mismatchedPackage, out _, out _));
        var httpLink = command with { PayloadJson = command.PayloadJson.Replace("https://", "http://", StringComparison.Ordinal) };
        Assert.False(HepsiburadaInvoiceDeliveryPolicy.TryCreate(httpLink, out _, out _));
        var unsupportedContentType = command with { PayloadJson = command.PayloadJson.Replace("Application/PDF", "image/png", StringComparison.Ordinal) };
        Assert.False(HepsiburadaInvoiceDeliveryPolicy.TryCreate(unsupportedContentType, out _, out _));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void InvoiceStatusMapper_ReadsHasInvoice(string rawValue, bool expected)
    {
        using var json = JsonDocument.Parse($"{{\"hasInvoice\":{rawValue}}}");

        Assert.Equal(expected, HepsiburadaJsonMapper.InvoiceUploaded(json.RootElement));
    }

    [Fact]
    public async Task HepsiburadaReturnWriteRemainsAuthGatedAndReasonsAreAvailable()
    {
        var adapter = new HepsiburadaHttpClient(
            null!,
            null!,
            Options.Create(new HepsiburadaOptions()),
            TimeProvider.System,
            new ConfigurationBuilder().Build(),
            NullLogger<HepsiburadaHttpClient>.Instance);

        var returnAction = await adapter.ExecuteAsync(null!, new ReturnActionCommand("claim-1", [], "APPROVE", null, null, []), CancellationToken.None);
        var preApprovalAction = await adapter.ExecuteAsync(null!, new ReturnActionCommand("claim-1", [], "PREAPPROVAL_CONFIRM", null, null, []), CancellationToken.None);
        var reasons = await adapter.IssueReasonsAsync(null!, CancellationToken.None);
        var packageAction = await adapter.ExecutePackageActionAsync(null!, new PackageActionCommand("package-1", "SHIP", "{}"), CancellationToken.None);
        var label = await adapter.GetCommonLabelAsync(null!, "tracking", CancellationToken.None);

        Assert.Equal(AdapterErrorClass.NotSupported, returnAction.Error!.Class);
        Assert.Equal(AdapterErrorClass.NotSupported, preApprovalAction.Error!.Class);
        Assert.True(reasons.IsSuccess);
        Assert.Contains(reasons.Value!, reason => reason.Id == "ProductNotDefective");
        Assert.Equal(AdapterErrorClass.NotSupported, packageAction.Error!.Class);
        Assert.Equal(AdapterErrorClass.NotSupported, label.Error!.Class);
    }

    private static HepsiburadaHttpClient CreateReadOnlyHepsiburadaClient(HttpMessageHandler handler) => new(
        new CapturingHttpClientFactory(handler),
        null!,
        Options.Create(new HepsiburadaOptions { AuthenticationMode = "BASIC" }),
        TimeProvider.System,
        new ConfigurationBuilder().Build(),
        NullLogger<HepsiburadaHttpClient>.Instance);

    private sealed class CapturingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHttpHandler(string responseBody) : HttpMessageHandler
    {
        public List<(string Method, Uri Uri, string? AuthorizationScheme)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!, request.Headers.Authorization?.Scheme));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
