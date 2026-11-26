using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient
{
    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<RemoteOperationRef>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        if (!await CanWriteAsync(shop, context, MarketplaceCapabilities.ProductWrite, MarketplaceCapabilities.InventoryWrite, cancellationToken)
            || !await CanWriteAsync(shop, context, MarketplaceCapabilities.PublicationRead, cancellationToken)
            || !await CanWriteAsync(shop, context, MarketplaceCapabilities.PublicationWrite, cancellationToken)) return WriteClosed<RemoteOperationRef>();

        JsonObject input;
        try
        {
            input = JsonNode.Parse(publication.PayloadJson) as JsonObject
                ?? throw new JsonException("ProductSet payload must be an object.");
        }
        catch (JsonException)
        {
            return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_PRODUCT_PAYLOAD_INVALID", "Shopify ürün yayını geçerli JSON içermiyor.", HttpStatusCode.BadRequest);
        }
        var publishOnly = input.Remove(PublishOnlyKey);
        var updateOnly = input.Remove(UpdateOnlyKey) || publishOnly;

        var handle = input["handle"]?.GetValue<string>()?.Trim();
        var variants = input["variants"] as JsonArray;
        if (string.IsNullOrWhiteSpace(handle) || variants is null || variants.Count == 0 || variants.Count > 250)
            return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_PRODUCT_PAYLOAD_INVALID", "Shopify ürün yayını benzersiz handle ve 1-250 varyant içermelidir.", HttpStatusCode.BadRequest);
        var requestedSkus = variants.Select(variant => variant?["sku"]?.GetValue<string>()?.Trim()).ToArray();
        if (requestedSkus.Any(string.IsNullOrWhiteSpace) || requestedSkus.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requestedSkus.Length)
            return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_PRODUCT_SKUS_INVALID", "Shopify ürün yayınındaki varyant SKU değerleri boş veya yinelenen olamaz.", HttpStatusCode.BadRequest);
        var requestedFileCount = (input["files"] as JsonArray)?.Count ?? 0;
        // The product's media is read only as far as the files sent: a query for 250 would cost as much as the rest.
        var mediaFirst = Math.Clamp(requestedFileCount + 1, 1, 250);

        // A model family's product is owned by its family key, a single product by its product id.
        var requestedFamilyKey = RequestedMetafield(input, "ravencia_family_key");
        var ownerKey = requestedFamilyKey is null ? "ravencia_product_id" : "ravencia_family_key";
        var requestedOwner = requestedFamilyKey ?? RequestedMetafield(input, "ravencia_product_id");
        var requestedPayloadHash = RequestedMetafield(input, "payload_hash");
        IReadOnlyList<RemoteOperationLine>? unchangedLines = null;
        string? unchangedProductId = null;
        var existingProductResult = await QueryAsync(shop, ExistingProductQuery, new { handle, ownerKey, mediaFirst }, cancellationToken);
        if (!existingProductResult.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(existingProductResult.Error!, existingProductResult.RateLimit);
        try
        {
            var existingProduct = existingProductResult.Value!.RootElement.GetProperty("productByIdentifier");
            if (updateOnly && existingProduct.ValueKind != JsonValueKind.Object)
                return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, UpdateTargetMissingCode, $"Shopify'da panelin bu ürüne verdiği adresle ({handle}) bir ürün yok; güncelleme yeni ürün açmaz. Ürün Shopify'dan silindiyse veya hiç gönderilmediyse Yayınla ile gönderin.", HttpStatusCode.UnprocessableEntity);
            if (existingProduct.ValueKind == JsonValueKind.Object)
            {
                var ownerId = existingProduct.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
                    ? TextValue(owner, "value")
                    : null;
                if (string.IsNullOrWhiteSpace(ownerId) || !string.Equals(ownerId, requestedOwner, StringComparison.OrdinalIgnoreCase))
                    return Fail<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, "SHOPIFY_PRODUCT_HANDLE_CONFLICT", "Bu Shopify ürün adresi başka bir kayıt tarafından kullanılıyor; mevcut ürün değiştirilmedi.", HttpStatusCode.Conflict);
                // B-10: a product with more variants than one read holds is neither compared nor written: productSet takes
                // at most 250 variants and would delete the ones this read never saw.
                if (HasNextPage(existingProduct.GetProperty("variants")))
                    return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, VariantListIncompleteCode, $"Shopify'daki {handle} ürününde 250'den fazla varyant var; varyant listesi tam okunamadığı için ürüne yazılmadı.", HttpStatusCode.UnprocessableEntity);
                // Another card of the family already wrote this exact content: the product is not written again,
                // but its Online Store publication below still runs, since that earlier write may have stopped there.
                // A product whose pictures did not all arrive is written again.
                unchangedLines = MediaShortfall(existingProduct, requestedFileCount) is null
                    ? UnchangedPublicationLines(existingProduct, requestedPayloadHash, requestedSkus!)
                    : null;
                // A publish-only resend never writes the product: it takes the variants the product already has.
                if (publishOnly) unchangedLines = PublicationLines(existingProduct, requestedSkus!);
                unchangedProductId = unchangedLines is null ? null : TextValue(existingProduct, "id");
                if (string.IsNullOrWhiteSpace(unchangedProductId)) unchangedLines = null;
                if (publishOnly && unchangedLines is null)
                    return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, PublishOnlyMismatchCode, $"Shopify'daki {handle} ürünü bu yayının varyantlarını taşımıyor veya etkin değil; yalnız yayın adımı yapılamadı ve ürüne yazılmadı. Kartı Platformlarda Güncelle ile gönderin.", HttpStatusCode.UnprocessableEntity);
            }
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "SHOPIFY_PRODUCT_LOOKUP_CONTRACT_INVALID", "Shopify ürün adresi kontrolü beklenen alanları içermiyor; ürün oluşturulmadı.", HttpStatusCode.BadGateway);
        }

        var locationsResult = await QueryAsync(shop, "query { locations(first:250) { nodes { id isActive } pageInfo { hasNextPage } } }", cancellationToken: cancellationToken);
        if (!locationsResult.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(locationsResult.Error!, locationsResult.RateLimit);
        JsonElement activeLocation;
        try
        {
            var locationList = locationsResult.Value!.RootElement.GetProperty("locations");
            if (locationList.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())
                return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_LOCATION_LIST_INCOMPLETE", "Shopify depo listesi tek bir etkin depo doğrulanamayacak kadar büyük.", HttpStatusCode.UnprocessableEntity);
            var activeLocations = locationList.GetProperty("nodes").EnumerateArray()
                .Where(location => location.GetProperty("isActive").GetBoolean())
                .ToArray();
            if (activeLocations.Length != 1)
                return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_INVENTORY_LOCATION_AMBIGUOUS", "Ürün ve stok yayını için Shopify mağazasında tam bir etkin depo olmalıdır.", HttpStatusCode.UnprocessableEntity);
            activeLocation = activeLocations[0].Clone();
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "SHOPIFY_LOCATION_CONTRACT_INVALID", "Shopify etkin depo yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
        var locationId = TextValue(activeLocation, "id");
        if (string.IsNullOrWhiteSpace(locationId))
            return Fail<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "SHOPIFY_LOCATION_CONTRACT_INVALID", "Shopify etkin depo kimliği boş döndü.", HttpStatusCode.BadGateway);

        var publicationsResult = await OnlineStorePublicationIdAsync(shop, cancellationToken);
        if (!publicationsResult.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(publicationsResult.Error!, publicationsResult.RateLimit);
        var publicationId = publicationsResult.Value!;

        foreach (var variant in variants)
        {
            if (variant is not JsonObject variantObject || variantObject["inventoryQuantity"] is not JsonValue quantityValue
                || !quantityValue.TryGetValue<int>(out var quantity) || quantity < 0)
                return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_INVENTORY_QUANTITY_INVALID", "Shopify varyant stoku geçersiz veya negatif olamaz.", HttpStatusCode.BadRequest);
            variantObject.Remove("inventoryQuantity");
            variantObject["inventoryQuantities"] = new JsonArray(new JsonObject
            {
                ["locationId"] = locationId,
                ["name"] = "available",
                ["quantity"] = quantity
            });
        }

        string productId;
        IReadOnlyList<RemoteOperationLine> lines;
        AdapterResult<JsonDocument>? productSet = null;
        string? mediaShortfall = null;
        if (unchangedLines is not null)
        {
            productId = unchangedProductId!;
            lines = unchangedLines;
        }
        else
        {
            productSet = await QueryAsync(shop, ProductSetMutation, new { identifier = new { handle }, input, mediaFirst }, cancellationToken);
            if (!productSet.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(productSet.Error!, productSet.RateLimit);
            try
            {
                var productSetPayload = productSet.Value!.RootElement.GetProperty("productSet");
                if (HasUserErrors(productSetPayload, out var productError))
                    return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_PRODUCT_CREATE_REJECTED", productError ?? "Shopify ürünü oluşturmayı reddetti.", HttpStatusCode.UnprocessableEntity);
                var product = productSetPayload.GetProperty("product");
                productId = TextValue(product, "id") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(productId) || !string.Equals(TextValue(product, "status"), "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    return Fail<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "SHOPIFY_PRODUCT_CREATE_CONTRACT_INVALID", "Shopify ürün yanıtında etkin ürün kimliği bulunamadı.", HttpStatusCode.BadGateway);
                var remoteVariants = product.GetProperty("variants").GetProperty("nodes").EnumerateArray()
                    .Select(variant => new { Id = TextValue(variant, "id"), Sku = TextValue(variant, "sku")?.Trim() })
                    .ToArray();
                // A variant list with a further page is not the whole product: it never passes as matching.
                if (HasNextPage(product.GetProperty("variants"))
                    || remoteVariants.Length != requestedSkus.Length
                    || remoteVariants.Any(variant => string.IsNullOrWhiteSpace(variant.Id) || string.IsNullOrWhiteSpace(variant.Sku))
                    || remoteVariants.Select(variant => variant.Sku).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requestedSkus.Length
                    || requestedSkus.Any(sku => !remoteVariants.Any(variant => string.Equals(variant.Sku, sku, StringComparison.OrdinalIgnoreCase))))
                    return Fail<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "SHOPIFY_PRODUCT_VARIANT_CONTRACT_INVALID", "Shopify yanıtındaki varyant SKU listesi gönderilen ürünle eşleşmiyor.", HttpStatusCode.BadGateway);
                lines = remoteVariants.Select(variant => new RemoteOperationLine(variant.Sku!, true, variant.Id, null, false, "COMPLETED")).ToArray();
                mediaShortfall = MediaShortfall(product, requestedFileCount);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
            {
                return Fail<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "SHOPIFY_PRODUCT_CREATE_CONTRACT_INVALID", "Shopify ürün oluşturma yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
            }
        }

        var publish = await QueryAsync(shop, PublishMutation, new { id = productId, input = new[] { new { publicationId } } }, cancellationToken);
        if (PublishFailure(publish, productId, handle) is { } publishFailure)
            return AdapterResult<RemoteOperationRef>.Failure(publishFailure, publish.RateLimit);
        // The product is written and published, so its variants are recorded as live; a picture that did not arrive is not
        // swallowed: it rides on the result and the job ends with it. The next publication writes the product again (the
        // existing product check sees the same shortfall).
        var operationId = EncodeCompletedOperation(lines, mediaShortfall is null ? null
            : $"Shopify ürünü {productId} (handle {handle}) yazıldı ve yayınlandı, ancak görseller eksik: {mediaShortfall}. Görseller düzeltildikten sonra Platformlarda Güncelle ile yeniden gönderin.");
        return AdapterResult<RemoteOperationRef>.Success(new(operationId, "SHOPIFY_PRODUCT_CREATE", timeProvider.GetUtcNow()), publish.RateLimit ?? productSet?.RateLimit ?? publicationsResult.RateLimit ?? locationsResult.RateLimit);
    }

    // publishable is the Publishable interface, which has no id of its own: the product's id is read through the Product
    // type. Asking publishable{id} fails the whole operation in GraphQL validation, so nothing was ever published (live
    // 2026-10-27/28: every Shopify publication ended "Online Store yayını doğrulanamadı").
    internal const string PublishMutation = "mutation($id:ID!,$input:[PublicationInput!]!){publishablePublish(id:$id,input:$input){publishable{... on Product{id}}userErrors{field message}}}";

    // The product is written (created or updated under its handle) by the time the Online Store publication runs, so a
    // publish that fails leaves no doubt about the product: it is not ambiguous and is never created again. A retry
    // finds the product by its handle with the same payload hash, skips productSet and repeats only the publication,
    // which Shopify applies once (publishing an already published product changes nothing).
    internal const string MediaIncompleteCode = "SHOPIFY_PRODUCT_MEDIA_INCOMPLETE";
    internal const string MediaSelection = "media(first:$mediaFirst){nodes{status mediaErrors{message}}}";
    // Both reads of a product's variants ask whether more follow (B-10): a list cut at 250 is never taken as the product's.
    internal const string ExistingProductQuery = "query($handle:String!,$ownerKey:String!,$mediaFirst:Int!){productByIdentifier(identifier:{handle:$handle}){id status owner:metafield(namespace:\"marketplacehub\",key:$ownerKey){value} payloadHash:metafield(namespace:\"marketplacehub\",key:\"payload_hash\"){value} " + MediaSelection + " variants(first:250){nodes{id sku}pageInfo{hasNextPage}}}}";
    internal const string ProductSetMutation = "mutation($identifier:ProductSetIdentifiers!,$input:ProductSetInput!,$mediaFirst:Int!){productSet(identifier:$identifier,input:$input,synchronous:true){product{id status " + MediaSelection + " variants(first:250){nodes{id sku}pageInfo{hasNextPage}}}userErrors{field message}}}";
    internal const string VariantListIncompleteCode = "SHOPIFY_PRODUCT_VARIANT_LIST_INCOMPLETE";

    // Shopify answers productSet with the media it created from the files (status UPLOADED while it fetches them): fewer
    // than sent, or one FAILED or carrying errors, is a shortfall. Null when every file arrived.
    internal static string? MediaShortfall(JsonElement product, int requestedFileCount)
    {
        if (!product.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object)
            return requestedFileCount == 0 ? null : $"Shopify {requestedFileCount} görselin hiçbirini bildirmedi";
        var nodes = media.GetProperty("nodes").EnumerateArray().ToArray();
        var errors = nodes
            .SelectMany(node => node.TryGetProperty("mediaErrors", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(error => TextValue(error, "message")) : [])
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var failed = nodes.Count(node => string.Equals(TextValue(node, "status"), "FAILED", StringComparison.OrdinalIgnoreCase));
        if (nodes.Length >= requestedFileCount && failed == 0 && errors.Count == 0) return null;
        var parts = new List<string>();
        if (nodes.Length < requestedFileCount) parts.Add($"{requestedFileCount} görselden {nodes.Length} tanesi eklendi");
        if (failed > 0) parts.Add($"{failed} görsel işlenemedi");
        if (errors.Count > 0) parts.Add("Shopify: " + string.Join("; ", errors.Take(3)));
        return string.Join(", ", parts);
    }

    internal const string PublishRetryCode = "SHOPIFY_PRODUCT_PUBLISH_RETRY";
    // "Yayına alınamadı": the product is written, its Online Store publication is not.
    internal const string PublishFailedCode = "SHOPIFY_PRODUCT_CREATED_BUT_PUBLISH_FAILED";
    internal const string PublishFailedLabel = "Yayına alınamadı";

    // A job whose earlier attempt wrote the product and stopped at the publication is resent with this key set: the product
    // is looked up under its handle and only published, never written or created (MarketplaceJobProcessor). The key is taken
    // out before the request goes to Shopify.
    internal const string PublishOnlyKey = "ravenciaPublishOnly";
    internal const string PublishOnlyMismatchCode = "SHOPIFY_PUBLISH_ONLY_PRODUCT_MISMATCH";

    internal static string MarkPublishOnly(string payloadJson)
    {
        var input = JsonNode.Parse(payloadJson)!.AsObject();
        input[PublishOnlyKey] = true;
        return input.ToJsonString();
    }

    internal static AdapterError? PublishFailure(AdapterResult<JsonDocument> publish, string productId, string handle)
    {
        var product = $"Shopify ürünü {productId} (handle {handle}) oluşturuldu/güncellendi";
        if (!publish.IsSuccess)
        {
            var error = publish.Error!;
            var retry = error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.RateLimit;
            return new(retry ? AdapterErrorClass.TransientNetwork : AdapterErrorClass.Validation,
                retry ? PublishRetryCode : PublishFailedCode,
                $"{(retry ? "" : PublishFailedLabel + ": ")}{product} ancak Online Store yayını yapılamadı: {error.Code}: {error.SafeMessage}. {(retry ? "Yalnızca yayın adımı yeniden denenecek." : "İşi Yeniden Dene ile tekrarlayın; ürün yeniden oluşturulmaz ve yazılmaz, yalnızca yayın adımı tekrarlanır.")}",
                error.HttpStatus, error.RetryAfter, error.RemoteRequestId);
        }
        try
        {
            var payload = publish.Value!.RootElement.GetProperty("publishablePublish");
            if (HasUserErrors(payload, out var publishError))
                return new(AdapterErrorClass.Validation, PublishFailedCode,
                    $"{PublishFailedLabel}: {product} ancak Shopify Online Store yayınını reddetti: {publishError ?? "Shopify neden bildirmedi"}. Sorunu Shopify'da giderip işi Yeniden Dene ile tekrarlayın; ürün yeniden oluşturulmaz ve yazılmaz, yalnızca yayın adımı tekrarlanır.",
                    (int)HttpStatusCode.UnprocessableEntity, null, null);
            if (!string.Equals(TextValue(payload.GetProperty("publishable"), "id"), productId, StringComparison.Ordinal))
                throw new JsonException("The published resource is not the product.");
            return null;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return new(AdapterErrorClass.TransientNetwork, PublishRetryCode,
                $"{product} ancak Online Store yayın yanıtı doğrulanamadı. Yalnızca yayın adımı yeniden denenecek.",
                (int)HttpStatusCode.BadGateway, null, null);
        }
    }
    // "Platformlarda Güncelle" sends the publication's productSet with this key set: it writes the product the panel already
    // holds under its handle and never creates one. The key is taken out before the request goes to Shopify.
    internal const string UpdateOnlyKey = "ravenciaUpdateOnly";
    internal const string UpdateTargetMissingCode = "SHOPIFY_PRODUCT_NOT_FOUND";

    internal static string MarkUpdateOnly(string payloadJson)
    {
        var input = JsonNode.Parse(payloadJson)!.AsObject();
        input[UpdateOnlyKey] = true;
        return input.ToJsonString();
    }

    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        try
        {
            if (!externalOperationId.StartsWith("shopify-completed:", StringComparison.Ordinal)) return Unsupported<RemoteOperationStatus>("Shopify operation kimliği tanınmıyor.");
            var parts = externalOperationId["shopify-completed:".Length..].Split('|');
            var lines = JsonSerializer.Deserialize<List<RemoteOperationLine>>(Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])));
            if (lines is null) return Unsupported<RemoteOperationStatus>("Shopify operation sonucu okunamadı.");
            var mediaIssue = parts.Length > 1 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])) : null;
            return Task.FromResult(AdapterResult<RemoteOperationStatus>.Success(new(externalOperationId, "COMPLETED", lines, IssueCode: mediaIssue is null ? null : MediaIncompleteCode, IssueSummary: mediaIssue)));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException) { return Unsupported<RemoteOperationStatus>("Shopify operation sonucu geçersiz."); }
    }
    // The publication state of one SKU: NOT_FOUND when no variant of the store carries it, APPROVED when an
    // active product does, PENDING_APPROVAL for a draft, ARCHIVED for an archived product, UNKNOWN otherwise.
    public async Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        var sku = barcode.Trim();
        if (sku.Length == 0) return Fail<RemotePublicationStatus>(AdapterErrorClass.Validation, "SHOPIFY_SKU_REQUIRED", "Shopify yayın durumu için stok kodu gerekir.", HttpStatusCode.BadRequest);
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<RemotePublicationStatus>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        var publication = await OnlineStorePublicationIdAsync(shop, cancellationToken);
        if (!publication.IsSuccess) return AdapterResult<RemotePublicationStatus>.Failure(publication.Error!, publication.RateLimit);
        var publicationId = publication.Value!;
        var result = await QueryAsync(shop, SkuStatusQuery,
            new { query = $"sku:\"{sku.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"", publicationId }, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemotePublicationStatus>.Failure(result.Error!, result.RateLimit);
        RemotePublicationStatus status;
        string? unpublishedProductId;
        string? unpublishedHandle;
        try
        {
            var raw = result.Value!.RootElement.GetProperty("productVariants");
            if (SkuProducts(sku, raw) is { Count: > 1 } holders)
                return Fail<RemotePublicationStatus>(AdapterErrorClass.Validation, DuplicateSkuCode,
                    $"'{sku}' stok kodu Shopify'da birden çok üründe var: {string.Join(", ", holders.Select(holder => $"{holder.Id} (handle {holder.Handle ?? "?"})"))}. Ravencia model ailesinin ürünü ravencia-model- ile başlayan handle'dır; fazlalığı Shopify yönetim panelinden silip durumu yeniden sorgulayın.",
                    HttpStatusCode.Conflict);
            status = MapSkuPublicationStatus(sku, raw);
            (unpublishedProductId, unpublishedHandle) = OnlineStoreUnpublishedProduct(sku, raw);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<RemotePublicationStatus>(AdapterErrorClass.ContractViolation, "SHOPIFY_PUBLICATION_STATUS_CONTRACT_INVALID", "Shopify varyant arama yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
        if (unpublishedProductId is null || RecentlyPublished($"{shop.Shop}|{unpublishedProductId}"))
            return AdapterResult<RemotePublicationStatus>.Success(status, result.RateLimit);

        // An active product the Online Store does not show (live 2026-10-28: every Shopify product written before the
        // publish was fixed): it is published here, the same idempotent step a publication runs. Nothing is created.
        if (!await CanWriteAsync(shop, context, MarketplaceCapabilities.PublicationWrite, cancellationToken))
            return Fail<RemotePublicationStatus>(AdapterErrorClass.Validation, PublishFailedCode,
                $"Shopify ürünü {unpublishedProductId} (handle {unpublishedHandle ?? "?"}) etkin ama Online Store kanalında yayında değil; Shopify dış yazması veya write_publications izni kapalı olduğu için yayınlanamadı.",
                HttpStatusCode.Forbidden);
        var publish = await QueryAsync(shop, PublishMutation, new { id = unpublishedProductId, input = new[] { new { publicationId } } }, cancellationToken);
        if (PublishFailure(publish, unpublishedProductId, unpublishedHandle ?? "?") is { } publishFailure)
            return AdapterResult<RemotePublicationStatus>.Failure(publishFailure, publish.RateLimit);
        PublishedOnlineStoreProducts[$"{shop.Shop}|{unpublishedProductId}"] = timeProvider.GetUtcNow();
        logger.LogInformation("Shopify product {ProductId} ({Handle}) published to the Online Store by a status check of {Sku}", unpublishedProductId, unpublishedHandle, sku);
        return AdapterResult<RemotePublicationStatus>.Success(status, publish.RateLimit ?? result.RateLimit);
    }

    internal const string SkuStatusQuery = "query($query:String!,$publicationId:ID!){productVariants(first:10,query:$query){nodes{id sku product{id status handle publishedOnPublication(publicationId:$publicationId)}}}}";

    // Products this process published to the Online Store in the last minutes: the family's other SKUs, checked right
    // after, do not publish them again (Shopify may still answer from before the publication).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> PublishedOnlineStoreProducts = new(StringComparer.Ordinal);
    private bool RecentlyPublished(string key) => PublishedOnlineStoreProducts.TryGetValue(key, out var at) && timeProvider.GetUtcNow() - at < TimeSpan.FromMinutes(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> OnlineStorePublicationIds = new(StringComparer.Ordinal);

    // The active product holding this SKU when the Online Store publication does not show it; null otherwise. The
    // field is asked for, so its absence is a broken answer, never "unpublished".
    internal static (string? ProductId, string? Handle) OnlineStoreUnpublishedProduct(string sku, JsonElement productVariants)
    {
        var matches = ExactSkuMatches(sku, productVariants);
        if (matches.Length != 1) return (null, null);
        var product = matches[0].GetProperty("product");
        if (!string.Equals(TextValue(product, "status"), "ACTIVE", StringComparison.OrdinalIgnoreCase)) return (null, null);
        var published = product.GetProperty("publishedOnPublication");
        if (published.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("publishedOnPublication is not a boolean.");
        return published.GetBoolean() ? (null, null) : (TextValue(product, "id"), TextValue(product, "handle"));
    }

    // The store's single Online Store publication; kept per shop for the process.
    private async Task<AdapterResult<string>> OnlineStorePublicationIdAsync(ShopifyRequestContext shop, CancellationToken cancellationToken)
    {
        if (OnlineStorePublicationIds.TryGetValue(shop.Shop, out var known)) return AdapterResult<string>.Success(known);
        var publicationsResult = await QueryAsync(shop, "query { publications(first:100) { nodes { id channels(first:20) { nodes { name handle } } } pageInfo { hasNextPage } } }", cancellationToken: cancellationToken);
        if (!publicationsResult.IsSuccess) return AdapterResult<string>.Failure(publicationsResult.Error!, publicationsResult.RateLimit);
        try
        {
            var publications = publicationsResult.Value!.RootElement.GetProperty("publications");
            if (publications.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())
                return Fail<string>(AdapterErrorClass.Validation, "SHOPIFY_PUBLICATION_LIST_INCOMPLETE", "Shopify satış kanalı listesi tamamlanamadı; Online Store yayın kanalı bulunamadı.", HttpStatusCode.UnprocessableEntity);
            var onlineStorePublications = publications.GetProperty("nodes").EnumerateArray()
                .Where(publication => publication.GetProperty("channels").GetProperty("nodes").EnumerateArray()
                    .Any(channel => string.Equals(TextValue(channel, "name"), "Online Store", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(TextValue(channel, "handle"), "online-store", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(TextValue(channel, "handle"), "online_store", StringComparison.OrdinalIgnoreCase)))
                .Select(publication => TextValue(publication, "id"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (onlineStorePublications.Length != 1)
                return Fail<string>(AdapterErrorClass.Validation, "SHOPIFY_ONLINE_STORE_PUBLICATION_REQUIRED", "Shopify mağazasında tek bir Online Store yayın kanalı bulunamadı; ürün yayınlanmadı.", HttpStatusCode.UnprocessableEntity);
            OnlineStorePublicationIds[shop.Shop] = onlineStorePublications[0]!;
            return AdapterResult<string>.Success(onlineStorePublications[0]!, publicationsResult.RateLimit);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<string>(AdapterErrorClass.ContractViolation, "SHOPIFY_PUBLICATION_CONTRACT_INVALID", "Shopify yayın kanalı yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
    }

    internal const string DuplicateSkuCode = "SHOPIFY_SKU_IN_SEVERAL_PRODUCTS";

    // The search is a prefix-tolerant index lookup; only an exact SKU counts.
    private static JsonElement[] ExactSkuMatches(string sku, JsonElement productVariants) => productVariants.GetProperty("nodes").EnumerateArray()
        .Where(variant => string.Equals(TextValue(variant, "sku")?.Trim(), sku, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    // The products whose variants carry this exact SKU (an earlier one-card product and the family product may both).
    internal static IReadOnlyList<(string Id, string? Handle)> SkuProducts(string sku, JsonElement productVariants) => ExactSkuMatches(sku, productVariants)
        .Select(variant => variant.GetProperty("product"))
        .Select(product => (Id: TextValue(product, "id") ?? "?", Handle: TextValue(product, "handle")))
        .DistinctBy(product => product.Id, StringComparer.Ordinal)
        .ToList();

    internal static RemotePublicationStatus MapSkuPublicationStatus(string sku, JsonElement productVariants)
    {
        var matches = ExactSkuMatches(sku, productVariants);
        if (matches.Length == 0) return new(sku, "NOT_FOUND", null, null, null, productVariants.GetRawText());
        if (matches.Length > 1) throw new JsonException("Several Shopify variants carry the same SKU.");
        // A model family is one product: a card is live when its SKUs are variants of an active product, whichever card's
        // job wrote it.
        var product = matches[0].GetProperty("product");
        // ProductStatus: ACTIVE is live, DRAFT not yet published (waits like an unapproved product), ARCHIVED a closed
        // listing; any other status (UNLISTED, a new one) is unknown and goes to review.
        var status = TextValue(product, "status")?.Trim().ToUpperInvariant() switch
        {
            "ACTIVE" => "APPROVED",
            "DRAFT" => "PENDING_APPROVAL",
            "ARCHIVED" => "ARCHIVED",
            _ => "UNKNOWN"
        };
        return new(sku, status, TextValue(product, "id"), TextValue(matches[0], "id"), null, matches[0].GetRawText());
    }
    public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    // An issue rides after a | (no base64 character), so an operation id written before it still reads.
    internal static string EncodeCompletedOperation(IReadOnlyList<RemoteOperationLine> lines, string? mediaIssue = null) =>
        "shopify-completed:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(lines))
        + (mediaIssue is null ? "" : "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(mediaIssue)));

    internal static string? RequestedMetafield(JsonObject input, string key) => input["metafields"] is JsonArray metafields
        ? metafields.Where(item => string.Equals(item?["key"]?.GetValue<string>(), key, StringComparison.Ordinal))
            .Select(item => item?["value"]?.GetValue<string>())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        : null;

    // The existing product already holds this payload (same hash, active, the same SKUs): its variants are the
    // result, and nothing is written. Null when anything differs, so the product is written as usual.
    internal static IReadOnlyList<RemoteOperationLine>? UnchangedPublicationLines(JsonElement existingProduct, string? requestedPayloadHash, IReadOnlyList<string> requestedSkus)
    {
        if (string.IsNullOrWhiteSpace(requestedPayloadHash)
            || !existingProduct.TryGetProperty("payloadHash", out var hash) || hash.ValueKind != JsonValueKind.Object
            || !string.Equals(TextValue(hash, "value"), requestedPayloadHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(TextValue(existingProduct, "status"), "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return null;
        return PublicationLines(existingProduct, requestedSkus);
    }

    // The lines of an active product that carries exactly the requested SKUs, one variant each; null otherwise.
    internal static IReadOnlyList<RemoteOperationLine>? PublicationLines(JsonElement existingProduct, IReadOnlyList<string> requestedSkus)
    {
        if (!string.Equals(TextValue(existingProduct, "status"), "ACTIVE", StringComparison.OrdinalIgnoreCase)) return null;
        var remoteVariants = existingProduct.GetProperty("variants").GetProperty("nodes").EnumerateArray()
            .Select(variant => (Id: TextValue(variant, "id"), Sku: TextValue(variant, "sku")?.Trim()))
            .ToArray();
        if (remoteVariants.Length != requestedSkus.Count
            || remoteVariants.Any(variant => string.IsNullOrWhiteSpace(variant.Id) || string.IsNullOrWhiteSpace(variant.Sku))
            || !requestedSkus.All(sku => remoteVariants.Count(variant => string.Equals(variant.Sku, sku, StringComparison.OrdinalIgnoreCase)) == 1))
            return null;
        return remoteVariants.Select(variant => new RemoteOperationLine(variant.Sku!, true, variant.Id, null, false, "COMPLETED")).ToArray();
    }
}
