using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaAdapterTests
{
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
            "lineItems": [{ "lineItemId": "line-18", "quantity": 2 }]
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
        Assert.Equal("cargo-18", item.Package.CargoTrackingNumber);
        Assert.Equal("line-18", item.Package.Allocations.Single().ExternalLineId);
        Assert.Equal(2m, item.Package.Allocations.Single().AllocatedQuantity);
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
        Assert.Equal("offset=20&limit=100&beginDate=2026-09-28%2012%3A15&endDate=2026-09-29%2012%3A15", HepsiburadaHttpClient.ClaimQuery(20, 101, DateTimeOffset.Parse("2026-09-28T12:15:00Z"), DateTimeOffset.Parse("2026-09-29T12:15:00Z")));
        Assert.Equal("api/categories/get-all-categories?leaf=true&status=ACTIVE&available=true&version=1&page=3&size=1000", HepsiburadaHttpClient.Categories(3, 1000));
        Assert.Equal("api/categories/category%2F11/attributes?version=2", HepsiburadaHttpClient.CategoryAttributes("category/11"));
        Assert.Equal("api/categories/11/attribute/color/values?version=5&page=2&size=1000", HepsiburadaHttpClient.AttributeValues("11", "color", 2, 1000));
    }

    [Fact]
    public void HepsiburadaReferenceReadIsRegisteredAndCatalogBootstrapRequiresAConfiguredProductionUrl()
    {
        Assert.Contains(MarketplaceCapabilities.ReferenceRead, MarketplaceConnectionService.HepsiburadaCapabilityCodes);
        Assert.True(MarketplaceConnectionService.ShouldBootstrapHepsiburadaCatalogReferences("STAGE", null));
        Assert.False(MarketplaceConnectionService.ShouldBootstrapHepsiburadaCatalogReferences("PRODUCTION", null));
        Assert.False(MarketplaceConnectionService.ShouldBootstrapHepsiburadaCatalogReferences("PRODUCTION", "http://catalog.example/product/"));
        Assert.True(MarketplaceConnectionService.ShouldBootstrapHepsiburadaCatalogReferences("PRODUCTION", "https://catalog.example/product/"));
    }

    [Fact]
    public void RateLimitHeadersRetainLimitRemainingResetAndRetryAfter()
    {
        var adapter = new HepsiburadaHttpClient(
            null!,
            null!,
            Options.Create(new HepsiburadaOptions()),
            TimeProvider.System,
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
    public async Task HepsiburadaInvoiceDeliveryIsRoutedButRemainsDisabled()
    {
        var adapter = new HepsiburadaHttpClient(
            null!,
            null!,
            Options.Create(new HepsiburadaOptions()),
            TimeProvider.System,
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
    public async Task HepsiburadaReturnAndShipmentWritesRemainDisabled()
    {
        var adapter = new HepsiburadaHttpClient(
            null!,
            null!,
            Options.Create(new HepsiburadaOptions()),
            TimeProvider.System,
            NullLogger<HepsiburadaHttpClient>.Instance);

        var returnAction = await adapter.ExecuteAsync(null!, new ReturnActionCommand("claim-1", [], "APPROVE", null, null, []), CancellationToken.None);
        var reasons = await adapter.IssueReasonsAsync(null!, CancellationToken.None);
        var packageAction = await adapter.ExecutePackageActionAsync(null!, new PackageActionCommand("package-1", "SHIP", "{}"), CancellationToken.None);
        var label = await adapter.GetCommonLabelAsync(null!, "tracking", CancellationToken.None);

        Assert.Equal(AdapterErrorClass.NotSupported, returnAction.Error!.Class);
        Assert.Equal(AdapterErrorClass.NotSupported, reasons.Error!.Class);
        Assert.Equal(AdapterErrorClass.NotSupported, packageAction.Error!.Class);
        Assert.Equal(AdapterErrorClass.NotSupported, label.Error!.Class);
    }
}
