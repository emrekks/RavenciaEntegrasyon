using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient(
    IHttpClientFactory clients,
    HepsiburadaAuthenticationHandler authentication,
    IOptions<HepsiburadaOptions> options,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<HepsiburadaHttpClient> logger)
    : IConnectionPort, IReferenceDataPort, IProductPort, IHepsiburadaProductMatchPort, IProductVisualLookupPort, IInventoryPricePort, IOrderPort, IOrderPackageReadPort, IReturnPort, IInvoiceMarketplacePort
{
    private readonly HepsiburadaOptions settings = options.Value;
    private bool GlobalWritesEnabled => configuration.GetValue<bool>("FeatureFlags:ExternalWrites");
    private static readonly string[] ClaimStatuses = ["NewRequest", "AwaitingAction", "InDispute", "Accepted", "Rejected", "Refunded", "Cancelled", "AwaitingPreApproval"];
    private static readonly ReturnIssueReason[] ClaimRejectionReasons =
    [
        new("CustomerReturnedWrongItem", "İade edilen ürün siparişteki ürün değil", false),
        new("ProductIsDamaged", "İade edilen ürün kusurlu veya hasarlı", false),
        new("MissingQuantity", "İade edilen ürün adedi eksik", false),
        new("NoSuchAccessory", "İade edilen ürün tekrar satılabilir durumda değil", false),
        new("BoxIsEmptyWithReport", "Paket boş, tutanak mevcut", false),
        new("BoxIsEmptyWithoutReport", "Paket boş, tutanak yok", false),
        new("SomePartsOrSomeAccessoriesOrSomePapersAreMissing", "Ürün parçası, aksesuarı veya faturası eksik", false),
        new("ReturnedProductIsNotDelivered", "İade ürünü teslim edilmedi", false),
        new("NewProductWillBeSent", "Müşteriye yeni ürün gönderilecek", false),
        new("ExtraProductHasBeenReturned", "Fazla gönderilen ürün iade edildi", false),
        new("ProductNotWrong", "Gönderilen ürün yanlış değil", false),
        new("ProductNotDefective", "Gönderilen ürün kusurlu değil", false),
        new("StockProblem", "Stok sorunu nedeniyle değişim yapılamıyor", false),
        new("ReturnedProductHasAccountOrPassword", "Üründe hesap veya parola bulunuyor", false),
        new("MarkedAsServiceProcess", "Ürün servis veya analiz sürecine alınacak", false),
        new("ProductSentComplete", "Ürün eksiksiz gönderildi", false),
        new("MissingItemOrPartCannotBeSupplied", "Eksik ürün veya parça tedarik edilemiyor", false),
        new("ClaimedComponentIsNotPartOfTheProduct", "Talep edilen parça ürün içeriğine dahil değil", false),
        new("InvoiceReplacesWarranty", "Fatura garanti belgesi yerine geçer", false),
        new("PartialShipmentMissingPackageWillBeDelivered", "Eksik paket kısmi sevkiyatla teslim edilecek", false),
        new("CustomerProblemSolved", "Müşteri sorunu çözüldü", false),
        new("Other", "Diğer", false)
    ];
    private const string OrderGuide = "https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=siparis-entegrasyonu-onemli-bilgiler&product=siparis-olusturma-entegrasyonu&view=guide";
    private const string ListingGuide = "https://developers.hepsiburada.com/tr/companies/hepsiburada?category=baslangic&op=Listing+Bilgilerini+Sorgulama&product=listeleme&version=v1&view=endpoint";
    private const string CategoryEndpointGuide = "https://developers.hepsiburada.com/tr/companies/hepsiburada?category=katalog-urun-entegrasyonu&product=katalog-urun-entegrasyonu&version=v1.0&op=getAllCategoriesByParameters&view=endpoint";

    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<ConnectionIdentity>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantısı veya şifreli kullanıcı bilgileri eksik ya da geçersiz.", HttpStatusCode.Unauthorized);
        var result = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, Orders(account, "offset=0&limit=1"), cancellationToken);
        string? verifiedUsernameToPersist = null;
        if (!result.IsSuccess && string.Equals(result.Error?.Code, "HEPSIBURADA_CREDENTIALS_REJECTED", StringComparison.Ordinal))
        {
            var alternateUsername = HepsiburadaAuthenticationHandler.MerchantIdUsernameFallback(account.Username, account.Connection.ExternalStoreId);
            if (alternateUsername is not null)
            {
                var alternateAccount = account with { Username = alternateUsername };
                var alternateResult = await SendAsync(alternateAccount, alternateAccount.OmsBaseAddress, HttpMethod.Get, Orders(alternateAccount, "offset=0&limit=1"), cancellationToken);
                if (alternateResult.IsSuccess)
                {
                    account = alternateAccount;
                    result = alternateResult;
                    verifiedUsernameToPersist = alternateUsername;
                }
                else if (alternateResult.Error?.HttpStatus == (int)HttpStatusCode.Unauthorized)
                {
                    return AdapterResult<ConnectionIdentity>.Failure(new(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIALS_REJECTED", "Hepsiburada canlı sipariş servisi entegratör kullanıcı adıyla da mağaza ID’siyle de kimlik doğrulamasını reddetti. Aynı aktif entegratör kaydının güncel canlı servis anahtarını ve mağaza yetkisini kontrol edin.", (int)HttpStatusCode.Unauthorized, alternateResult.Error.RetryAfter, alternateResult.Error.RemoteRequestId), alternateResult.RateLimit ?? result.RateLimit);
                }
                else
                {
                    return AdapterResult<ConnectionIdentity>.Failure(alternateResult.Error!, alternateResult.RateLimit);
                }
            }
        }
        if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        try
        {
            HepsiburadaJsonMapper.OrderPage(result.Value!.RootElement);
            if (verifiedUsernameToPersist is not null && !await authentication.SaveBasicUsernameAsync(context.TenantId, context.ConnectionId, verifiedUsernameToPersist, cancellationToken))
                return Failure<ConnectionIdentity>(AdapterErrorClass.Authentication, "HEPSIBURADA_AUTH_USERNAME_SAVE_FAILED", "Hepsiburada mağaza ID’si ile doğrulama başarılı oldu ancak eşzamanlı kimlik bilgisi değişikliği nedeniyle çalışan Basic kullanıcı adı kaydedilemedi. Kimlik bilgilerini yeniden kaydedip tekrar deneyin.", HttpStatusCode.Conflict);
            return AdapterResult<ConnectionIdentity>.Success(new("HEPSIBURADA", account.Connection.Environment, account.Connection.ExternalStoreId, account.Connection.ApiVersion, account.Connection.ExternalStoreId), result.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<ConnectionIdentity>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_CONNECTION_CONTRACT_INVALID", "Hepsiburada bağlantı yanıtı beklenen sipariş sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var connection = await TestAsync(context, cancellationToken);
        if (!connection.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(connection.Error!, connection.RateLimit);
        var identity = connection.Value!;
        var now = timeProvider.GetUtcNow();
        var orders = await PollAsync(context, new OrderPollWindow(null, now), new(null, 1), cancellationToken);
        var products = await ListAsync(context, new(null, 1), new(null), cancellationToken);
        var returnsResult = await PollAsync(context, new ReturnPollWindow(null, null, Status: "AwaitingAction"), new("0", 1), cancellationToken);
        var references = await ReadAsync(context, new("CATEGORIES", null), new(null, 1), cancellationToken);
        IReadOnlyList<CapabilityEvidence> evidence =
        [
            new(MarketplaceCapabilities.ConnectionTest, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, OrderGuide, "v1.0", null, null, "Ödemesi tamamlanmış sipariş endpoint'inden SIT/Canlı salt okunur yanıt alındı.", null, now),
            new(MarketplaceCapabilities.OrderRead, orders.IsSuccess ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, OrderGuide, "v1.0", null, null, orders.IsSuccess ? "Sipariş listeleme ve sipariş detay okuma probe'u başarılı." : "Sipariş okuma probe'u tamamlanamadı; yetenek UNKNOWN bırakıldı.", null, now),
            new(MarketplaceCapabilities.ProductRead, products.IsSuccess ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, ListingGuide, "v1", null, null, products.IsSuccess ? "Satıcı listing bilgisi salt okunur olarak alındı." : "Listing okuma probe'u tamamlanamadı; yetenek UNKNOWN bırakıldı.", null, now),
            new(MarketplaceCapabilities.ReferenceRead, references.IsSuccess ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, CategoryEndpointGuide, "v1.0", null, null, references.IsSuccess ? "Etkin ve ürün açılabilir kategori listesi salt okunur olarak yanıt verdi." : "Kategori referans okuma probe'u tamamlanamadı; yetenek UNKNOWN bırakıldı.", null, now),
            new(MarketplaceCapabilities.ReturnRead, returnsResult.IsSuccess ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, "https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=talep-onemli-bilgiler-2&product=talep-entegrasyonu&view=guide", "v1.0", null, null, returnsResult.IsSuccess ? "Aksiyon bekleyen talep listeleme endpoint'i salt okunur olarak yanıt verdi." : "Talep okuma probe'u tamamlanamadı; yetenek UNKNOWN bırakıldı.", null, now)
        ];
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence, connection.RateLimit ?? orders.RateLimit ?? products.RateLimit ?? returnsResult.RateLimit ?? references.RateLimit);
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (account.CatalogBaseAddress is null) return await Unsupported<AdapterPageResult<RemoteReferenceItem>>("Hepsiburada production katalog base URL'si resmi hesap yapılandırmasında doğrulanana kadar referans okuma kapalıdır.");

        var type = resource.ResourceType.Trim().ToUpperInvariant();
        string endpoint;
        var (pageNumber, limit) = ReferencePage(page, type == "CATEGORIES" ? 2000 : 1000);
        switch (type)
        {
            case "CATEGORIES":
                if (resource.ParentExternalId is not null) return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "HEPSIBURADA_REFERENCE_SCOPE_INVALID", "Kategori listesi parent kapsamı kabul etmez.", HttpStatusCode.BadRequest);
                endpoint = Categories(pageNumber, limit);
                break;
            case "CATEGORY_ATTRIBUTES":
                if (string.IsNullOrWhiteSpace(resource.ParentExternalId) || resource.ParentExternalId.Contains("/", StringComparison.Ordinal))
                    return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "HEPSIBURADA_REFERENCE_SCOPE_INVALID", "Kategori özellikleri tek bir categoryId ister.", HttpStatusCode.BadRequest);
                endpoint = CategoryAttributes(resource.ParentExternalId);
                break;
            case "ATTRIBUTE_VALUES":
                if (!TryReferenceParts(resource.ParentExternalId, out var categoryId, out var attributeId))
                    return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "HEPSIBURADA_REFERENCE_SCOPE_INVALID", "Enum değerleri categoryId/attributeId kapsamı ister.", HttpStatusCode.BadRequest);
                endpoint = AttributeValues(categoryId, attributeId, pageNumber, limit);
                break;
            default:
                return await Unsupported<AdapterPageResult<RemoteReferenceItem>>("Hepsiburada referans türü desteklenmiyor.");
        }

        var response = await SendAsync(account, account.CatalogBaseAddress, HttpMethod.Get, endpoint, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var result = HepsiburadaJsonMapper.References(type, response.Value!.RootElement, resource.ParentExternalId, pageNumber, limit);
            return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(result, response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_REFERENCE_CONTRACT_INVALID", "Hepsiburada referans yanıtı beklenen veri sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var result = await ListCatalogAsync(context, page, filter, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(result.Error!, result.RateLimit);
        var products = result.Value!.Items.SelectMany(product => product.Variants.Select(variant => new RemoteProduct(product.ExternalProductId, variant.ExternalVariantId, variant.Barcode, variant.Sku, variant.RawJson))).ToArray();
        return AdapterResult<AdapterPageResult<RemoteProduct>>.Success(new(products, result.Value.NextCursor, result.Value.HasMore, result.Value.TotalCount, result.Value.Issues), result.RateLimit);
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteProductMatch>>> ListPendingProductMatchesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<AdapterPageResult<RemoteProductMatch>>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar eşleşen ürünler okunmadı.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteProductMatch>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsManualRead(account.Connection)) return await Unsupported<AdapterPageResult<RemoteProductMatch>>("Hepsiburada eşleşme kuyruğu yalnız etkin veya doğrulanmış bağlantıdan okunabilir.");
        if (account.CatalogBaseAddress is null) return await Unsupported<AdapterPageResult<RemoteProductMatch>>("Hepsiburada katalog base URL'si yapılandırılmamış.");
        var (pageNumber, limit) = ReferencePage(page, 100);
        var endpoint = PendingProductMatches(account, pageNumber, limit);
        var response = await SendAsync(account, account.CatalogBaseAddress, HttpMethod.Get, endpoint, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteProductMatch>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var mapped = HepsiburadaJsonMapper.PendingProductMatches(response.Value!.RootElement, pageNumber, limit);
            return AdapterResult<AdapterPageResult<RemoteProductMatch>>.Success(mapped, response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteProductMatch>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PRODUCT_MATCH_CONTRACT_INVALID", "Hepsiburada eşleşen ürün yanıtı beklenen mağaza/SKU sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<bool>> ReviewProductMatchesAsync(AdapterContext context, IReadOnlyList<string> merchantSkus, bool approve, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<bool>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar ürün eşleşmesi kararı gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<bool>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (account.CatalogBaseAddress is null) return await Unsupported<bool>("Hepsiburada katalog base URL'si yapılandırılmamış.");
        if (!IntegrationRuntimePolicy.AllowsExternalWrite(account.Connection, context, GlobalWritesEnabled, ConnectionWritesEnabled(account.Connection.SettingsJson))
            || !await authentication.HasVerifiedWriteEvidenceAsync(account.Connection, cancellationToken, MarketplaceCapabilities.ProductWrite))
            return await Unsupported<bool>("Ürün eşleşmesi kararı yalnız mevcut dış yazma kapıları ve mağaza/ortam kapsamlı PRODUCT_WRITE SIT fixture kanıtı sağlandığında kullanılabilir.");
        if (!ValidMatchDecisionSkus(merchantSkus))
            return Failure<bool>(AdapterErrorClass.Validation, "HEPSIBURADA_PRODUCT_MATCH_SKUS_INVALID", "Eşleşme kararı için 1-100 benzersiz, boşluksuz büyük harf merchantSku gerekir.", HttpStatusCode.BadRequest);

        var body = ProductMatchDecisionPayload(account.Connection.ExternalStoreId, merchantSkus);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var endpoint = ProductMatchDecision(approve);
        var response = await SendAsync(account, account.CatalogBaseAddress, HttpMethod.Post, endpoint, content, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<bool>.Failure(response.Error!, response.RateLimit);
        try
        {
            if (!HepsiburadaJsonMapper.ProductMatchDecisionAccepted(response.Value!.RootElement))
                return Failure<bool>(AdapterErrorClass.Validation, "HEPSIBURADA_PRODUCT_MATCH_DECISION_REJECTED", "Hepsiburada eşleşme kararı yanıtı başarısızlık bildirdi.", HttpStatusCode.BadGateway);
            return AdapterResult<bool>.Success(true, response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<bool>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PRODUCT_MATCH_DECISION_CONTRACT_INVALID", "Hepsiburada eşleşme kararı yanıtı beklenen sözleşmeyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        var (offset, limit) = Page(page, settings.PageSize);
        var productLookup = !string.IsNullOrWhiteSpace(filter.ProductMainId) ? filter.ProductMainId : filter.Barcode;
        var lookupQueries = ListingLookupQueries(productLookup);
        var attempts = lookupQueries.Count == 0 ? new string?[] { null } : lookupQueries.Cast<string?>().ToArray();
        for (var attemptIndex = 0; attemptIndex < attempts.Length; attemptIndex++)
        {
            var query = new List<string> { $"offset={offset.ToString(CultureInfo.InvariantCulture)}", $"limit={limit.ToString(CultureInfo.InvariantCulture)}" };
            if (filter.ModifiedAfter is { } modifiedAfter) query.Add("updateStartDate=" + Uri.EscapeDataString(modifiedAfter.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            if (attempts[attemptIndex] is { } lookupQuery) query.Add(lookupQuery);
            var response = await SendAsync(account, account.ListingBaseAddress, HttpMethod.Get, Listings(account, string.Join('&', query)), cancellationToken);
            if (!response.IsSuccess)
            {
                if (ShouldTryNextListingLookup(attemptIndex, attempts.Length, page.Cursor, 0, response.Error?.Class)) continue;
                return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(response.Error!, response.RateLimit);
            }
            try
            {
                var pageResult = HepsiburadaJsonMapper.ListingPage(response.Value!.RootElement);
                var items = pageResult.Items.Select(HepsiburadaJsonMapper.CatalogProduct).ToArray();
                if (!string.IsNullOrWhiteSpace(productLookup))
                    items = items.Where(item => ListingProductMatchesLookup(item, productLookup)).ToArray();
                var nextOffset = offset + pageResult.Items.Count;
                var hasMore = pageResult.TotalCount is { } total ? nextOffset < total : pageResult.Items.Count == limit;
                var mapped = new AdapterPageResult<RemoteCatalogProduct>(items, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, pageResult.TotalCount);

                // Order lines can expose either hbSku or merchantSku. Try the
                // documented productId, hbSkuList, and merchantSkuList filters,
                // returning only a listing that exactly matches the requested key.
                if (ShouldTryNextListingLookup(attemptIndex, attempts.Length, page.Cursor, items.Length, null)) continue;
                return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(mapped, response.RateLimit);
            }
            catch (JsonException)
            {
                return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_LISTING_CONTRACT_INVALID", "Hepsiburada listing yanıtı beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
            }
        }

        return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new([], null, false, 0), null);
    }

    internal static IReadOnlyList<string> ListingLookupQueries(string? productLookup)
    {
        var value = productLookup?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return [];
        var escaped = Uri.EscapeDataString(value);
        return [$"productId={escaped}", $"hbSkuList={escaped}", $"merchantSkuList={escaped}"];
    }

    internal static bool ListingProductMatchesLookup(RemoteCatalogProduct product, string lookup)
    {
        var normalized = lookup.Trim();
        return normalized.Length > 0
            && (string.Equals(product.ExternalProductId, normalized, StringComparison.OrdinalIgnoreCase)
                || product.Variants.Any(variant =>
                    string.Equals(variant.ExternalVariantId, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(variant.Sku, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(variant.Barcode, normalized, StringComparison.OrdinalIgnoreCase)));
    }

    internal static bool ShouldTryNextListingLookup(int attemptIndex, int attemptCount, string? cursor, int itemCount, AdapterErrorClass? errorClass) =>
        attemptIndex + 1 < attemptCount
        && cursor is null
        && (errorClass == AdapterErrorClass.NotFound || errorClass is null && itemCount == 0);

    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<RemoteOperationRef>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar ürün dosyası gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<RemoteOperationRef>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (account.CatalogBaseAddress is null) return await Unsupported<RemoteOperationRef>("Hepsiburada ürün import için doğrulanmış katalog base URL'si yok.");
        if (!IntegrationRuntimePolicy.AllowsExternalWrite(account.Connection, context, GlobalWritesEnabled, ConnectionWritesEnabled(account.Connection.SettingsJson))
            || !await authentication.HasVerifiedWriteEvidenceAsync(account.Connection, cancellationToken, MarketplaceCapabilities.ProductWrite, MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite))
            return await Unsupported<RemoteOperationRef>("Hepsiburada ürün import yalnız write kapıları ve PRODUCT_WRITE/PRICE_WRITE/INVENTORY_WRITE SIT fixture kanıtları açıldıktan sonra kullanılabilir.");
        if (publication.ProductId == Guid.Empty || string.IsNullOrWhiteSpace(publication.PayloadHash) || !ValidProductImportPayload(publication.PayloadJson, account.Connection.ExternalStoreId))
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "HEPSIBURADA_PRODUCT_IMPORT_PAYLOAD_INVALID", "Hepsiburada ürün import dosyası beklenen en fazla 1000 satırlık JSON sözleşmesini sağlamıyor.", HttpStatusCode.BadRequest);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(publication.PayloadJson));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "products.json");
        var response = await SendAsync(account, account.CatalogBaseAddress, HttpMethod.Post, ProductImportUpload(), content, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(response.Error!, response.RateLimit);
        try
        {
            var trackingId = HepsiburadaJsonMapper.ProductImportTrackingId(response.Value!.RootElement);
            return AdapterResult<RemoteOperationRef>.Success(new(trackingId, "PRODUCT_IMPORT", timeProvider.GetUtcNow()), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PRODUCT_IMPORT_CONTRACT_INVALID", "Hepsiburada ürün import yanıtında trackingId bulunamadı.", HttpStatusCode.BadGateway);
        }
    }

    private async Task<AdapterResult<RemoteOperationRef>> SubmitProductUpdateAsync(AdapterContext context, ProductUpdatePublication publication, string payloadJson, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<RemoteOperationRef>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar ürün güncelleme dosyası gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<RemoteOperationRef>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (account.CatalogBaseAddress is null) return await Unsupported<RemoteOperationRef>("Hepsiburada ürün güncelleme için doğrulanmış katalog base URL'si yok.");
        if (!IntegrationRuntimePolicy.AllowsExternalWrite(account.Connection, context, GlobalWritesEnabled, ConnectionWritesEnabled(account.Connection.SettingsJson))
            || !await authentication.HasVerifiedWriteEvidenceAsync(account.Connection, cancellationToken, MarketplaceCapabilities.ProductWrite))
            return await Unsupported<RemoteOperationRef>("Hepsiburada ürün güncellemesi yalnız dış yazma kapıları ve mağaza/ortam kapsamlı PRODUCT_WRITE SIT fixture kanıtı sağlandığında kullanılabilir.");
        if (publication.ProductId == Guid.Empty || string.IsNullOrWhiteSpace(publication.PayloadHash) || !ValidProductUpdatePayload(payloadJson, account.Connection.ExternalStoreId))
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "HEPSIBURADA_PRODUCT_UPDATE_PAYLOAD_INVALID", "Hepsiburada ürün güncelleme dosyası merchantId/hbSku/merchantSku sözleşmesini sağlamıyor.", HttpStatusCode.BadRequest);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(payloadJson));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "product-updates.json");
        var response = await SendAsync(account, account.CatalogBaseAddress, HttpMethod.Post, ProductUpdateUpload(), content, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(response.Error!, response.RateLimit);
        try
        {
            var trackingId = HepsiburadaJsonMapper.ProductImportTrackingId(response.Value!.RootElement);
            return AdapterResult<RemoteOperationRef>.Success(new($"PRODUCT_UPDATE:{trackingId}", "PRODUCT_UPDATE_IMPORT", timeProvider.GetUtcNow()), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PRODUCT_UPDATE_CONTRACT_INVALID", "Hepsiburada ürün güncelleme yanıtında trackingId bulunamadı.", HttpStatusCode.BadGateway);
        }
    }

    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        publication is null ? Unsupported<RemoteOperationRef>("Hepsiburada ürün güncelleme içeriği zorunludur.") : SubmitProductUpdateAsync(context, publication, publication.UnapprovedPayloadJson, cancellationToken);
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        publication is null ? Unsupported<RemoteOperationRef>("Hepsiburada ürün güncelleme içeriği zorunludur.") : SubmitProductUpdateAsync(context, publication, publication.ApprovedContentPayloadJson, cancellationToken);
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        Unsupported<RemoteOperationRef>("Hepsiburada ürün yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        Unsupported<RemoteOperationRef>("Hepsiburada ürün yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");
    public async Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<RemoteOperationStatus>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar trackingId sorgulanmadı.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<RemoteOperationStatus>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsManualRead(account.Connection)) return await Unsupported<RemoteOperationStatus>("Hepsiburada import durumu yalnız etkin veya doğrulanmış bağlantıda okunabilir.");
        if (string.IsNullOrWhiteSpace(externalOperationId) || externalOperationId.Length > 240)
            return Failure<RemoteOperationStatus>(AdapterErrorClass.Validation, "HEPSIBURADA_TRACKING_ID_INVALID", "Hepsiburada trackingId geçersiz.", HttpStatusCode.BadRequest);

        var isProductUpdate = externalOperationId.StartsWith("PRODUCT_UPDATE:", StringComparison.Ordinal);
        var isInventoryUpload = externalOperationId.StartsWith("LISTING_INVENTORY:", StringComparison.Ordinal);
        var operationId = isProductUpdate ? externalOperationId["PRODUCT_UPDATE:".Length..]
            : isInventoryUpload ? externalOperationId["LISTING_INVENTORY:".Length..]
            : externalOperationId;
        if (!isInventoryUpload && account.CatalogBaseAddress is null) return await Unsupported<RemoteOperationStatus>("Hepsiburada ürün import status base URL'si yapılandırılmamış.");
        if (isInventoryUpload)
        {
            if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 200)
                return Failure<RemoteOperationStatus>(AdapterErrorClass.Validation, "HEPSIBURADA_INVENTORY_UPLOAD_ID_INVALID", "Hepsiburada inventoryUploadId geçersiz.", HttpStatusCode.BadRequest);
            var statusResponse = await SendAsync(account, account.ListingBaseAddress, HttpMethod.Get, InventoryUploadStatus(account, operationId), cancellationToken);
            if (!statusResponse.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(statusResponse.Error!, statusResponse.RateLimit);
            try { return AdapterResult<RemoteOperationStatus>.Success(HepsiburadaJsonMapper.InventoryUploadStatus(statusResponse.Value!.RootElement, operationId), statusResponse.RateLimit); }
            catch (JsonException)
            {
                return Failure<RemoteOperationStatus>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_INVENTORY_STATUS_CONTRACT_INVALID", "Hepsiburada listing güncelleme durumu beklenen sözleşmeyle eşleşmiyor.", HttpStatusCode.BadGateway);
            }
        }
        var trackingId = operationId;
        if (string.IsNullOrWhiteSpace(trackingId) || trackingId.Length > 200)
            return Failure<RemoteOperationStatus>(AdapterErrorClass.Validation, "HEPSIBURADA_TRACKING_ID_INVALID", "Hepsiburada trackingId geçersiz.", HttpStatusCode.BadRequest);

        var allLines = new List<RemoteOperationLine>();
        var operationStatus = "IN_PROGRESS";
        RateLimitMetadata? rate = null;
        int? totalCount = null;
        for (var page = 0; page < 10; page++)
        {
            var statusEndpoint = isProductUpdate ? ProductUpdateStatus(trackingId, page, 100) : ProductImportStatus(trackingId, page, 100);
            var response = await SendAsync(account, account.CatalogBaseAddress!, HttpMethod.Get, statusEndpoint, cancellationToken);
            if (!response.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(response.Error!, response.RateLimit);
            rate = response.RateLimit ?? rate;
            try
            {
                var mapped = HepsiburadaJsonMapper.ProductImportStatus(response.Value!.RootElement, trackingId);
                operationStatus = mapped.Status;
                if (operationStatus == "IN_PROGRESS") return AdapterResult<RemoteOperationStatus>.Success(mapped, rate);
                allLines.AddRange(mapped.Lines);
                totalCount = HepsiburadaJsonMapper.ProductImportTotalCount(response.Value.RootElement) ?? totalCount;
                if (totalCount is { } expected && allLines.Count >= expected) break;
                if (mapped.Lines.Count < 100) break;
            }
            catch (JsonException)
            {
                return Failure<RemoteOperationStatus>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID", "Hepsiburada ürün import durum yanıtı beklenen sözleşmeyle eşleşmiyor.", HttpStatusCode.BadGateway);
            }
        }
        if (totalCount is { } expectedCount && allLines.Count != expectedCount)
            return Failure<RemoteOperationStatus>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PRODUCT_STATUS_INCOMPLETE", "Hepsiburada trackingId sayfalaması tüm ürün satırlarını döndürmedi.", HttpStatusCode.BadGateway);
        if (allLines.Count > 1000 || allLines.GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return Failure<RemoteOperationStatus>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PRODUCT_STATUS_DUPLICATE", "Hepsiburada ürün durum yanıtında satır sayısı sınırı aşıldı veya merchantSku yinelendi.", HttpStatusCode.BadGateway);
        return AdapterResult<RemoteOperationStatus>.Success(new(externalOperationId, operationStatus, allLines), rate);
    }
    public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Unsupported<RemotePublicationStatus>("Hepsiburada ürün durum okuması bu aşamada desteklenmiyor.");
    public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Hepsiburada ürün yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");
    public async Task<AdapterResult<RemoteProduct?>> FindByBarcodeAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        var page = await ListAsync(context, new(null, Math.Clamp(settings.PageSize, 1, 10)), new(null, Barcode: barcode), cancellationToken);
        if (!page.IsSuccess) return AdapterResult<RemoteProduct?>.Failure(page.Error!, page.RateLimit);
        var lookup = barcode.Trim();
        var product = page.Value!.Items.FirstOrDefault(item =>
            string.Equals(item.Barcode, lookup, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Sku, lookup, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.ExternalVariantId, lookup, StringComparison.OrdinalIgnoreCase));
        return AdapterResult<RemoteProduct?>.Success(product, page.RateLimit);
    }
    public async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<RemoteOperationRef>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar fiyat/stok yüklemesi gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<RemoteOperationRef>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsExternalWrite(account.Connection, context, GlobalWritesEnabled, ConnectionWritesEnabled(account.Connection.SettingsJson))
            || !await authentication.HasVerifiedWriteEvidenceAsync(account.Connection, cancellationToken, MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite))
            return await Unsupported<RemoteOperationRef>("Hepsiburada fiyat/stok yüklemesi yalnız dış yazma kapıları ve mağaza/ortam kapsamlı PRICE_WRITE/INVENTORY_WRITE SIT kanıtları sağlandığında kullanılabilir.");
        if (!ValidPriceInventoryPayload(payloadJson, account.Connection.ExternalStoreId))
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "HEPSIBURADA_PRICE_INVENTORY_PAYLOAD_INVALID", "Hepsiburada fiyat/stok yüklemesi merchantId, hbSku, merchantSku, fiyat ve adet sözleşmesini sağlamıyor.", HttpStatusCode.BadRequest);

        using var content = new StringContent(BuildInventoryUploadXml(payloadJson), Encoding.UTF8, "application/xml");
        var response = await SendAsync(account, account.ListingBaseAddress, HttpMethod.Post, InventoryUpload(account), content, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(response.Error!, response.RateLimit);
        try
        {
            var uploadId = HepsiburadaJsonMapper.InventoryUploadId(response.Value!.RootElement);
            return AdapterResult<RemoteOperationRef>.Success(new($"LISTING_INVENTORY:{uploadId}", "LISTING_INVENTORY_UPLOAD", timeProvider.GetUtcNow()), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_INVENTORY_UPLOAD_CONTRACT_INVALID", "Hepsiburada listing yanıtında inventoryUploadId bulunamadı.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (window.ModifiedAfter is { } start && window.ModifiedBefore is { } end && end - start > TimeSpan.FromHours(24))
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.Validation, "HEPSIBURADA_ORDER_WINDOW_TOO_LARGE", "Hepsiburada sipariş endpoint'i en fazla 24 saatlik tarih aralığı kabul eder.", HttpStatusCode.BadRequest);
        var (offset, limit) = Page(page, settings.PageSize);
        var query = new List<string> { $"offset={offset.ToString(CultureInfo.InvariantCulture)}", $"limit={limit.ToString(CultureInfo.InvariantCulture)}" };
        if (window.ModifiedAfter is { } after) query.Add("begindate=" + Uri.EscapeDataString(after.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        if (window.ModifiedBefore is { } before) query.Add("enddate=" + Uri.EscapeDataString(before.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, Orders(account, string.Join('&', query)), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var pageResult = HepsiburadaJsonMapper.OrderPage(response.Value!.RootElement);
            // The paid-order list contains the line, customer, address, price,
            // and due-date fields needed by the panel. Use it directly; the
            // detail endpoint is reserved for lifecycle checks on existing orders.
            var mapped = pageResult.Items.Select(HepsiburadaJsonMapper.PaidOrderLine).ToArray();
            var nextOffset = offset + pageResult.Items.Count;
            var hasMore = pageResult.TotalCount is { } total ? nextOffset < total : pageResult.Items.Count == limit;
            return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(new(mapped, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, pageResult.TotalCount), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_ORDER_LIST_CONTRACT_INVALID", "Hepsiburada sipariş listesi beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<RemoteOrder>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        var result = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, OrderDetails(account, externalOrderId), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteOrder>.Failure(result.Error!, result.RateLimit);
        try
        {
            var order = HepsiburadaJsonMapper.Order(result.Value!.RootElement, externalOrderId);
            var packages = order.Packages.ToArray();
            for (var index = 0; index < packages.Length; index++)
            {
                var readback = await ReadPackageTrackingInfoAsync(
                    account,
                    new RemoteOrderPackage(order.ExternalOrderId, packages[index]),
                    cancellationToken);
                packages[index] = readback.Package.Package;
            }
            return AdapterResult<RemoteOrder>.Success(order with { Packages = packages }, result.RateLimit);
        }
        catch (JsonException) { return Failure<RemoteOrder>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_ORDER_CONTRACT_INVALID", "Hepsiburada sipariş detay yanıtı beklenen sözleşmeyle eşleşmiyor.", HttpStatusCode.BadGateway); }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteOrderPackage>>> PollPackagesAsync(AdapterContext context, PackagePollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteOrderPackage>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (window.ModifiedAfter is { } start && window.ModifiedBefore is { } end && (end < start || end - start > TimeSpan.FromHours(24)))
            return Failure<AdapterPageResult<RemoteOrderPackage>>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_WINDOW_INVALID", "Hepsiburada paket listelemesi artan ve en fazla 24 saatlik tarih aralığı kabul eder.", HttpStatusCode.BadRequest);

        var (offset, limit) = Page(page, settings.PageSize);
        try
        {
            var query = PackageListQuery(offset, limit, window);
            var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, Packages(account, query), cancellationToken);
            if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrderPackage>>.Failure(response.Error!, response.RateLimit);

            var sources = new List<(string? Status, IReadOnlyList<JsonElement> Items, int? TotalCount)>();
            var openPage = HepsiburadaJsonMapper.PackagePage(response.Value!.RootElement);
            sources.Add((null, openPage.Items, openPage.TotalCount));
            var rateLimit = response.RateLimit;

            // The generic package list only returns Open packages. Hepsiburada
            // exposes shipped/delivered/undelivered packages through separate
            // read-only endpoints, each with a 30-day lookback and max limit 50.
            foreach (var status in new[] { "shipped", "delivered", "undelivered" })
            {
                var statusResponse = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, PackagesByStatus(account, status, query), cancellationToken);
                if (!statusResponse.IsSuccess)
                    return AdapterResult<AdapterPageResult<RemoteOrderPackage>>.Failure(statusResponse.Error!, statusResponse.RateLimit ?? rateLimit);
                var statusPage = HepsiburadaJsonMapper.PackagePage(statusResponse.Value!.RootElement);
                sources.Add((status, statusPage.Items, statusPage.TotalCount));
                rateLimit = statusResponse.RateLimit ?? rateLimit;
            }

            var rawItems = new List<RemoteOrderPackage>();
            var issues = new List<AdapterPageIssue>();
            foreach (var source in sources)
            {
                foreach (var item in source.Items)
                {
                    try
                    {
                        rawItems.Add(source.Status is null
                            ? HepsiburadaJsonMapper.OrderPackage(item)
                            : HepsiburadaJsonMapper.OrderStatusPackage(item, source.Status));
                    }
                    catch (JsonException)
                    {
                        var identity = HepsiburadaJsonMapper.PackageIdentity(item) ?? $"offset:{offset + rawItems.Count}";
                        issues.Add(new("HEPSIBURADA_PACKAGE_ORDER_LINK_MISSING", identity, "Hepsiburada paket/durum kaydında sipariş, paket kimliği veya olay tarihi yok; kayıt siparişe bağlanmadı."));
                    }
                }
            }

            var deduplicated = rawItems
                .GroupBy(item => (item.ExternalOrderId, item.Package.ExternalPackageId))
                .Select(MergePackageStatusObservation)
                .OrderBy(item => item.ExternalOrderId, StringComparer.Ordinal)
                .ThenBy(item => item.Package.ExternalPackageId, StringComparer.Ordinal)
                .ToList();
            var items = new List<RemoteOrderPackage>(deduplicated.Count);
            foreach (var item in deduplicated)
            {
                var trackingRead = await ReadPackageTrackingInfoAsync(account, item, cancellationToken);
                items.Add(trackingRead.Package);
                if (trackingRead.Issue is not null) issues.Add(trackingRead.Issue);
            }

            var nextOffset = offset + limit;
            var hasMore = sources.Any(source => source.TotalCount is { } total
                ? nextOffset < total
                : source.Items.Count == limit);
            var totalCount = sources.Where(source => source.TotalCount is not null).Select(source => source.TotalCount!.Value).DefaultIfEmpty().Max();
            return AdapterResult<AdapterPageResult<RemoteOrderPackage>>.Success(new(items, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, totalCount == 0 ? null : totalCount, issues), rateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteOrderPackage>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PACKAGE_LIST_CONTRACT_INVALID", "Hepsiburada paket veya sevkiyat durumu listesi beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<PackageTrackingStatusSnapshot>> GetPackageTrackingInfoAsync(AdapterContext context, string packageNumber, CancellationToken cancellationToken)
    {
        var normalizedPackageNumber = packageNumber.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPackageNumber))
            return Failure<PackageTrackingStatusSnapshot>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_NUMBER_REQUIRED", "Hepsiburada paket numarası gerekli.", HttpStatusCode.BadRequest);

        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null)
            return Failure<PackageTrackingStatusSnapshot>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);

        return await GetPackageTrackingInfoAsync(account, normalizedPackageNumber, cancellationToken);
    }

    internal async Task<AdapterResult<PackageTrackingStatusSnapshot>> GetPackageTrackingInfoAsync(HepsiburadaRequestContext account, string packageNumber, CancellationToken cancellationToken)
    {
        var normalizedPackageNumber = packageNumber.Trim();
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get,
            PackageTrackingInfo(account.Connection.ExternalStoreId, normalizedPackageNumber), cancellationToken);
        if (!response.IsSuccess)
            return AdapterResult<PackageTrackingStatusSnapshot>.Failure(response.Error!, response.RateLimit);

        try
        {
            var tracking = HepsiburadaJsonMapper.PackageTrackingInfo(response.Value!.RootElement, normalizedPackageNumber);
            return AdapterResult<PackageTrackingStatusSnapshot>.Success(
                new(normalizedPackageNumber, tracking.Status, tracking.CargoCompany, tracking.TrackingInfoCode, tracking.OrderNumber), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<PackageTrackingStatusSnapshot>(AdapterErrorClass.ContractViolation,
                "HEPSIBURADA_PACKAGE_TRACKING_INFO_INVALID",
                "Hepsiburada kargo yanıtı istenen paket numarası ve durum bilgileriyle eşleşmedi.", HttpStatusCode.BadGateway);
        }
    }

    private static string PackageListQuery(int offset, int limit, PackagePollWindow window)
    {
        var query = new List<string>
        {
            $"offset={offset.ToString(CultureInfo.InvariantCulture)}",
            $"limit={limit.ToString(CultureInfo.InvariantCulture)}"
        };
        if (window.ModifiedAfter is { } after) query.Add("begindate=" + Uri.EscapeDataString(after.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        if (window.ModifiedBefore is { } before) query.Add("enddate=" + Uri.EscapeDataString(before.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        return string.Join('&', query);
    }

    private static RemoteOrderPackage MergePackageStatusObservation(IGrouping<(string ExternalOrderId, string ExternalPackageId), RemoteOrderPackage> group)
    {
        var latest = group.OrderByDescending(item => item.Package.OccurredAt).First();
        var detailed = group.FirstOrDefault(item => !item.Package.IsStatusObservation || item.Package.Allocations.Count > 0);
        if (detailed is null) return latest;

        var updatedPackage = detailed.Package with
        {
            RawStatus = latest.Package.OccurredAt >= detailed.Package.OccurredAt ? latest.Package.RawStatus : detailed.Package.RawStatus,
            OccurredAt = latest.Package.OccurredAt > detailed.Package.OccurredAt ? latest.Package.OccurredAt : detailed.Package.OccurredAt,
            CargoProviderExternalId = latest.Package.CargoProviderExternalId ?? detailed.Package.CargoProviderExternalId,
            CargoTrackingNumber = latest.Package.CargoTrackingNumber ?? detailed.Package.CargoTrackingNumber,
            Invoice = latest.Package.Invoice ?? detailed.Package.Invoice,
            IsStatusObservation = false
        };
        var order = detailed.OrderSnapshot is null
            ? null
            : detailed.OrderSnapshot with { Packages = detailed.OrderSnapshot.Packages.Select(package => package.ExternalPackageId == updatedPackage.ExternalPackageId ? updatedPackage : package).ToArray() };
        return detailed with { Package = updatedPackage, OrderSnapshot = order };
    }

    internal async Task<(RemoteOrderPackage Package, AdapterPageIssue? Issue)> ReadPackageTrackingInfoAsync(
        HepsiburadaRequestContext account,
        RemoteOrderPackage remotePackage,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, PackageTrackingInfo(account.Connection.ExternalStoreId, remotePackage.Package.ExternalPackageId), cancellationToken);
        if (!response.IsSuccess)
        {
            var issue = response.Error?.HttpStatus == (int)HttpStatusCode.NotFound
                ? null
                : new AdapterPageIssue("HEPSIBURADA_PACKAGE_TRACKING_INFO_UNAVAILABLE", remotePackage.Package.ExternalPackageId, "Paket kargo takip bilgisi alınamadı; paket listesi bilgisi korundu.");
            return (remotePackage, issue);
        }

        using var trackingDocument = response.Value!;
        try
        {
            var tracking = HepsiburadaJsonMapper.PackageTrackingInfo(trackingDocument.RootElement, remotePackage.Package.ExternalPackageId);
            var trackingStatus = string.Equals(tracking.Status, "Delivered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tracking.Status, "Undelivered", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(remotePackage.Package.RawStatus, "Delivered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tracking.Status, "InTransit", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(remotePackage.Package.RawStatus, "Undelivered", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(remotePackage.Package.RawStatus, "Delivered", StringComparison.OrdinalIgnoreCase)
                ? tracking.Status!
                : remotePackage.Package.RawStatus;
            var enrichedPackage = remotePackage.Package with
            {
                RawStatus = trackingStatus,
                CargoProviderExternalId = string.IsNullOrWhiteSpace(tracking.CargoCompany) ? remotePackage.Package.CargoProviderExternalId : tracking.CargoCompany,
                CargoTrackingNumber = string.IsNullOrWhiteSpace(tracking.TrackingInfoCode) ? remotePackage.Package.CargoTrackingNumber : tracking.TrackingInfoCode
            };
            var orderSnapshot = remotePackage.OrderSnapshot is null
                ? null
                : remotePackage.OrderSnapshot with
                {
                    Packages = remotePackage.OrderSnapshot.Packages
                        .Select(package => package.ExternalPackageId == enrichedPackage.ExternalPackageId ? enrichedPackage : package)
                        .ToArray()
                };
            return (remotePackage with { Package = enrichedPackage, OrderSnapshot = orderSnapshot }, null);
        }
        catch (JsonException)
        {
            return (remotePackage, new AdapterPageIssue("HEPSIBURADA_PACKAGE_TRACKING_INFO_INVALID", remotePackage.Package.ExternalPackageId, "Paket kargo yanıtı beklenen packageNumber alanıyla eşleşmedi; paket listesi bilgisi korundu."));
        }
    }

    public async Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string sku, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<StageTestOrderResult>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar test siparişi gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<StageTestOrderResult>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!context.IsStageCapabilityProbe || !string.Equals(account.Connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)
            || !IntegrationRuntimePolicy.AllowsExternalWrite(account.Connection, context, GlobalWritesEnabled, ConnectionWritesEnabled(account.Connection.SettingsJson)))
            return AdapterResult<StageTestOrderResult>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_TEST_ORDER_STAGE_ONLY", "Hepsiburada test siparişi yalnız kullanıcı tarafından başlatılan Stage capability fixture'ında kullanılabilir.", null, null, null));
        if (account.StageTestOrderBaseAddress is null || !ValidTestOrderSku(sku))
            return Failure<StageTestOrderResult>(AdapterErrorClass.Validation, "HEPSIBURADA_TEST_ORDER_INPUT_INVALID", "Stage test siparişi için doğrulanmış HBSKU ve test ortamı adresi gerekir.", HttpStatusCode.BadRequest);

        var orderNumber = RandomNumberGenerator.GetInt32(1_000_000_000, int.MaxValue).ToString(CultureInfo.InvariantCulture);
        using var content = new StringContent(TestOrderPayload(account.Connection.ExternalStoreId, orderNumber, sku, timeProvider.GetUtcNow()), Encoding.UTF8, "application/json");
        var response = await SendAsync(account, account.StageTestOrderBaseAddress, HttpMethod.Post, StageTestOrder(account, account.Connection.ExternalStoreId), content, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<StageTestOrderResult>.Failure(response.Error!, response.RateLimit);
        var responseOrderNumber = Text(response.Value!.RootElement, "orderNumber", "OrderNumber");
        return string.IsNullOrWhiteSpace(responseOrderNumber)
            ? Failure<StageTestOrderResult>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_TEST_ORDER_CONTRACT_INVALID", "Hepsiburada test siparişi yanıtında orderNumber bulunamadı.", HttpStatusCode.BadGateway)
            : AdapterResult<StageTestOrderResult>.Success(new(responseOrderNumber), response.RateLimit);
    }
    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!TryClaimStatus(window.Status, out var status))
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_STATUS_INVALID", "Talep listelemesi için belgelenmiş bir Hepsiburada talep durumu zorunludur.", HttpStatusCode.BadRequest);
        if ((window.ModifiedAfter is null) != (window.ModifiedBefore is null)
            || window.ModifiedAfter is { } start && window.ModifiedBefore is { } end && end < start
            || (window.StatusModifiedAfter is null) != (window.StatusModifiedBefore is null)
            || window.StatusModifiedAfter is { } statusStart && window.StatusModifiedBefore is { } statusEnd && statusEnd < statusStart)
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_WINDOW_INVALID", "Talep tarih filtresi başlangıç ve bitiş tarihlerini birlikte ve artan sırada gerektirir.", HttpStatusCode.BadRequest);

        var offset = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
        var limit = Math.Clamp(page.Limit, 1, 100);
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, Claims(account, status, ClaimQuery(
            offset,
            limit,
            window.ModifiedAfter,
            window.ModifiedBefore,
            window.StatusModifiedAfter,
            window.StatusModifiedBefore)), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var pageResult = HepsiburadaJsonMapper.ClaimPage(response.Value!.RootElement);
            var claims = new List<RemoteReturnClaim>(pageResult.Items.Count);
            var issues = new List<AdapterPageIssue>();
            for (var index = 0; index < pageResult.Items.Count; index++)
            {
                var item = pageResult.Items[index];
                try { claims.Add(HepsiburadaJsonMapper.ReturnClaim(item)); }
                catch (JsonException)
                {
                    var identity = HepsiburadaJsonMapper.ReturnClaimIdentity(item) ?? $"offset:{offset + index}";
                    issues.Add(new("HEPSIBURADA_CLAIM_CONTRACT_INVALID", identity, "Talep kaydında güvenli order ve kalem bağlantısı için gerekli alanlar eksik; kayıt atlandı."));
                }
            }

            var nextOffset = offset + pageResult.Items.Count;
            var hasMore = pageResult.TotalCount is { } total ? nextOffset < total : pageResult.Items.Count == limit;
            return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new(claims, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, pageResult.TotalCount, issues), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_CLAIM_LIST_CONTRACT_INVALID", "Hepsiburada talep listesi beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }
    async Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => await Unsupported<RemoteReturnClaim>("Hepsiburada tekil talep detayı için belgeli bir GET endpoint'i sunmuyor; durum taramasıyla güncellenmeye devam edecek.");
    public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success(ClaimRejectionReasons));
    public async Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<ReturnActionResult>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar talep aksiyonu gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<ReturnActionResult>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsExternalWrite(account.Connection, context, GlobalWritesEnabled, ConnectionWritesEnabled(account.Connection.SettingsJson)))
            return await Unsupported<ReturnActionResult>("Hepsiburada talep aksiyonu yalnız doğrulanmış Stage bağlantısında veya dış yazma kapıları açılmış canlı bağlantıda kullanılabilir.");
        if (string.IsNullOrWhiteSpace(command.ExternalClaimId))
            return Failure<ReturnActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_NUMBER_REQUIRED", "Talep numarası zorunludur.", HttpStatusCode.BadRequest);

        var action = command.Action.Trim().ToUpperInvariant();
        string path;
        object body;
        string resultStatus;
        if (action == "APPROVE")
        {
            var finalizedWith = command.FinalizedWith?.Trim();
            if (finalizedWith is not ("Refund" or "Change"))
                return Failure<ReturnActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_FINALIZED_WITH_REQUIRED", "Talep kabulünde iade veya ürün değişimi seçilmelidir.", HttpStatusCode.BadRequest);
            path = AcceptClaim(account, command.ExternalClaimId);
            body = new { FinalizedWith = finalizedWith };
            resultStatus = "ACCEPTED";
        }
        else if (action == "PREAPPROVAL_CONFIRM")
        {
            path = ConfirmClaimPreApproval(account, command.ExternalClaimId);
            body = new { preApprovalReason = "ProductInvestigation" };
            resultStatus = "PREAPPROVAL_CONFIRM_SUBMITTED";
        }
        else if (action == "REJECT")
        {
            if (string.IsNullOrWhiteSpace(command.ReasonCode) || !ClaimRejectionReasons.Any(reason => reason.Id == command.ReasonCode)
                || string.IsNullOrWhiteSpace(command.Explanation) || command.Explanation.Trim().Length > 500)
                return Failure<ReturnActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_REJECTION_INVALID", "Talep reddinde belgelenmiş ret nedeni ve en fazla 500 karakterlik açıklama zorunludur.", HttpStatusCode.BadRequest);
            path = RejectClaim(account, command.ExternalClaimId);
            body = new { ClaimRejectionReason = command.ReasonCode, MerchantStatement = command.Explanation.Trim() };
            resultStatus = "REJECTED";
        }
        else
            return Failure<ReturnActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_ACTION_INVALID", "Talep aksiyonu APPROVE, PREAPPROVAL_CONFIRM veya REJECT olmalıdır.", HttpStatusCode.BadRequest);

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Post, path, content, cancellationToken);
        return response.IsSuccess
            ? AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, resultStatus, null), response.RateLimit)
            : AdapterResult<ReturnActionResult>.Failure(response.Error!, response.RateLimit);
    }
    public async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<InvoiceDeliveryResult>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar fatura bağlantısı gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<InvoiceDeliveryResult>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsExternalWrite(account.Connection, context, GlobalWritesEnabled, ConnectionWritesEnabled(account.Connection.SettingsJson)))
            return await Unsupported<InvoiceDeliveryResult>("Hepsiburada fatura teslimi yalnız doğrulanmış Stage bağlantısında veya dış yazma kapıları açılmış canlı bağlantıda kullanılabilir.");
        if (!HepsiburadaInvoiceDeliveryPolicy.TryCreate(command, out var invoice, out var validationError))
            return Failure<InvoiceDeliveryResult>(AdapterErrorClass.Validation, "HEPSIBURADA_INVOICE_DELIVERY_INVALID", validationError, HttpStatusCode.BadRequest);

        var body = JsonSerializer.Serialize(new
        {
            arrangementDate = invoice!.ArrangementDate,
            invoiceLink = invoice.InvoiceLink.AbsoluteUri,
            invoices = new[]
            {
                new
                {
                    arrangementDate = invoice.ArrangementDate,
                    contentType = invoice.ContentType,
                    invoiceLink = invoice.InvoiceLink.AbsoluteUri,
                    orderNumber = invoice.OrderNumber
                }
            }
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var path = InvoiceLink(account, invoice.PackageNumber);
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Put, path, content, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(response.Error!, response.RateLimit);
        return AdapterResult<InvoiceDeliveryResult>.Success(new(invoice.PackageNumber, "SUBMITTED"), response.RateLimit);
    }

    public async Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<InvoiceDeliveryStatus>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar fatura durumu sorgulanmadı.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<InvoiceDeliveryStatus>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsManualRead(account.Connection))
            return await Unsupported<InvoiceDeliveryStatus>("Hepsiburada fatura durumu yalnız etkin veya doğrulanmış bağlantıdan okunabilir.");
        if (string.IsNullOrWhiteSpace(reference.OrderNumber))
            return Failure<InvoiceDeliveryStatus>(AdapterErrorClass.Validation, "HEPSIBURADA_ORDER_NUMBER_REQUIRED", "Hepsiburada fatura durumu için sipariş numarası zorunludur.", HttpStatusCode.BadRequest);

        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, OrderDetails(account, reference.OrderNumber), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<InvoiceDeliveryStatus>.Failure(response.Error!, response.RateLimit);
        try
        {
            var uploaded = HepsiburadaJsonMapper.InvoiceUploaded(response.Value!.RootElement);
            return AdapterResult<InvoiceDeliveryStatus>.Success(new(reference.ExternalReference, uploaded ? "INVOICED" : "NOT_INVOICED", uploaded), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<InvoiceDeliveryStatus>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_INVOICE_STATUS_CONTRACT_INVALID", "Hepsiburada sipariş yanıtında fatura durumu beklenen hasInvoice alanıyla eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    internal static string Orders(HepsiburadaRequestContext context, string query) => $"orders/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}?{query}";
    internal static string OrderDetails(HepsiburadaRequestContext context, string orderNumber) => $"orders/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/ordernumber/{Uri.EscapeDataString(orderNumber)}";
    internal static string Packages(HepsiburadaRequestContext context, string query) => $"packages/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}?{query}";
    internal static string PackagesByStatus(HepsiburadaRequestContext context, string status, string query) => $"packages/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/{Uri.EscapeDataString(status)}?{query}";
    internal static string PackageTrackingInfo(string merchantId, string packageNumber) => $"packages/merchantid/{Uri.EscapeDataString(merchantId)}/packagenumber/{Uri.EscapeDataString(packageNumber)}";
    internal static string InvoiceLink(HepsiburadaRequestContext context, string packageNumber) => $"packages/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/packagenumber/{Uri.EscapeDataString(packageNumber)}/invoice";
    internal static string Listings(HepsiburadaRequestContext context, string query) => $"listings/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}?{query}";
    internal static string Claims(HepsiburadaRequestContext context, string status, string query) => $"claims/merchantId/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/status/{Uri.EscapeDataString(status)}?{query}";
    internal static string AcceptClaim(HepsiburadaRequestContext context, string claimNumber) => $"claims/number/{Uri.EscapeDataString(claimNumber)}/accept";
    internal static string ConfirmClaimPreApproval(HepsiburadaRequestContext context, string claimNumber) => $"claims/number/{Uri.EscapeDataString(claimNumber)}/preapprovalconfirm";
    internal static string RejectClaim(HepsiburadaRequestContext context, string claimNumber) => $"claims/number/{Uri.EscapeDataString(claimNumber)}/reject";
    internal static string ClaimQuery(
        int offset,
        int limit,
        DateTimeOffset? beginDate,
        DateTimeOffset? endDate,
        DateTimeOffset? statusBeginDate = null,
        DateTimeOffset? statusEndDate = null)
    {
        var query = new List<string>
        {
            $"offset={Math.Max(0, offset).ToString(CultureInfo.InvariantCulture)}",
            $"limit={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}"
        };
        if (beginDate is { } begin)
            query.Add("beginDate=" + Uri.EscapeDataString(begin.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        if (endDate is { } end)
            query.Add("endDate=" + Uri.EscapeDataString(end.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        if (statusBeginDate is { } statusBegin)
            query.Add("statusBeginDate=" + Uri.EscapeDataString(statusBegin.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        if (statusEndDate is { } statusFinish)
            query.Add("statusEndDate=" + Uri.EscapeDataString(statusFinish.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        return string.Join('&', query);
    }
    internal static string Categories(int page, int limit) => $"api/categories/get-all-categories?leaf=true&status=ACTIVE&available=true&version=1&page={page.ToString(CultureInfo.InvariantCulture)}&size={limit.ToString(CultureInfo.InvariantCulture)}";
    internal static string ProductImportUpload() => "api/products/import?version=1";
    internal static string ProductImportStatus(string trackingId, int page, int size) => $"api/products/status/{Uri.EscapeDataString(trackingId)}?version=1&page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";
    internal static string ProductUpdateUpload() => "/ticket-api/api/integrator/import?version=1";
    internal static string ProductUpdateStatus(string trackingId, int page, int size) => $"/ticket-api/api/integrator/status/{Uri.EscapeDataString(trackingId)}?version=1&page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";
    internal static string InventoryUpload(HepsiburadaRequestContext context) => $"listings/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/inventory-uploads";
    internal static string InventoryUploadStatus(HepsiburadaRequestContext context, string uploadId) => $"listings/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/inventory-uploads/id/{Uri.EscapeDataString(uploadId)}";
    internal static string InventoryUploadStatus(string merchantId, string uploadId) => $"listings/merchantid/{Uri.EscapeDataString(merchantId)}/inventory-uploads/id/{Uri.EscapeDataString(uploadId)}";
    internal static string StageTestOrder(HepsiburadaRequestContext context, string merchantId) => $"orders/merchantId/{Uri.EscapeDataString(merchantId)}";
    internal static bool ValidTestOrderSku(string? sku) => !string.IsNullOrWhiteSpace(sku) && sku == sku.Trim() && !sku.Any(char.IsWhiteSpace) && sku.Length <= 100;
    internal static string TestOrderPayload(string merchantId, string orderNumber, string sku, DateTimeOffset orderDate) => JsonSerializer.Serialize(new
    {
        OrderNumber = orderNumber,
        OrderDate = orderDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        PaymentStatus = "Paid",
        Customer = new { CustomerId = "stage-customer", Name = "Stage Test" },
        DeliveryAddress = new { AddressId = "stage-address", Name = "Stage Test", AddressDetail = "Test adresi", Email = "stage-test@example.invalid", CountryCode = "TR", PhoneNumber = "0000000000", AlternatePhoneNumber = "0000000000", Town = "Kadikoy", District = "Test", City = "Istanbul" },
        LineItems = new[] { new { Sku = sku, MerchantId = merchantId, Quantity = 1, Price = new { Amount = 1m, Currency = "TRY" }, Vat = 0m, TotalPrice = new { Amount = 1m, Currency = "TRY" }, CargoCompanyId = 1, DeliveryOptionId = 1 } }
    });
    internal static bool ValidPriceInventoryPayload(string payloadJson, string merchantId)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("merchantId", out var merchant) || merchant.ValueKind != JsonValueKind.String
                || !string.Equals(merchant.GetString(), merchantId, StringComparison.Ordinal)
                || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 4000) return false;
            var merchantSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hbSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                var merchantSku = item.TryGetProperty("merchantSku", out var merchantSkuValue) && merchantSkuValue.ValueKind == JsonValueKind.String ? merchantSkuValue.GetString() : null;
                var hbSku = item.TryGetProperty("hepsiburadaSku", out var hbSkuValue) && hbSkuValue.ValueKind == JsonValueKind.String ? hbSkuValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(merchantSku) || merchantSku != merchantSku.Trim() || merchantSku.Any(char.IsWhiteSpace)
                    || string.IsNullOrWhiteSpace(hbSku) || hbSku != hbSku.Trim() || hbSku.Any(char.IsWhiteSpace)
                    || !merchantSkus.Add(merchantSku) || !hbSkus.Add(hbSku)) return false;
                if (!item.TryGetProperty("availableStock", out var stockValue) || !stockValue.TryGetInt32(out var stock) || stock < 0) return false;
                if (!item.TryGetProperty("price", out var priceValue) || !priceValue.TryGetDecimal(out var price) || price <= 0 || decimal.Round(price, 2) != price) return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }
    internal static string BuildInventoryUploadXml(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var items = document.RootElement.GetProperty("items").EnumerateArray().Select(item => new XElement("listing",
            new XElement("HepsiburadaSku", item.GetProperty("hepsiburadaSku").GetString()),
            new XElement("MerchantSku", item.GetProperty("merchantSku").GetString()),
            new XElement("Price", item.GetProperty("price").GetDecimal().ToString("0.00", CultureInfo.GetCultureInfo("tr-TR"))),
            new XElement("AvailableStock", item.GetProperty("availableStock").GetInt32().ToString(CultureInfo.InvariantCulture))));
        return new XDocument(new XElement("listings", items)).ToString(SaveOptions.DisableFormatting);
    }
    private static string? Text(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
            if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return property.Value.ToString();
        return null;
    }
    internal static bool ValidProductUpdatePayload(string payloadJson, string merchantId)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            var payloadMerchantId = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("merchantId", out var merchant) && merchant.ValueKind == JsonValueKind.String ? merchant.GetString() : null;
            if (!string.Equals(payloadMerchantId, merchantId, StringComparison.Ordinal)) return false;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 1000) return false;
            var merchantSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hbSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                var merchantSku = item.TryGetProperty("merchantSku", out var skuValue) && skuValue.ValueKind == JsonValueKind.String ? skuValue.GetString() : null;
                var hbSku = item.TryGetProperty("hbSku", out var hbValue) && hbValue.ValueKind == JsonValueKind.String ? hbValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(merchantSku) || merchantSku != merchantSku.Trim() || merchantSku.Any(char.IsWhiteSpace)
                    || string.IsNullOrWhiteSpace(hbSku) || hbSku != hbSku.Trim() || hbSku.Any(char.IsWhiteSpace)
                    || !merchantSkus.Add(merchantSku) || !hbSkus.Add(hbSku)) return false;
                if (item.TryGetProperty("attributes", out var attributes))
                {
                    if (attributes.ValueKind != JsonValueKind.Object) return false;
                    foreach (var attribute in attributes.EnumerateObject())
                        if (!attribute.Name.StartsWith("attribute-", StringComparison.OrdinalIgnoreCase) || attribute.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)) return false;
                }
                for (var imageIndex = 1; imageIndex <= 10; imageIndex++)
                {
                    if (!item.TryGetProperty($"image{imageIndex}", out var imageValue)) continue;
                    if (imageValue.ValueKind != JsonValueKind.String || !Uri.TryCreate(imageValue.GetString(), UriKind.Absolute, out var imageUri) || imageUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(imageUri.UserInfo)) return false;
                }
            }
            return true;
        }
        catch (JsonException) { return false; }
    }
    internal static string PendingProductMatches(HepsiburadaRequestContext context, int page, int size) => $"api/products/products-by-merchant-and-status?merchantId={Uri.EscapeDataString(context.Connection.ExternalStoreId)}&productStatus=PRE_MATCHED&taskStatus=false&version=1&page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";
    internal static string ProductMatchDecision(bool approve) => approve ? "api/products/approve-prematch" : "api/products/reject-prematch";
    internal static string ProductMatchDecisionPayload(string merchantId, IReadOnlyList<string> merchantSkus) => JsonSerializer.Serialize(new[] { new { merchant = merchantId, merchantSkuList = merchantSkus.Select(sku => sku.Trim()).ToArray() } });
    internal static bool ValidMatchDecisionSkus(IReadOnlyList<string> merchantSkus) => merchantSkus.Count is > 0 and <= 100
        && merchantSkus.All(sku => !string.IsNullOrWhiteSpace(sku) && sku == sku.Trim() && sku == sku.ToUpperInvariant() && !sku.Any(char.IsWhiteSpace))
        && merchantSkus.Distinct(StringComparer.OrdinalIgnoreCase).Count() == merchantSkus.Count;
    internal static string CategoryAttributes(string categoryId) => $"api/categories/{Uri.EscapeDataString(categoryId)}/attributes?version=2";
    internal static string AttributeValues(string categoryId, string attributeId, int page, int limit) => $"api/categories/{Uri.EscapeDataString(categoryId)}/attribute/{Uri.EscapeDataString(attributeId)}/values?version=5&page={page.ToString(CultureInfo.InvariantCulture)}&size={limit.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryClaimStatus(string? value, out string status)
    {
        var normalized = value?.Trim().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        var match = ClaimStatuses.FirstOrDefault(candidate => string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
        status = match?.ToLowerInvariant() ?? "";
        return match is not null;
    }

    private async Task<AdapterResult<JsonDocument>> SendAsync(HepsiburadaRequestContext context, Uri baseAddress, HttpMethod method, string path, CancellationToken cancellationToken) =>
        await SendAsync(context, baseAddress, method, path, null, cancellationToken);

    private async Task<AdapterResult<JsonDocument>> SendAsync(HepsiburadaRequestContext context, Uri baseAddress, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_AUTHENTICATION_UNVERIFIED", "Hepsiburada auth biçimi SIT hesabında doğrulanana kadar bağlantı isteği gönderilmedi.", null, null, null));
        var client = clients.CreateClient("Hepsiburada");
        using var request = new HttpRequestMessage(method, new Uri(baseAddress, path));
        request.Content = content;
        if (!ApplyAuthentication(request, context))
            return Failure<JsonDocument>(AdapterErrorClass.Validation, "HEPSIBURADA_INTEGRATOR_NAME_INVALID", "Hepsiburada entegratör adı geçerli bir User-Agent kimliği olmalıdır.", HttpStatusCode.UnprocessableEntity);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout > TimeSpan.Zero && settings.Timeout < TimeSpan.FromMinutes(2) ? settings.Timeout : TimeSpan.FromSeconds(30));
            using var response = await client.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var rate = RateLimit(response);
            var requestId = response.Headers.TryGetValues("X-Request-Id", out var ids) ? ids.FirstOrDefault() : null;
            if (rate is not null)
                logger.LogDebug("Hepsiburada yanıt hız sınırı başlıkları. ConnectionId: {ConnectionId}, Limit: {Limit}, Remaining: {Remaining}, ResetAt: {ResetAt}, RetryAfterSeconds: {RetryAfterSeconds}", context.Connection.Id, rate.Limit, rate.Remaining, rate.ResetAt, rate.RetryAfter?.TotalSeconds);
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } retryDate ? retryDate - timeProvider.GetUtcNow() : null);
                var error = Error(response.StatusCode, retryAfter ?? rate?.RetryAfter, requestId);
                logger.LogWarning("Hepsiburada API isteği reddedildi. ConnectionId: {ConnectionId}, Status: {Status}, Code: {Code}, RequestId: {RequestId}, Limit: {Limit}, Remaining: {Remaining}, ResetAt: {ResetAt}, RetryAfterSeconds: {RetryAfterSeconds}", context.Connection.Id, (int)response.StatusCode, error.Code, requestId, rate?.Limit, rate?.Remaining, rate?.ResetAt, error.RetryAfter?.TotalSeconds ?? rate?.RetryAfter?.TotalSeconds);
                return AdapterResult<JsonDocument>.Failure(error, rate);
            }
            try { return AdapterResult<JsonDocument>.Success(JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body), rate); }
            catch (JsonException)
            {
                return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_JSON_INVALID", "Hepsiburada geçerli JSON yanıtı vermedi.", (int)response.StatusCode, null, requestId), rate);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "HEPSIBURADA_TIMEOUT", "Hepsiburada isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(15), null));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Hepsiburada API isteği başarısız. ConnectionId: {ConnectionId}", context.Connection.Id);
            return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "HEPSIBURADA_NETWORK_ERROR", "Hepsiburada bağlantısı geçici olarak kurulamadı.", null, TimeSpan.FromSeconds(15), null));
        }
    }

    internal static bool ApplyAuthentication(HttpRequestMessage request, HepsiburadaRequestContext context)
    {
        var integratorName = (context.IntegratorName ?? context.Username).Trim();
        if (integratorName.Length == 0 || !ProductInfoHeaderValue.TryParse(integratorName, out var userAgent) || userAgent.Product is null)
            return false;
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{context.Username}:{context.Password}")));
        request.Headers.UserAgent.Add(userAgent);
        return true;
    }

    private static (int Offset, int Limit) Page(AdapterPageRequest page, int configuredLimit)
    {
        var offset = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? Math.Max(0, parsed) : 0;
        return (offset, Math.Clamp(page.Limit, 1, Math.Clamp(configuredLimit, 1, 10)));
    }

    private static bool ConnectionWritesEnabled(string settingsJson)
    {
        try
        {
            using var settings = JsonDocument.Parse(settingsJson);
            return settings.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    internal static bool ValidProductImportPayload(string payloadJson, string merchantId)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 1000) return false;
            var merchantSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var barcodes = new HashSet<string>(StringComparer.Ordinal);
            string? variantGroupId = null;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("categoryId", out var category) || category.ValueKind != JsonValueKind.Number || !category.TryGetInt32(out var categoryId) || categoryId <= 0) return false;
                if (!item.TryGetProperty("merchant", out var merchant) || merchant.ValueKind != JsonValueKind.String || !string.Equals(merchant.GetString(), merchantId, StringComparison.Ordinal)) return false;
                if (!item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object) return false;
                if (!attributes.TryGetProperty("merchantSku", out var skuValue) || skuValue.ValueKind != JsonValueKind.String) return false;
                var sku = skuValue.GetString();
                if (string.IsNullOrWhiteSpace(sku) || sku != sku.ToUpperInvariant() || sku.Any(char.IsWhiteSpace) || !merchantSkus.Add(sku)) return false;
                if (!attributes.TryGetProperty("VaryantGroupID", out var groupValue) || groupValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(groupValue.GetString())) return false;
                variantGroupId ??= groupValue.GetString();
                if (!string.Equals(groupValue.GetString(), variantGroupId, StringComparison.Ordinal)) return false;
                if (!attributes.TryGetProperty("Barcode", out var barcodeValue) || barcodeValue.ValueKind != JsonValueKind.String || !ValidEan13(barcodeValue.GetString()) || !barcodes.Add(barcodeValue.GetString()!)) return false;
                if (!attributes.TryGetProperty("UrunAdi", out var titleValue) || titleValue.ValueKind != JsonValueKind.String || !attributes.TryGetProperty("Marka", out var brandValue) || brandValue.ValueKind != JsonValueKind.String || !StartsWithBrand(titleValue.GetString(), brandValue.GetString())) return false;
                if (!attributes.TryGetProperty("UrunAciklamasi", out var descriptionValue) || descriptionValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(descriptionValue.GetString())) return false;
                if (!attributes.TryGetProperty("tax_vat_rate", out var vatValue) || vatValue.ValueKind != JsonValueKind.String || !int.TryParse(vatValue.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var vatRate) || vatRate is < 0 or > 100) return false;
                if (!attributes.TryGetProperty("price", out var priceValue) || priceValue.ValueKind != JsonValueKind.String || !decimal.TryParse(priceValue.GetString(), NumberStyles.Number, CultureInfo.GetCultureInfo("tr-TR"), out var price) || price <= 0 || price != decimal.Round(price, 2) || priceValue.GetString() != price.ToString("0.00", CultureInfo.GetCultureInfo("tr-TR"))) return false;
                if (!attributes.TryGetProperty("stock", out var stockValue) || stockValue.ValueKind != JsonValueKind.String || !int.TryParse(stockValue.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var stock) || stock < 0) return false;
                var imageCount = 0;
                foreach (var imageProperty in attributes.EnumerateObject().Where(property => property.Name.StartsWith("Image", StringComparison.Ordinal)))
                {
                    if (!int.TryParse(imageProperty.Name.AsSpan("Image".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var imageIndex) || imageIndex is < 1 or > 5 || imageProperty.Value.ValueKind != JsonValueKind.String || !IsPublicHttpsUri(imageProperty.Value.GetString())) return false;
                    imageCount++;
                }
                if (imageCount == 0 || attributes.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Array)) return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool ValidEan13(string? value)
    {
        if (value is null || value.Length != 13 || value.Any(character => !char.IsAsciiDigit(character))) return false;
        var sum = 0;
        for (var index = 0; index < 12; index++) sum += (value[index] - '0') * (index % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == value[12] - '0';
    }

    private static bool IsPublicHttpsUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(uri.Host)
        && string.IsNullOrEmpty(uri.UserInfo)
        && !uri.IsLoopback
        && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithBrand(string? title, string? brand)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(brand) || !title.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) return false;
        return title.Length == brand.Length || !char.IsLetterOrDigit(title[brand.Length]);
    }

    private static (int Page, int Limit) ReferencePage(AdapterPageRequest page, int maximumLimit)
    {
        var pageNumber = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPage) ? Math.Clamp(parsedPage, 0, 1_000_000) : 0;
        return (pageNumber, Math.Clamp(page.Limit, 1, maximumLimit));
    }

    private static bool TryReferenceParts(string? value, out string categoryId, out string attributeId)
    {
        var parts = value?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        categoryId = parts.Length == 2 ? parts[0] : "";
        attributeId = parts.Length == 2 ? parts[1] : "";
        return parts.Length == 2;
    }

    internal RateLimitMetadata? RateLimit(HttpResponseMessage response)
    {
        int? limit = response.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues) && int.TryParse(limitValues.FirstOrDefault(), out var parsedLimit) ? parsedLimit : null;
        int? remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && int.TryParse(values.FirstOrDefault(), out var parsedRemaining) ? parsedRemaining : null;
        DateTimeOffset? resetAt = null;
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) && int.TryParse(resets.FirstOrDefault(), out var resetSeconds))
        {
            retryAfter ??= TimeSpan.FromSeconds(Math.Max(0, resetSeconds));
            resetAt = timeProvider.GetUtcNow().AddSeconds(Math.Max(0, resetSeconds));
        }
        return limit is null && remaining is null && resetAt is null && retryAfter is null ? null : new(remaining, resetAt, retryAfter, limit);
    }

    internal static AdapterError Error(HttpStatusCode status, TimeSpan? retryAfter, string? requestId) => status switch
    {
        HttpStatusCode.Unauthorized => new(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIALS_REJECTED", "Hepsiburada HTTP 401: entegratör kullanıcı adı ve servis anahtarı bu ortama uymuyor. Aynı aktif Entegratörlerim kaydından alınan kullanıcı adı ve canlı servis anahtarını kontrol edin.", (int)status, null, requestId),
        HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "HEPSIBURADA_ACCESS_FORBIDDEN", "Hepsiburada HTTP 403: mağaza veya endpoint erişim yetkisi reddedildi. Mağaza ID’sini ve entegratör erişim yetkisini kontrol edin.", (int)status, null, requestId),
        HttpStatusCode.TooManyRequests => new(AdapterErrorClass.RateLimit, "HEPSIBURADA_RATE_LIMITED", "Hepsiburada istek sınırına ulaşıldı.", 429, retryAfter ?? TimeSpan.FromSeconds(5), requestId),
        HttpStatusCode.NotFound => new(AdapterErrorClass.NotFound, "HEPSIBURADA_RESOURCE_NOT_FOUND", "Hepsiburada kaynağı bulunamadı.", 404, null, requestId),
        >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "HEPSIBURADA_REMOTE_ERROR", "Hepsiburada geçici sunucu hatası verdi.", (int)status, retryAfter ?? TimeSpan.FromSeconds(15), requestId),
        _ => new(AdapterErrorClass.Validation, "HEPSIBURADA_REQUEST_REJECTED", "Hepsiburada isteği doğrulama nedeniyle reddedildi.", (int)status, null, requestId)
    };

    private static Task<AdapterResult<T>> Unsupported<T>(string message) => Task.FromResult(AdapterResult<T>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_CAPABILITY_NOT_ENABLED", message, (int)HttpStatusCode.NotImplemented, null, null)));
    private static AdapterResult<T> Failure<T>(AdapterErrorClass @class, string code, string message, HttpStatusCode? status = null) => AdapterResult<T>.Failure(new(@class, code, message, status is null ? null : (int)status, null, null));
}
