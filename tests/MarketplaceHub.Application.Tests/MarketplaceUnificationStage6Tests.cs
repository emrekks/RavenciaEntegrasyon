using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The shared rules of stage 6 of the cross-marketplace report (items 13-17): one case per marketplace where the rule is common.
public sealed class MarketplaceUnificationStage6Tests
{
    private const string GatewayPage = "<!DOCTYPE html><html><body>Request blocked</body></html>";

    // ---- 16: a 403 HTML page is the security gateway on every marketplace (temporary, a quarter hour) ----

    [Fact]
    public void A403HtmlPageIsAGatewayBlockOnEveryMarketplace()
    {
        var errors = new[]
        {
            MarketplaceErrorRules.GatewayBlocked(HttpStatusCode.Forbidden, GatewayPage, "Trendyol", "TRENDYOL")!,
            MarketplaceErrorRules.GatewayBlocked(HttpStatusCode.Forbidden, GatewayPage, "Hepsiburada", "HEPSIBURADA")!,
            N11HttpClient.Error(HttpStatusCode.Forbidden, GatewayPage, null),
            N11HttpClient.SoapHttpError(HttpStatusCode.Forbidden, GatewayPage, "GetProductList")!,
            PazaramaHttpClient.Error(HttpStatusCode.Forbidden, GatewayPage, null),
            PttAvmHttpClient.SoapResponse(HttpStatusCode.Forbidden, GatewayPage, "StokKontrolListesi").Error!,
            ShopifyHttpClient.MapHttpError(HttpStatusCode.Forbidden, GatewayPage)
        };

        Assert.All(errors, error =>
        {
            Assert.Equal(AdapterErrorClass.RateLimit, error.Class);
            Assert.EndsWith("_GATEWAY_BLOCKED", error.Code, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.FromMinutes(15), error.RetryAfter);
            // Never sent to the API, so never an outcome-unknown write.
            Assert.False(WriteOutcomePolicy.IsUncertain(error));
        });
    }

    [Fact]
    public void A403WithoutAPageAndA401StayRefusalsOfAccess()
    {
        Assert.Null(MarketplaceErrorRules.GatewayBlocked(HttpStatusCode.Unauthorized, GatewayPage, "Trendyol", "TRENDYOL"));
        Assert.Equal(AdapterErrorClass.Authentication, N11HttpClient.Error(HttpStatusCode.Forbidden, """{"message":"denied"}""", null).Class);
        Assert.Equal(AdapterErrorClass.Authentication, N11HttpClient.Error(HttpStatusCode.Unauthorized, GatewayPage, null).Class);
        Assert.Equal(AdapterErrorClass.Authentication, HepsiburadaHttpClient.Error(HttpStatusCode.Forbidden, null, null).Class);
        Assert.Equal(AdapterErrorClass.Authentication, ShopifyHttpClient.MapHttpError(HttpStatusCode.Forbidden, """{"errors":"denied"}""").Class);
    }

    [Fact]
    public void AnUnexplainedServerFaultIsRetriedOnReadsAndUncertainOnWritesEverywhere()
    {
        var faults = new[]
        {
            HepsiburadaHttpClient.Error(HttpStatusCode.InternalServerError, null, null),
            N11HttpClient.Error(HttpStatusCode.InternalServerError, "", null),
            N11HttpClient.SoapHttpError(HttpStatusCode.BadGateway, "", "GetProductList")!,
            PazaramaHttpClient.Error(HttpStatusCode.InternalServerError, "", null),
            PttAvmHttpClient.SoapResponse(HttpStatusCode.InternalServerError, "", "StokKontrolListesi").Error!,
            ShopifyHttpClient.MapHttpError(HttpStatusCode.ServiceUnavailable, "")
        };

        Assert.All(faults, fault =>
        {
            Assert.Equal(AdapterErrorClass.Remote5xx, fault.Class);
            Assert.Equal(JobCompletionKind.Retry, JobExecutionResult.FromAdapterError(fault).Kind);
            Assert.True(WriteOutcomePolicy.IsUncertain(fault));
        });
    }

    // E-Faturam's 408 on an invoice creation may hide a created invoice: outcome unknown, never a refusal to resend.
    [Fact]
    public void AnEFaturamTimeoutLeavesTheInvoiceOutcomeUnknown()
    {
        var timeout = TrendyolEFaturamErrorMapper.FromAuthorizedStatus(HttpStatusCode.RequestTimeout, null, "req-1");

        Assert.Equal(AdapterErrorClass.TransientNetwork, timeout.Class);
        Assert.True(InvoiceSubmissionOutcomePolicy.IsOutcomeUnknown(timeout));
    }

