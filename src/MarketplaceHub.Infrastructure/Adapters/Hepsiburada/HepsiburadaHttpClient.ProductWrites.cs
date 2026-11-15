using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient
{
    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<RemoteOperationRef>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar ürün dosyası gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<RemoteOperationRef>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (account.CatalogBaseAddress is null) return await Unsupported<RemoteOperationRef>("Hepsiburada ürün import için doğrulanmış katalog base URL'si yok.");
        // On a STAGE (SIT) connection the import is what produces PRODUCT_WRITE evidence, so only
        // PRICE_WRITE/INVENTORY_WRITE are required there; PRODUCTION still requires all three.
        if (!ExternalWriteGate.AllowsRuntimeWrite(account.Connection, context, GlobalWritesEnabled)
            || !await authentication.HasVerifiedWriteEvidenceAsync(account.Connection, cancellationToken, HepsiburadaStageCertificationPolicy.PublicationEvidenceCapabilities(account.Connection)))
            return await Unsupported<RemoteOperationRef>("Hepsiburada ürün import yalnız write kapıları ve PRODUCT_WRITE/PRICE_WRITE/INVENTORY_WRITE SIT fixture kanıtları açıldıktan sonra kullanılabilir.");
        if (publication.ProductId == Guid.Empty || string.IsNullOrWhiteSpace(publication.PayloadHash) || !ValidProductImportPayload(publication.PayloadJson, account.Connection.ExternalStoreId))
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "HEPSIBURADA_PRODUCT_IMPORT_PAYLOAD_INVALID", "Hepsiburada ürün import dosyası beklenen en fazla 1000 satırlık JSON sözleşmesini sağlamıyor.", HttpStatusCode.BadRequest);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(publication.PayloadJson));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "products.json");
        var response = await SendAsync(account, account.CatalogBaseAddress, HttpMethod.Post, ProductImportUpload(), content, cancellationToken);
        using var responseDocument = response.Value;
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
        if (!ExternalWriteGate.AllowsRuntimeWrite(account.Connection, context, GlobalWritesEnabled)
            || !await authentication.HasVerifiedWriteEvidenceAsync(account.Connection, cancellationToken, HepsiburadaStageCertificationPolicy.PublicationEvidenceCapabilities(account.Connection)))
            return await Unsupported<RemoteOperationRef>("Hepsiburada ürün güncellemesi yalnız dış yazma kapıları ve mağaza/ortam kapsamlı fiyat ve stok yazma kanıtı sağlandığında kullanılabilir.");
        if (publication.ProductId == Guid.Empty || string.IsNullOrWhiteSpace(publication.PayloadHash) || !ValidProductUpdatePayload(payloadJson, account.Connection.ExternalStoreId))
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "HEPSIBURADA_PRODUCT_UPDATE_PAYLOAD_INVALID", "Hepsiburada ürün güncelleme dosyası merchantId/hbSku/merchantSku sözleşmesini sağlamıyor.", HttpStatusCode.BadRequest);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(payloadJson));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "product-updates.json");
        var response = await SendAsync(account, account.CatalogBaseAddress, HttpMethod.Post, ProductUpdateUpload(), content, cancellationToken);
        using var responseDocument = response.Value;
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
        publication is null ? Unsupported<RemoteOperationRef>("Hepsiburada teslimat süresi içeriği zorunludur.") : SubmitShippingInfoAsync(context, publication.ApprovedDeliveryPayloadJson, cancellationToken);

    // The dispatch time (DispatchTime) of each listing, by the listing shipping-info upload: a JSON array of
    // {hepsiburadaSku, merchantSku, dispatchTime}; the answer is an upload id read back like an inventory upload.
    private async Task<AdapterResult<RemoteOperationRef>> SubmitShippingInfoAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<RemoteOperationRef>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar teslimat süresi gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<RemoteOperationRef>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!ExternalWriteGate.AllowsRuntimeWrite(account.Connection, context, GlobalWritesEnabled)
            || !await authentication.HasVerifiedWriteEvidenceAsync(account.Connection, cancellationToken, HepsiburadaStageCertificationPolicy.PublicationEvidenceCapabilities(account.Connection)))
            return await Unsupported<RemoteOperationRef>("Hepsiburada teslimat süresi yalnız dış yazma kapıları ve mağaza/ortam kapsamlı fiyat ve stok yazma kanıtı sağlandığında gönderilebilir.");
        if (!ValidShippingInfoPayload(payloadJson, account.Connection.ExternalStoreId))
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "HEPSIBURADA_SHIPPING_INFO_PAYLOAD_INVALID", "Hepsiburada teslimat süresi yüklemesi merchantId, hbSku, merchantSku ve dispatchTime sözleşmesini sağlamıyor.", HttpStatusCode.BadRequest);
        payloadJson = WithHepsiburadaSkus(payloadJson, await StageHepsiburadaSkusAsync(account, cancellationToken));
        if (!ValidShippingInfoPayload(payloadJson, account.Connection.ExternalStoreId))
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "HEPSIBURADA_SHIPPING_INFO_PAYLOAD_INVALID", "Hepsiburada listing hbSku eşlemesi teslimat süresi yüklemesinde yinelenen hbSku üretti.", HttpStatusCode.BadRequest);

        using var content = new StringContent(BuildShippingInfoUploadJson(payloadJson), Encoding.UTF8, "application/json");
        var response = await SendAsync(account, account.ListingBaseAddress, HttpMethod.Post, ShippingInfoUpload(account), content, cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(response.Error!, response.RateLimit);
        try
        {
            var uploadId = HepsiburadaJsonMapper.InventoryUploadId(response.Value!.RootElement);
            return AdapterResult<RemoteOperationRef>.Success(new($"{ShippingInfoUploadPrefix}{uploadId}", "LISTING_SHIPPING_INFO_UPLOAD", timeProvider.GetUtcNow()), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_SHIPPING_INFO_UPLOAD_CONTRACT_INVALID", "Hepsiburada teslimat süresi yanıtında yükleme numarası bulunamadı.", HttpStatusCode.BadGateway);
        }
    }
    public async Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<RemoteOperationStatus>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar trackingId sorgulanmadı.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<RemoteOperationStatus>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        return await GetOperationAsync(account, externalOperationId, cancellationToken);
    }

    internal async Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(HepsiburadaRequestContext account, string externalOperationId, CancellationToken cancellationToken)
    {
        if (!IntegrationRuntimePolicy.AllowsManualRead(account.Connection)) return await Unsupported<RemoteOperationStatus>("Hepsiburada import durumu yalnız etkin veya doğrulanmış bağlantıda okunabilir.");
        if (string.IsNullOrWhiteSpace(externalOperationId) || externalOperationId.Length > 240)
            return Failure<RemoteOperationStatus>(AdapterErrorClass.Validation, "HEPSIBURADA_TRACKING_ID_INVALID", "Hepsiburada trackingId geçersiz.", HttpStatusCode.BadRequest);

        var isProductUpdate = externalOperationId.StartsWith("PRODUCT_UPDATE:", StringComparison.Ordinal);
        if (externalOperationId.StartsWith(ShippingInfoUploadPrefix, StringComparison.Ordinal))
        {
            var shippingUploadId = externalOperationId[ShippingInfoUploadPrefix.Length..];
            if (string.IsNullOrWhiteSpace(shippingUploadId) || shippingUploadId.Length > 200)
                return Failure<RemoteOperationStatus>(AdapterErrorClass.Validation, "HEPSIBURADA_SHIPPING_INFO_UPLOAD_ID_INVALID", "Hepsiburada teslimat süresi yükleme numarası geçersiz.", HttpStatusCode.BadRequest);
            var shippingResponse = await SendAsync(account, account.ListingBaseAddress, HttpMethod.Get, ShippingInfoUploadStatus(account, shippingUploadId), cancellationToken);
            using var shippingResponseDocument = shippingResponse.Value;
            if (!shippingResponse.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(shippingResponse.Error!, shippingResponse.RateLimit);
            try { return AdapterResult<RemoteOperationStatus>.Success(HepsiburadaJsonMapper.InventoryUploadStatus(shippingResponse.Value!.RootElement, shippingUploadId, ShippingInfoUploadPrefix), shippingResponse.RateLimit); }
            catch (JsonException)
            {
                return Failure<RemoteOperationStatus>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_SHIPPING_INFO_STATUS_CONTRACT_INVALID", "Hepsiburada teslimat süresi güncelleme durumu beklenen sözleşmeyle eşleşmiyor.", HttpStatusCode.BadGateway);
            }
        }
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
            using var statusResponseDocument = statusResponse.Value;
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
            using var responseDocument = response.Value;
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
            catch (JsonException exception)
            {
                return Failure<RemoteOperationStatus>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID", $"Hepsiburada ürün import durum yanıtı beklenen sözleşmeyle eşleşmiyor: {exception.Message} ({HepsiburadaJsonMapper.ProductImportStatusShape(response.Value!.RootElement)})", HttpStatusCode.BadGateway);
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
    internal static string ProductImportUpload() => "api/products/import?version=1";
    internal static string ProductImportStatus(string trackingId, int page, int size) => $"api/products/status/{Uri.EscapeDataString(trackingId)}?version=1&page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";
    internal static bool IsProductUpdatePath(string path) => path.Contains("/ticket-api/", StringComparison.OrdinalIgnoreCase);

    // C21: the mpop ticket-api schema names Bearer authentication while the panel sends Basic. A 401 there is
    // told apart from a wrong service key (the order and listing calls with the same key may work); confirm on SIT.
    internal static AdapterError ProductUpdateAuthenticationRejected(string? requestId) => new(AdapterErrorClass.Authentication, "HEPSIBURADA_PRODUCT_UPDATE_AUTH_REJECTED",
        "Hepsiburada ürün güncelleme servisi (ticket-api) Basic kimliği reddetti (401). Servis Bearer token bekliyor olabilir; sipariş ve listeleme kimliği bundan etkilenmez.", 401, null, requestId);

    internal static string ProductUpdateUpload() => "/ticket-api/api/integrator/import?version=1";
    internal static string ProductUpdateStatus(string trackingId, int page, int size) => $"/ticket-api/api/integrator/status/{Uri.EscapeDataString(trackingId)}?version=1&page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";
    // Listing Teslimat Güncelleme (uploadShippingInfo) and its status read (getShippingInfoUpload).
    internal const string ShippingInfoUploadPrefix = "LISTING_SHIPPING:";
    internal static string ShippingInfoUpload(HepsiburadaRequestContext context) => $"listings/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/shipping-info-uploads";
    internal static string ShippingInfoUploadStatus(HepsiburadaRequestContext context, string uploadId) => $"listings/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/shipping-info-uploads/id/{Uri.EscapeDataString(uploadId)}";
    // {"merchantId","items":[{hepsiburadaSku, merchantSku, dispatchTime}]}: both SKUs, a whole dispatch time of at least 0
    // days and no other field (a field left out keeps Hepsiburada's value; none is sent empty).
    internal static bool ValidShippingInfoPayload(string payloadJson, string merchantId)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("merchantId", out var merchant) || merchant.ValueKind != JsonValueKind.String
                || !string.Equals(merchant.GetString(), merchantId, StringComparison.Ordinal)
                || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() < 1 || items.GetArrayLength() > MaxInventoryUploadItems) return false;
            var merchantSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hbSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(property => property.Name is not ("hepsiburadaSku" or "merchantSku" or "dispatchTime"))) return false;
                var merchantSku = item.TryGetProperty("merchantSku", out var merchantSkuValue) && merchantSkuValue.ValueKind == JsonValueKind.String ? merchantSkuValue.GetString() : null;
                var hbSku = item.TryGetProperty("hepsiburadaSku", out var hbSkuValue) && hbSkuValue.ValueKind == JsonValueKind.String ? hbSkuValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(merchantSku) || merchantSku != merchantSku.Trim() || merchantSku.Any(char.IsWhiteSpace)
                    || string.IsNullOrWhiteSpace(hbSku) || hbSku != hbSku.Trim() || hbSku.Any(char.IsWhiteSpace)
                    || !merchantSkus.Add(merchantSku) || !hbSkus.Add(hbSku)) return false;
                if (!item.TryGetProperty("dispatchTime", out var dispatchValue) || dispatchValue.ValueKind != JsonValueKind.Number || !dispatchValue.TryGetInt32(out var dispatchTime) || dispatchTime < 0) return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    // The request body: the items alone, as the JSON array the endpoint takes.
    internal static string BuildShippingInfoUploadJson(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var items = document.RootElement.GetProperty("items").EnumerateArray().Select(item => new Dictionary<string, object>
        {
            ["hepsiburadaSku"] = item.GetProperty("hepsiburadaSku").GetString()!,
            ["merchantSku"] = item.GetProperty("merchantSku").GetString()!,
            ["dispatchTime"] = item.GetProperty("dispatchTime").GetInt32()
        }).ToList();
        return JsonSerializer.Serialize(items);
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
                if (!attributes.TryGetProperty("Barcode", out var barcodeValue) || barcodeValue.ValueKind != JsonValueKind.String || !MarketplaceHub.Infrastructure.Persistence.HepsiburadaProductPublicationComposer.IsValidBarcode(barcodeValue.GetString()) || !barcodes.Add(barcodeValue.GetString()!)) return false;
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
}
