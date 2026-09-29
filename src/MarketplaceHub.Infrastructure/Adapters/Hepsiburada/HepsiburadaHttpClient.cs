using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed class HepsiburadaHttpClient(
    IHttpClientFactory clients,
    HepsiburadaAuthenticationHandler authentication,
    IOptions<HepsiburadaOptions> options,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<HepsiburadaHttpClient> logger)
    : IConnectionPort, IReferenceDataPort, IProductPort, IProductVisualLookupPort, IInventoryPricePort, IOrderPort, IOrderPackageReadPort, IReturnPort, IInvoiceMarketplacePort
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
        if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        try
        {
            HepsiburadaJsonMapper.OrderPage(result.Value!.RootElement);
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

    public async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        var (offset, limit) = Page(page, settings.PageSize);
        var query = new List<string> { $"offset={offset.ToString(CultureInfo.InvariantCulture)}", $"limit={limit.ToString(CultureInfo.InvariantCulture)}" };
        if (filter.ModifiedAfter is { } modifiedAfter) query.Add("updateStartDate=" + Uri.EscapeDataString(modifiedAfter.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(filter.ProductMainId)) query.Add("productId=" + Uri.EscapeDataString(filter.ProductMainId));
        var response = await SendAsync(account, account.ListingBaseAddress, HttpMethod.Get, Listings(account, string.Join('&', query)), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var pageResult = HepsiburadaJsonMapper.ListingPage(response.Value!.RootElement);
            var items = pageResult.Items.Select(HepsiburadaJsonMapper.CatalogProduct).ToArray();
            var nextOffset = offset + pageResult.Items.Count;
            var hasMore = pageResult.TotalCount is { } total ? nextOffset < total : pageResult.Items.Count == limit;
            return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(items, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, pageResult.TotalCount), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_LISTING_CONTRACT_INVALID", "Hepsiburada listing yanıtı beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) =>
        Unsupported<RemoteOperationRef>("Hepsiburada ürün yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        Unsupported<RemoteOperationRef>("Hepsiburada ürün yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        Unsupported<RemoteOperationRef>("Hepsiburada ürün yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        Unsupported<RemoteOperationRef>("Hepsiburada ürün yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        Unsupported<RemoteOperationRef>("Hepsiburada ürün yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");
    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => Unsupported<RemoteOperationStatus>("Hepsiburada ürün gönderim sonucu okuması bu aşamada desteklenmiyor.");
    public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Unsupported<RemotePublicationStatus>("Hepsiburada ürün durum okuması bu aşamada desteklenmiyor.");
    public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Hepsiburada ürün yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");
    public async Task<AdapterResult<RemoteProduct?>> FindByBarcodeAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        var page = await ListAsync(context, new(null, Math.Clamp(settings.PageSize, 1, 10)), new(null, Barcode: barcode), cancellationToken);
        if (!page.IsSuccess) return AdapterResult<RemoteProduct?>.Failure(page.Error!, page.RateLimit);
        var product = page.Value!.Items.FirstOrDefault(item => string.Equals(item.Barcode, barcode, StringComparison.OrdinalIgnoreCase));
        return AdapterResult<RemoteProduct?>.Success(product, page.RateLimit);
    }
    public Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) =>
        Unsupported<RemoteOperationRef>("Hepsiburada fiyat ve stok yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");

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
            var orderNumbers = pageResult.Items.Select(HepsiburadaJsonMapper.OrderNumber).Where(number => !string.IsNullOrWhiteSpace(number)).Select(number => number!).Distinct(StringComparer.Ordinal).ToArray();
            var mapped = new List<RemoteOrder>(orderNumbers.Length);
            foreach (var orderNumber in orderNumbers)
            {
                var detail = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, OrderDetails(account, orderNumber), cancellationToken);
                if (!detail.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(detail.Error!, detail.RateLimit ?? response.RateLimit);
                try { mapped.Add(HepsiburadaJsonMapper.Order(detail.Value!.RootElement, orderNumber)); }
                catch (JsonException) { return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_ORDER_CONTRACT_INVALID", "Hepsiburada sipariş detay yanıtı beklenen sözleşmeyle eşleşmiyor.", HttpStatusCode.BadGateway); }
            }
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
        try { return AdapterResult<RemoteOrder>.Success(HepsiburadaJsonMapper.Order(result.Value!.RootElement, externalOrderId), result.RateLimit); }
        catch (JsonException) { return Failure<RemoteOrder>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_ORDER_CONTRACT_INVALID", "Hepsiburada sipariş detay yanıtı beklenen sözleşmeyle eşleşmiyor.", HttpStatusCode.BadGateway); }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteOrderPackage>>> PollPackagesAsync(AdapterContext context, PackagePollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteOrderPackage>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (window.ModifiedAfter is { } start && window.ModifiedBefore is { } end && (end < start || end - start > TimeSpan.FromHours(24)))
            return Failure<AdapterPageResult<RemoteOrderPackage>>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_WINDOW_INVALID", "Hepsiburada paket listelemesi artan ve en fazla 24 saatlik tarih aralığı kabul eder.", HttpStatusCode.BadRequest);

        var (offset, limit) = Page(page, settings.PageSize);
        var query = new List<string> { $"offset={offset.ToString(CultureInfo.InvariantCulture)}", $"limit={limit.ToString(CultureInfo.InvariantCulture)}" };
        if (window.ModifiedAfter is { } after) query.Add("begindate=" + Uri.EscapeDataString(after.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        if (window.ModifiedBefore is { } before) query.Add("enddate=" + Uri.EscapeDataString(before.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, Packages(account, string.Join('&', query)), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrderPackage>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var pageResult = HepsiburadaJsonMapper.PackagePage(response.Value!.RootElement);
            var items = new List<RemoteOrderPackage>(pageResult.Items.Count);
            var issues = new List<AdapterPageIssue>();
            foreach (var item in pageResult.Items)
            {
                try { items.Add(HepsiburadaJsonMapper.OrderPackage(item)); }
                catch (JsonException)
                {
                    var identity = HepsiburadaJsonMapper.PackageIdentity(item) ?? $"offset:{offset + items.Count}";
                    issues.Add(new("HEPSIBURADA_PACKAGE_ORDER_LINK_MISSING", identity, "Paket kaydında açık sipariş ve paket kimliği bulunmadı; kayıt siparişe bağlanmadı."));
                }
            }
            var nextOffset = offset + pageResult.Items.Count;
            var hasMore = pageResult.TotalCount is { } total ? nextOffset < total : pageResult.Items.Count == limit;
            return AdapterResult<AdapterPageResult<RemoteOrderPackage>>.Success(new(items, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, pageResult.TotalCount, issues), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteOrderPackage>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PACKAGE_LIST_CONTRACT_INVALID", "Hepsiburada paket listesi beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) =>
        Unsupported<PackageActionResult>("Hepsiburada paket ve kargo yazması SIT kanıtı ve ayrı yetenek kapısı açılana kadar kapalıdır.");
    public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => Unsupported<bool>("Hepsiburada etiket dış yazması bu aşamada kapalıdır.");
    public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken) => Unsupported<CommonLabelDocument>("Hepsiburada kargo etiketi okuması bu aşamada desteklenmiyor.");
    public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Unsupported<StageTestOrderResult>("Test siparişi oluşturmak için ayrıca onay ve SIT fixture akışı gerekir; bağlantı testi dış yazma yapmaz.");
    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!TryClaimStatus(window.Status, out var status))
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_STATUS_INVALID", "Talep listelemesi için belgelenmiş bir Hepsiburada talep durumu zorunludur.", HttpStatusCode.BadRequest);
        if ((window.ModifiedAfter is null) != (window.ModifiedBefore is null)
            || window.ModifiedAfter is { } start && window.ModifiedBefore is { } end && end < start)
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_WINDOW_INVALID", "Talep tarih filtresi başlangıç ve bitiş tarihlerini birlikte ve artan sırada gerektirir.", HttpStatusCode.BadRequest);

        var offset = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
        var limit = Math.Clamp(page.Limit, 1, 100);
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, Claims(account, status, ClaimQuery(offset, limit, window.ModifiedAfter, window.ModifiedBefore)), cancellationToken);
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
            return Failure<ReturnActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_ACTION_INVALID", "Talep aksiyonu APPROVE veya REJECT olmalıdır.", HttpStatusCode.BadRequest);

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
    internal static string InvoiceLink(HepsiburadaRequestContext context, string packageNumber) => $"packages/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/packagenumber/{Uri.EscapeDataString(packageNumber)}/invoice";
    internal static string Listings(HepsiburadaRequestContext context, string query) => $"listings/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}?{query}";
    internal static string Claims(HepsiburadaRequestContext context, string status, string query) => $"claims/merchantId/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/status/{Uri.EscapeDataString(status)}?{query}";
    internal static string AcceptClaim(HepsiburadaRequestContext context, string claimNumber) => $"claims/number/{Uri.EscapeDataString(claimNumber)}/accept";
    internal static string RejectClaim(HepsiburadaRequestContext context, string claimNumber) => $"claims/number/{Uri.EscapeDataString(claimNumber)}/reject";
    internal static string ClaimQuery(int offset, int limit, DateTimeOffset? beginDate, DateTimeOffset? endDate)
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
        return string.Join('&', query);
    }
    internal static string Categories(int page, int limit) => $"api/categories/get-all-categories?leaf=true&status=ACTIVE&available=true&version=1&page={page.ToString(CultureInfo.InvariantCulture)}&size={limit.ToString(CultureInfo.InvariantCulture)}";
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
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{context.Username}:{context.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("MarketplaceHub/1.0");
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
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "HEPSIBURADA_AUTHENTICATION_FAILED", "Hepsiburada kimlik doğrulaması veya erişim yetkisi başarısız.", (int)status, null, requestId),
        HttpStatusCode.TooManyRequests => new(AdapterErrorClass.RateLimit, "HEPSIBURADA_RATE_LIMITED", "Hepsiburada istek sınırına ulaşıldı.", 429, retryAfter ?? TimeSpan.FromSeconds(5), requestId),
        HttpStatusCode.NotFound => new(AdapterErrorClass.NotFound, "HEPSIBURADA_RESOURCE_NOT_FOUND", "Hepsiburada kaynağı bulunamadı.", 404, null, requestId),
        >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "HEPSIBURADA_REMOTE_ERROR", "Hepsiburada geçici sunucu hatası verdi.", (int)status, retryAfter ?? TimeSpan.FromSeconds(15), requestId),
        _ => new(AdapterErrorClass.Validation, "HEPSIBURADA_REQUEST_REJECTED", "Hepsiburada isteği doğrulama nedeniyle reddedildi.", (int)status, null, requestId)
    };

    private static Task<AdapterResult<T>> Unsupported<T>(string message) => Task.FromResult(AdapterResult<T>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_CAPABILITY_NOT_ENABLED", message, (int)HttpStatusCode.NotImplemented, null, null)));
    private static AdapterResult<T> Failure<T>(AdapterErrorClass @class, string code, string message, HttpStatusCode? status = null) => AdapterResult<T>.Failure(new(@class, code, message, status is null ? null : (int)status, null, null));
}