    // The retest goes by the stored error class; a row without one keeps the code's spelling.
    [Theory]
    [InlineData("RateLimit", "ANY_CODE", true)]
    [InlineData("Remote5xx", "N11_REMOTE_FAULT", true)]
    [InlineData("TransientNetwork", "SOMETHING", true)]
    [InlineData("Validation", "PAZARAMA_REMOTE_ERROR", false)]
    [InlineData(null, "PAZARAMA_GATEWAY_BLOCKED", true)]
    [InlineData(null, "PAZARAMA_REQUEST_REJECTED", false)]
    public void ARetestFollowsTheStoredErrorClass(string? errorClass, string code, bool retested)
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(retested, ConnectionRetestPolicy.ShouldRetest("DRAFT", now.AddMinutes(-16), code, errorClass, now));
    }

    [Fact]
    public void AnAuthenticationClassIsRetestedAsARefusalOfCredentials()
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.True(ConnectionRetestPolicy.ShouldRetest("DRAFT", now.AddMinutes(-61), "WHATEVER", "Authentication", now, now.AddMinutes(-122)));
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", now.AddMinutes(-61), "PAZARAMA_CREDENTIALS_REJECTED", "Validation", now, now.AddMinutes(-122)));
    }

    // ---- 15: a page without its list is a broken contract, never "no orders" or "last page" ----

    [Fact]
    public void ATrendyolOrderOrClaimPageWithoutContentIsAContractError()
    {
        Assert.Throws<JsonException>(() => TrendyolJsonMapper.Orders("""{"totalElements":0,"totalPages":0}"""));
        Assert.Throws<JsonException>(() => TrendyolJsonMapper.Returns("""{"page":0}"""));
        Assert.Throws<JsonException>(() => TrendyolJsonMapper.Returns("""{"totalElements":3,"totalPages":1}"""));
        Assert.Contains("totalElements:Number", Assert.Throws<JsonException>(() => TrendyolJsonMapper.Returns("""{"totalElements":3}""")).Message);
        Assert.Empty(TrendyolJsonMapper.Orders("""{"content":[],"totalPages":0}""").Items);
    }

    // Live since 5 Nov 2026 22:43: an empty claimItemStatus bucket answers without a content list; it says totalElements 0.
    [Theory]
    [InlineData("""{"totalElements":0,"totalPages":0,"page":0,"size":50}""")]
    [InlineData("""{"content":null,"totalElements":0,"totalPages":0,"page":0,"size":50}""")]
    public void AnEmptyTrendyolClaimBucketThatSaysZeroIsAnEmptyPage(string json)
    {
        var page = TrendyolJsonMapper.Returns(json);
        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void AnN11PageWithoutTotalPagesIsNotTakenForTheLastOne()
    {
        var page = N11OrderMapper.Packages("""{"content":[{"id":1}],"page":0}""");

        Assert.True(page.HasMore);
        Assert.False(N11OrderMapper.Packages("""{"content":[],"page":1}""").HasMore);
        Assert.False(N11OrderMapper.Packages("""{"content":[{"id":1}],"page":0,"totalPages":1}""").HasMore);
    }

    // ---- 14: "already there" in each marketplace's words goes to the approval check ----

    [Theory]
    [InlineData("Aynı barkodlu bir ürününüz bulunduğundan yeni ürün oluşturulamaz. Barkod: X", true)]
    [InlineData("Bu barkod zaten mevcut", true)]
    [InlineData("Ürün sistemde zaten kayıtlı", true)]
    [InlineData("Product already exists", true)]
    [InlineData("Kategori özelliği eksik", false)]
    [InlineData(null, false)]
    public void ARefusalSayingTheBarcodeIsThereIsReadByTheApprovalCheck(string? reason, bool exists) =>
        Assert.Equal(exists, ProductBatchResultPolicy.SaysBarcodeExists(reason));

    // ---- 13: shared approval words; PTT AVM's Aktif decides, missing is unknown ----

    [Theory]
    [InlineData("ON_SALE", "APPROVED")]
    [InlineData("OFF_SALE", "ARCHIVED")]
    [InlineData(null, "UNKNOWN")]
    public void APttAvmListingIsLiveClosedOrUnknownByItsAktif(string? saleState, string status) =>
        Assert.Equal(status, PttAvmProductPayloadRules.ListedStatus(saleState));

    // ---- 17: a notification of another store is dropped; an unknown Shopify topic is not an order ----

    [Theory]
    [InlineData("""{"content":[{"orderNumber":"1","supplierId":12345}]}""", null)]
    [InlineData("""{"content":[{"orderNumber":"1","supplierId":"12345"}]}""", null)]
    [InlineData("""{"content":[{"orderNumber":"1"}]}""", null)]
    [InlineData("""{"content":[{"orderNumber":"1","supplierId":12345},{"orderNumber":"2","supplierId":999}]}""", "999")]
    public void ATrendyolPackageOfAnotherSellerIsForeign(string page, string? foreign) =>
        Assert.Equal(foreign, MarketplaceWebhookService.ForeignSupplier(page, "12345"));

    [Theory]
    [InlineData("my-shop.myshopify.com", false, null)]
    [InlineData("MY-SHOP.myshopify.com", true, null)]
    [InlineData("other.myshopify.com", false, "other.myshopify.com")]
    [InlineData(null, false, null)]
    [InlineData(null, true, "mağaza adı yok")]
    public void AShopifyDeliveryOfAnotherShopIsForeign(string? header, bool required, string? foreign) =>
        Assert.Equal(foreign, MarketplaceWebhookService.ForeignShop(header, "my-shop", required));

    [Theory]
    [InlineData("orders/updated", "ORDERS")]
    [InlineData("refunds/create", "REFUNDS")]
    [InlineData("products/delete", ShopifyWebhookVerifier.ProductDeleteResourceType)]
    [InlineData("customers/create", ShopifyWebhookVerifier.UnknownEventResourceType)]
    [InlineData("orders/paid", ShopifyWebhookVerifier.UnknownEventResourceType)]
    public void OnlyTheSubscribedShopifyOrderTopicsAreOrders(string topic, string resourceType) =>
        Assert.Equal(resourceType, ShopifyWebhookVerifier.ResourceType(topic));
}
