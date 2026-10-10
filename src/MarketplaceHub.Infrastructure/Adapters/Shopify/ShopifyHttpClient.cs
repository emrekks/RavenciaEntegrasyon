using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed class ShopifyHttpClient(
    IHttpClientFactory clients,
    ShopifyAuthenticationHandler authentication,
    IOptions<ShopifyOptions> options,
    TimeProvider timeProvider,
    ILogger<ShopifyHttpClient> logger)
    : IConnectionPort, IReferenceDataPort, IProductPort, IProductVisualLookupPort, IInventoryPricePort, IOrderPort, IReturnPort
{
    private readonly ShopifyOptions settings = options.Value;

    // Shopify can reject protected customer fields for an otherwise valid
    // order token. Keep the order/status sync useful in that case: try the
    // rich customer projection first, then a name/address projection, and
    // finally the order-only projection. A token with read_customers still
    // gets the full customer snapshot without any special configuration.
    private const string OrderIdentityFields = "id name createdAt updatedAt cancelledAt currencyCode displayFinancialStatus displayFulfillmentStatus";
    private const string OrderRichCustomerFields = " email phone customer { id displayName firstName lastName email phone } shippingAddress { firstName lastName name company address1 address2 city province provinceCode zip country phone } billingAddress { firstName lastName name company address1 address2 city province provinceCode zip country phone }";
    private const string OrderReducedCustomerFields = " customer { id displayName firstName lastName } shippingAddress { firstName lastName name company address1 address2 city province provinceCode zip country } billingAddress { firstName lastName name company address1 address2 city province provinceCode zip country }";
    private const string OrderAddressOnlyFields = " shippingAddress { firstName lastName name } billingAddress { firstName lastName name }";
    private const string OrderFinancialFields = " currentTotalPriceSet { shopMoney { amount currencyCode } } totalDiscountsSet { shopMoney { amount currencyCode } } lineItems(first:250) { nodes { id name sku quantity currentQuantity originalUnitPriceSet { shopMoney { amount currencyCode } } variant { sku barcode } } } fulfillments(first:50) { id status displayStatus deliveredAt createdAt updatedAt trackingInfo { number company url } events(first:50) { nodes { status happenedAt } } fulfillmentLineItems(first:250) { nodes { id quantity lineItem { id } } } } refunds(first:100) { id createdAt totalRefundedSet { shopMoney { amount currencyCode } } }";
    private const string TaxonomyCategoryFields = "nodes { id name fullName parentId level isLeaf isArchived } pageInfo { hasNextPage endCursor }";
    private const string TaxonomyRootsQuery = "query($first:Int!, $after:String) { taxonomy { categories(first:$first, after:$after) { " + TaxonomyCategoryFields + " } } }";
    private const string TaxonomyDescendantsQuery = "query($first:Int!, $after:String, $rootId:ID!) { taxonomy { categories(first:$first, after:$after, descendantsOf:$rootId) { " + TaxonomyCategoryFields + " } } }";
    private const string GrantedScopesQuery = "query { currentAppInstallation { accessScopes { handle } } }";
    private const string FindVariantForWriteQuery = "query($first:Int!, $query:String!) { productVariants(first:$first, query:$query) { nodes { id barcode product { id } inventoryItem { id tracked inventoryLevels(first:250) { nodes { location { id isActive } quantities(names:[\"available\"]) { name quantity } } pageInfo { hasNextPage } } } } pageInfo { hasNextPage } } }";
    private const string UpdateVariantPricesMutation = "mutation UpdateVariantPrices($productId:ID!, $variants:[ProductVariantsBulkInput!]!) { productVariantsBulkUpdate(productId:$productId, variants:$variants) { userErrors { field message } } }";
    private const string SetInventoryQuantitiesMutation = "mutation SetInventoryQuantities($input:InventorySetQuantitiesInput!, $idempotencyKey:String!) { inventorySetQuantities(input:$input) @idempotent(key:$idempotencyKey) { userErrors { field message } } }";

    private static string OrderFields(string customerFields) => $"{OrderIdentityFields}{customerFields}{OrderFinancialFields}";
    private static string OrderPageQuery(string fields) => "query($first:Int!, $after:String, $query:String) { orders(first:$first, after:$after, query:$query, sortKey:UPDATED_AT, reverse:false) { edges { cursor node { " + fields + " } } pageInfo { hasNextPage endCursor } } }";
    private static string SingleOrderQuery(string fields) => "query($id:ID!) { order(id:$id) { " + fields + " } }";

    private async Task<AdapterResult<JsonDocument>> QueryOrderAsync(ShopifyRequestContext context, object variables, bool single, CancellationToken cancellationToken)
    {
        var richQuery = single ? SingleOrderQuery(OrderFields(OrderRichCustomerFields)) : OrderPageQuery(OrderFields(OrderRichCustomerFields));
        var result = await QueryAsync(context, richQuery, variables, cancellationToken);
        if (result.IsSuccess || result.Error?.Code != "SHOPIFY_REQUIRED_READ_SCOPE") return result;

        var reducedQuery = single ? SingleOrderQuery(OrderFields(OrderReducedCustomerFields)) : OrderPageQuery(OrderFields(OrderReducedCustomerFields));
        result = await QueryAsync(context, reducedQuery, variables, cancellationToken);
        if (result.IsSuccess || result.Error?.Code != "SHOPIFY_REQUIRED_READ_SCOPE") return result;

        var addressOnlyQuery = single ? SingleOrderQuery(OrderFields(OrderAddressOnlyFields)) : OrderPageQuery(OrderFields(OrderAddressOnlyFields));
        result = await QueryAsync(context, addressOnlyQuery, variables, cancellationToken);
        if (result.IsSuccess || result.Error?.Code != "SHOPIFY_REQUIRED_READ_SCOPE") return result;

        var minimalQuery = single ? SingleOrderQuery(OrderFields(string.Empty)) : OrderPageQuery(OrderFields(string.Empty));
        return await QueryAsync(context, minimalQuery, variables, cancellationToken);
    }

    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return Fail<ConnectionIdentity>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify mağaza bilgileri veya yetkilendirmesi geçersiz.", HttpStatusCode.Unauthorized);
        var result = await QueryAsync(shop, "query { shop { name primaryDomain { host } currencyCode } locations(first: 50) { nodes { id name isActive } } }", cancellationToken: cancellationToken);
        if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        try
        {
            var root = result.Value!.RootElement;
            if (!root.TryGetProperty("shop", out var shopElement) || shopElement.ValueKind != JsonValueKind.Object)
                return Fail<ConnectionIdentity>(AdapterErrorClass.ContractViolation, "SHOPIFY_CONNECTION_CONTRACT_INVALID", "Shopify mağaza doğrulama yanıtı mağaza bilgisini içermiyor.", HttpStatusCode.BadGateway);
            var host = shopElement.TryGetProperty("primaryDomain", out var primaryDomain)
                && primaryDomain.ValueKind == JsonValueKind.Object
                && primaryDomain.TryGetProperty("host", out var hostElement)
                && hostElement.ValueKind == JsonValueKind.String
                ? hostElement.GetString()
                : null;
            host = string.IsNullOrWhiteSpace(host) ? $"{shop.Shop}.myshopify.com" : host;
            var locationCount = root.TryGetProperty("locations", out var locations)
                && locations.ValueKind == JsonValueKind.Object
                && locations.TryGetProperty("nodes", out var nodes)
                && nodes.ValueKind == JsonValueKind.Array
                ? nodes.GetArrayLength()
                : 0;
            return AdapterResult<ConnectionIdentity>.Success(new("SHOPIFY", shop.Connection.Environment, shop.Shop, shop.ApiVersion, $"{host}:{locationCount}"), result.RateLimit);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<ConnectionIdentity>(AdapterErrorClass.ContractViolation, "SHOPIFY_CONNECTION_CONTRACT_INVALID", "Shopify mağaza doğrulama yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var test = await TestAsync(context, cancellationToken);
        if (!test.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(test.Error!, test.RateLimit);
        var identity = test.Value!;
        var now = timeProvider.GetUtcNow();
        var products = await ListCatalogAsync(context, new(null, 1), new(null), cancellationToken);
        var orders = await PollAsync(context, new OrderPollWindow(null, now, null), new(null, 1), cancellationToken);
        var categories = await ReadAsync(context, new("CATEGORIES", null), new(null, 1), cancellationToken);
        var shopContext = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shopContext is null) return Fail<IReadOnlyList<CapabilityEvidence>>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized);
        var scopeResult = await QueryAsync(shopContext, GrantedScopesQuery, cancellationToken: cancellationToken);
        var evidence = new List<CapabilityEvidence>
        {
            Supported(MarketplaceCapabilities.ConnectionTest, identity, "https://shopify.dev/docs/api/admin-graphql", "Shopify mağaza, para birimi ve depo bilgileri doğrulandı.", now)
        };
        evidence.Add(ShopifyScopeEvidence(identity, scopeResult, now));
        evidence.Add(ShopifyWriteCapabilityEvidence(MarketplaceCapabilities.PriceWrite, identity, scopeResult, "write_products", "Varyant satış ve liste fiyatı yazması", now));
        evidence.Add(ShopifyWriteCapabilityEvidence(MarketplaceCapabilities.InventoryWrite, identity, scopeResult, "write_inventory", "Shopify stok miktarı yazması; her barkod için tek aktif stok lokasyonu gerekir", now));
        evidence.Add(Probe(MarketplaceCapabilities.ProductRead, identity, "https://shopify.dev/docs/api/admin-graphql/latest/objects/Product", products, "GraphQL ürün ve varyant okuması", now, "read_products,read_inventory"));
        evidence.Add(Probe(MarketplaceCapabilities.OrderRead, identity, "https://shopify.dev/docs/api/admin-graphql/latest/objects/Order", orders, "GraphQL sipariş, müşteri ve teslimat durumu okuması", now, "read_orders,read_customers"));
        evidence.Add(Probe(MarketplaceCapabilities.ReferenceRead, identity, "https://shopify.dev/docs/api/admin-graphql/latest/queries/taxonomy", categories, "GraphQL ürün taksonomisi kategori okuması", now, "read_products"));
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence, products.RateLimit ?? orders.RateLimit ?? categories.RateLimit);
    }

    private static CapabilityEvidence ShopifyScopeEvidence(ConnectionIdentity identity, AdapterResult<JsonDocument> result, DateTimeOffset verifiedAt)
    {
        const string source = "https://shopify.dev/docs/apps/build/authentication-authorization/manage-access-scopes";
        if (!result.IsSuccess)
            return new(MarketplaceCapabilities.ShopifyAppScopes, "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, null, null,
                $"Shopify uygulama izinleri okunamadı: {result.Error?.SafeMessage ?? "Shopify yanıtı alınamadı."}", null, verifiedAt);

        try
        {
            var scopes = MapGrantedScopes(result.Value!.RootElement);
            var details = JsonSerializer.Serialize(new { grantedScopes = scopes });
            var note = scopes.Count == 0
                ? "Shopify tokenında kayıtlı uygulama izni bulunamadı."
                : $"Shopify tokenında {scopes.Count} uygulama izni doğrulandı.";
            return Supported(MarketplaceCapabilities.ShopifyAppScopes, identity, source, note, verifiedAt, constraintsJson: details);
        }
        catch (JsonException)
        {
            return new(MarketplaceCapabilities.ShopifyAppScopes, "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, null, null,
                "Shopify uygulama izinleri beklenen biçimde dönmedi.", null, verifiedAt);
        }
    }

    private static CapabilityEvidence ShopifyWriteCapabilityEvidence(string code, ConnectionIdentity identity, AdapterResult<JsonDocument> result, string requiredScope, string operation, DateTimeOffset verifiedAt)
    {
        const string source = "https://shopify.dev/docs/api/admin-graphql";
        if (!result.IsSuccess)
            return new(code, "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, requiredScope, null,
                $"{operation} izinleri doğrulanamadı: {result.Error?.SafeMessage ?? "Shopify yanıtı alınamadı."}", null, verifiedAt);
        try
        {
            var scopes = MapGrantedScopes(result.Value!.RootElement);
            var granted = scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
            return granted
                ? Supported(code, identity, source, $"{operation} için {requiredScope} izni doğrulandı.", verifiedAt, requiredScope, JsonSerializer.Serialize(new { grantedScopes = scopes }))
                : new(code, "NOTSUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, requiredScope, JsonSerializer.Serialize(new { grantedScopes = scopes }),
                    $"{operation} açılamadı; Shopify tokenında {requiredScope} izni yok.", null, verifiedAt);
        }
        catch (JsonException)
        {
            return new(code, "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, requiredScope, null,
                "Shopify uygulama izinleri beklenen biçimde dönmedi.", null, verifiedAt);
        }
    }

    public static IReadOnlyList<string> MapGrantedScopes(JsonElement root)
    {
        if (!root.TryGetProperty("currentAppInstallation", out var installation)
            || installation.ValueKind != JsonValueKind.Object
            || !installation.TryGetProperty("accessScopes", out var accessScopes)
            || accessScopes.ValueKind != JsonValueKind.Array)
            throw new JsonException("Shopify access scopes are missing.");

        return accessScopes.EnumerateArray()
            .Where(scope => scope.ValueKind == JsonValueKind.Object && scope.TryGetProperty("handle", out var handle) && handle.ValueKind == JsonValueKind.String)
            .Select(scope => scope.GetProperty("handle").GetString()?.Trim())
            .Where(handle => !string.IsNullOrWhiteSpace(handle))
            .Select(handle => handle!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(handle => handle, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (!string.Equals(resource.ResourceType, "CATEGORIES", StringComparison.Ordinal) || resource.ParentExternalId is not null)
            return Fail<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.NotSupported, "SHOPIFY_REFERENCE_NOT_SUPPORTED", "Shopify bağlantısında yalnızca ürün taksonomisi kategorileri okunabilir.", HttpStatusCode.NotImplemented);

        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return Fail<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized);

        ShopifyTaxonomyPageCursor state;
        RateLimitMetadata? rateLimit = null;
        if (string.IsNullOrWhiteSpace(page.Cursor))
        {
            var rootResult = await ReadTaxonomyRootsAsync(shop, cancellationToken);
            if (!rootResult.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(rootResult.Error!, rootResult.RateLimit);
            rateLimit = rootResult.RateLimit;
            state = new(rootResult.Value!.ToArray(), 0, 0, null);
        }
        else if (!TryDecodeTaxonomyCursor(page.Cursor, out state))
        {
            return Fail<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "SHOPIFY_REFERENCE_CURSOR_INVALID", "Shopify kategori eşitleme imleci geçersiz.", HttpStatusCode.UnprocessableEntity);
        }

        if (state.RootEmissionIndex < 0 || state.RootEmissionIndex > state.Roots.Length
            || state.DescendantRootIndex < 0 || state.DescendantRootIndex > state.Roots.Length)
            return Fail<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "SHOPIFY_REFERENCE_CURSOR_INVALID", "Shopify kategori eşitleme imleci geçersiz.", HttpStatusCode.UnprocessableEntity);

        var limit = Math.Clamp(page.Limit, 1, 1_000);
        var items = new List<RemoteReferenceItem>(limit);
        var rootEmissionIndex = state.RootEmissionIndex;
        var descendantRootIndex = state.DescendantRootIndex;
        var descendantsAfter = state.DescendantsAfter;

        while (items.Count < limit && rootEmissionIndex < state.Roots.Length)
            items.Add(state.Roots[rootEmissionIndex++].Reference);

        while (items.Count < limit && descendantRootIndex < state.Roots.Length)
        {
            var root = state.Roots[descendantRootIndex];
            var first = Math.Min(250, limit - items.Count);
            var result = await QueryAsync(shop, TaxonomyDescendantsQuery, new { first, after = descendantsAfter, rootId = root.GraphQlId }, cancellationToken);
            if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(result.Error!, result.RateLimit);
            rateLimit = result.RateLimit;

            try
            {
                var categoryConnection = result.Value!.RootElement.GetProperty("taxonomy").GetProperty("categories");
                foreach (var category in MapTaxonomyCategories(categoryConnection.GetProperty("nodes")))
                {
                    // Shopify normally returns descendants only. Ignore the
                    // root if an API version includes it in that connection.
                    if (string.Equals(category.ExternalId, root.Reference.ExternalId, StringComparison.Ordinal)) continue;
                    items.Add(category);
                }

                var info = categoryConnection.GetProperty("pageInfo");
                var hasMore = info.GetProperty("hasNextPage").GetBoolean();
                var next = hasMore ? info.GetProperty("endCursor").GetString() : null;
                if (hasMore && (string.IsNullOrWhiteSpace(next) || string.Equals(next, descendantsAfter, StringComparison.Ordinal)))
                    return Fail<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "SHOPIFY_REFERENCE_CURSOR_INVALID", "Shopify kategori yanıtı geçersiz bir sayfalama imleci içeriyor.", HttpStatusCode.BadGateway);

                if (hasMore)
                {
                    descendantsAfter = next;
                }
                else
                {
                    descendantRootIndex++;
                    descendantsAfter = null;
                }
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
            {
                return Fail<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "SHOPIFY_REFERENCE_CONTRACT_INVALID", "Shopify kategori yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
            }
        }

        var hasNext = rootEmissionIndex < state.Roots.Length || descendantRootIndex < state.Roots.Length;
        var nextCursor = hasNext
            ? EncodeTaxonomyCursor(new(state.Roots, rootEmissionIndex, descendantRootIndex, descendantsAfter))
            : null;
        return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(items, nextCursor, hasNext), rateLimit);
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var catalog = await ListCatalogAsync(context, page, filter, cancellationToken);
        if (!catalog.IsSuccess) return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(catalog.Error!, catalog.RateLimit);
        var items = catalog.Value!.Items.SelectMany(product => product.Variants.Select(variant => new RemoteProduct(product.ExternalProductId, variant.ExternalVariantId, variant.Barcode, variant.Sku, variant.RawJson))).ToList();
        return AdapterResult<AdapterPageResult<RemoteProduct>>.Success(new(items, catalog.Value.NextCursor, catalog.Value.HasMore, catalog.Value.TotalCount), catalog.RateLimit);
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return Fail<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized);
        // The catalog query contains nested variant and inventory-level
        // connections. Keeping the product page small is required because
        // Shopify rejects a query whose requested cost exceeds 1,000.
        var first = Math.Clamp(page.Limit, 1, Math.Min(Math.Clamp(settings.PageSize, 1, 250), 5));
        if (!TryBuildProductQuery(shop, filter, out var query, out var lookupError))
            return Fail<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.Validation, "SHOPIFY_PRODUCT_LOOKUP_INVALID", lookupError!, HttpStatusCode.UnprocessableEntity);
        // Keep the catalog page within Shopify's query-cost budget. The
        // aggregate inventoryQuantity is retained here; per-location levels
        // are not requested in this product query because nesting them under
        // every variant can exceed Shopify's single-query cost limit.
        const string gql = "query($first:Int!, $after:String, $query:String) { products(first:$first, after:$after, query:$query, sortKey:UPDATED_AT) { edges { cursor node { id title descriptionHtml vendor productType status updatedAt category { id name fullName } images(first:10) { nodes { url } } priceRange { minVariantPrice { currencyCode } } variants(first:250) { nodes { id sku barcode price compareAtPrice inventoryQuantity selectedOptions { name value } image { url } } } } } pageInfo { hasNextPage endCursor } } }";
        var variables = new { first, after = page.Cursor, query };
        var result = await QueryAsync(shop, gql, variables, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(result.Error!, result.RateLimit);
        try
        {
            var products = result.Value!.RootElement.GetProperty("products");
            var items = products.GetProperty("edges").EnumerateArray().Select(MapProduct).ToList();
            var info = products.GetProperty("pageInfo");
            var hasMore = info.GetProperty("hasNextPage").GetBoolean();
            var next = hasMore ? info.GetProperty("endCursor").GetString() : null;
            return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(items, next, hasMore, items.Count), result.RateLimit);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.ContractViolation, "SHOPIFY_PRODUCT_CONTRACT_INVALID", "Shopify ürün yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<RemoteProduct?>> FindByBarcodeAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        var result = await ListCatalogAsync(context, new(null, 50), new(null, barcode.Trim()), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteProduct?>.Failure(result.Error!, result.RateLimit);
        var match = result.Value!.Items.SelectMany(product => product.Variants.Select(variant => new RemoteProduct(product.ExternalProductId, variant.ExternalVariantId, variant.Barcode, variant.Sku, variant.RawJson))).FirstOrDefault(x => string.Equals(x.Barcode, barcode.Trim(), StringComparison.OrdinalIgnoreCase));
        return AdapterResult<RemoteProduct?>.Success(match, result.RateLimit);
    }

    public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        if (externalOperationId.StartsWith("SHOPIFY_IMMEDIATE:", StringComparison.Ordinal))
            return Task.FromResult(ParseImmediateOperation(externalOperationId));
        return Unsupported<RemoteOperationStatus>("Shopify bu entegrasyonun oluşturmadığı ürün işlemini sorgulamayı desteklemez.");
    }
    public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Unsupported<RemotePublicationStatus>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => Unsupported<RemoteOperationRef>("Shopify ürün yazma ilk sürümde kapalıdır.");
    public async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken)
    {
        var payload = ParsePriceInventoryPayload(payloadJson);
        if (!payload.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(payload.Error!);
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return Fail<RemoteOperationRef>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized);

        var resolved = new List<ShopifyPriceInventoryTarget>();
        RateLimitMetadata? rateLimit = null;
        foreach (var line in payload.Value!)
        {
            var lookup = await QueryAsync(shop, FindVariantForWriteQuery, new { first = 2, query = $"barcode:\"{EscapeSearch(line.Barcode)}\"" }, cancellationToken);
            if (!lookup.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(lookup.Error!, lookup.RateLimit);
            rateLimit = lookup.RateLimit ?? rateLimit;
            try
            {
                var variants = lookup.Value!.RootElement.GetProperty("productVariants").GetProperty("nodes").EnumerateArray().ToArray();
                if (lookup.Value.RootElement.GetProperty("productVariants").GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())
                    return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_VARIANT_MATCH_NOT_UNIQUE", $"'{line.Barcode}' barkodu Shopify'da güvenle ayırt edilemedi.", HttpStatusCode.Conflict);
                if (variants.Length != 1 || !string.Equals(variants[0].GetProperty("barcode").GetString()?.Trim(), line.Barcode, StringComparison.OrdinalIgnoreCase))
                    return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_VARIANT_MATCH_NOT_UNIQUE", $"'{line.Barcode}' barkodu Shopify'da tek bir varyantla eşleşmelidir.", HttpStatusCode.Conflict);
                var variant = variants[0];
                var item = variant.GetProperty("inventoryItem");
                if (!item.GetProperty("tracked").GetBoolean())
                    return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_INVENTORY_NOT_TRACKED", $"'{line.Barcode}' varyantında Shopify stok takibi kapalı.", HttpStatusCode.Conflict);
                var inventoryLevels = item.GetProperty("inventoryLevels");
                if (inventoryLevels.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())
                    return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_INVENTORY_LOCATION_AMBIGUOUS", $"'{line.Barcode}' lokasyonları güvenle ayırt edilemedi; stok yazması durduruldu.", HttpStatusCode.Conflict);
                var levels = inventoryLevels.GetProperty("nodes").EnumerateArray()
                    .Where(level => level.GetProperty("location").GetProperty("isActive").GetBoolean())
                    .ToArray();
                if (levels.Length != 1)
                    return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_INVENTORY_LOCATION_AMBIGUOUS", $"'{line.Barcode}' için tek bir aktif Shopify stok deposu bulunamadı; yanlış depoya stok yazılmasını önlemek için işlem durduruldu.", HttpStatusCode.Conflict);
                var quantityNode = levels[0].GetProperty("quantities").EnumerateArray().FirstOrDefault(value => value.GetProperty("name").GetString() == "available");
                if (quantityNode.ValueKind != JsonValueKind.Object || !quantityNode.GetProperty("quantity").TryGetInt32(out var currentQuantity))
                    return Fail<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "SHOPIFY_INVENTORY_QUANTITY_MISSING", $"'{line.Barcode}' için Shopify mevcut stok miktarı okunamadı.", HttpStatusCode.BadGateway);
                resolved.Add(new(line, variant.GetProperty("id").GetString()!, variant.GetProperty("product").GetProperty("id").GetString()!, item.GetProperty("id").GetString()!, levels[0].GetProperty("location").GetProperty("id").GetString()!, currentQuantity));
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
            {
                return Fail<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "SHOPIFY_VARIANT_WRITE_CONTRACT_INVALID", "Shopify varyant yanıtı fiyat ve stok yazması için gereken alanları içermiyor.", HttpStatusCode.BadGateway);
            }
        }

        foreach (var group in resolved.GroupBy(value => value.ProductId, StringComparer.Ordinal))
        {
            var variants = group.Select(value => new { id = value.VariantId, price = value.Line.SalePrice.ToString("0.00", CultureInfo.InvariantCulture), compareAtPrice = value.Line.ListPrice.ToString("0.00", CultureInfo.InvariantCulture) }).ToArray();
            var update = await QueryAsync(shop, UpdateVariantPricesMutation, new { productId = group.Key, variants }, cancellationToken);
            if (!update.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(update.Error!, update.RateLimit);
            rateLimit = update.RateLimit ?? rateLimit;
            if (HasMutationErrors(update.Value!.RootElement, "productVariantsBulkUpdate"))
                return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_PRICE_UPDATE_REJECTED", "Shopify ürün fiyatı güncellemesini reddetti; varyant izinlerini ve TRY fiyat alanlarını kontrol edin.", HttpStatusCode.UnprocessableEntity);
        }

        foreach (var target in resolved)
        {
            var update = await QueryAsync(shop, SetInventoryQuantitiesMutation, new
            {
                input = new
                {
                    name = "available",
                    reason = "correction",
                    quantities = new[] { new { inventoryItemId = target.InventoryItemId, locationId = target.LocationId, quantity = target.Line.Quantity, changeFromQuantity = target.CurrentQuantity } }
                },
                idempotencyKey = DeterministicIdempotencyKey($"{context.IdempotencyKey}:{target.Line.Barcode}")
            }, cancellationToken);
            if (!update.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(update.Error!, update.RateLimit);
            rateLimit = update.RateLimit ?? rateLimit;
            if (HasMutationErrors(update.Value!.RootElement, "inventorySetQuantities"))
                return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_INVENTORY_UPDATE_REJECTED", $"Shopify '{target.Line.Barcode}' stok güncellemesini reddetti; miktar değişmiş veya stok izni eksik olabilir.", HttpStatusCode.Conflict);
        }

        var lineKeys = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(resolved.Select(target => target.Line.Barcode).ToArray())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return AdapterResult<RemoteOperationRef>.Success(new($"SHOPIFY_IMMEDIATE:{lineKeys}", "SHOPIFY_PRICE_INVENTORY", timeProvider.GetUtcNow()), rateLimit);
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return Fail<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized);
        var first = Math.Clamp(page.Limit, 1, Math.Clamp(settings.OrderPageSize, 1, 250));
        var query = BuildOrderQuery(window);
        var result = await QueryOrderAsync(shop, new { first, after = page.Cursor, query }, single: false, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(result.Error!, result.RateLimit);
        try
        {
            var orders = result.Value!.RootElement.GetProperty("orders");
            var items = orders.GetProperty("edges").EnumerateArray().Select(edge => MapOrder(edge.GetProperty("node"))).ToList();
            var info = orders.GetProperty("pageInfo");
            var hasMore = info.GetProperty("hasNextPage").GetBoolean();
            return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(new(items, hasMore ? info.GetProperty("endCursor").GetString() : null, hasMore, items.Count), result.RateLimit);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.ContractViolation, "SHOPIFY_ORDER_CONTRACT_INVALID", "Shopify sipariş yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return Fail<RemoteOrder>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized);
        var id = externalOrderId.StartsWith("gid://", StringComparison.Ordinal) ? externalOrderId : $"gid://shopify/Order/{externalOrderId}";
        var result = await QueryOrderAsync(shop, new { id }, single: true, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteOrder>.Failure(result.Error!, result.RateLimit);
        var order = result.Value!.RootElement.GetProperty("order");
        if (order.ValueKind == JsonValueKind.Null) return Fail<RemoteOrder>(AdapterErrorClass.NotFound, "SHOPIFY_ORDER_NOT_FOUND", "Shopify siparişi bulunamadı.", HttpStatusCode.NotFound);
        return AdapterResult<RemoteOrder>.Success(MapOrder(order), result.RateLimit);
    }

    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => Unsupported<PackageActionResult>("Shopify kargo dış yazması ilk sürümde kapalıdır.");
    public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => Unsupported<IReadOnlyList<RemoteCargoCompany>>("Shopify kargo firması listesi sağlamaz.");
    public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => Unsupported<IReadOnlyList<RemotePackageableLine>>("Shopify Hepsiburada paketlenebilir kalem listesini sağlamaz.");
    public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => Unsupported<CreateOrderPackageResult>("Shopify Hepsiburada paket oluşturma işlemini sağlamaz.");
    public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => Unsupported<bool>("Shopify etiket işlemi ilk sürümde kapalıdır.");
    public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => Unsupported<CommonLabelDocument>("Shopify etiket işlemi ilk sürümde kapalıdır.");
    public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Unsupported<StageTestOrderResult>("Shopify test siparişi oluşturma ilk sürümde kapalıdır.");
    public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Unsupported<AdapterPageResult<RemoteReturnClaim>>("Shopify iade okuması sonraki aşamada açılacaktır.");
    async Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => await Unsupported<RemoteReturnClaim>("Shopify iade okuması sonraki aşamada açılacaktır.");
    public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => Unsupported<IReadOnlyList<ReturnIssueReason>>("Shopify iade işlemi sonraki aşamada açılacaktır.");
    public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => Unsupported<ReturnActionResult>("Shopify iade dış yazması ilk sürümde kapalıdır.");

    private async Task<AdapterResult<JsonDocument>> QueryAsync(ShopifyRequestContext context, string query, object? variables = null, CancellationToken cancellationToken = default)
    {
        var client = clients.CreateClient("Shopify");
        var endpoint = new Uri($"https://{context.Shop}.myshopify.com/admin/api/{context.ApiVersion}/graphql.json");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Shopify-Access-Token", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var rate = ReadRateLimit(document);
            var remoteRequestId = response.Headers.TryGetValues("X-Request-ID", out var requestIds) ? requestIds.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } resetAt ? resetAt - DateTimeOffset.UtcNow : null);
                return Failure<JsonDocument>(MapHttpError(response.StatusCode, raw, retryAfter, remoteRequestId), rate);
            }
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var firstError = errors[0];
                var message = firstError.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() : "Shopify GraphQL isteği reddedildi.";
                var errorCode = firstError.TryGetProperty("extensions", out var extensions)
                    && extensions.TryGetProperty("code", out var code)
                    ? code.GetString()
                    : null;
                var throttled = string.Equals(errorCode, "THROTTLED", StringComparison.OrdinalIgnoreCase);
                var accessDenied = string.Equals(errorCode, "ACCESS_DENIED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(errorCode, "FORBIDDEN", StringComparison.OrdinalIgnoreCase);
                var deniedField = firstError.TryGetProperty("path", out var path)
                    && path.ValueKind == JsonValueKind.Array
                    ? string.Join(".", path.EnumerateArray()
                        .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString())
                        .Where(value => !string.IsNullOrWhiteSpace(value)))
                    : null;
                return Failure<JsonDocument>(new(
                    throttled ? AdapterErrorClass.RateLimit : accessDenied ? AdapterErrorClass.Authentication : AdapterErrorClass.Validation,
                    throttled ? "SHOPIFY_RATE_LIMITED" : accessDenied ? "SHOPIFY_REQUIRED_READ_SCOPE" : "SHOPIFY_GRAPHQL_ERROR",
                    throttled
                        ? "Shopify API sınırına ulaşıldı; bağlantı bazında yeniden denenecek."
                        : accessDenied
                            ? $"Shopify API izni eksik{(string.IsNullOrWhiteSpace(deniedField) ? string.Empty : $" ({deniedField})")}. read_products, read_inventory, read_orders, read_customers, read_locations, write_products ve write_inventory izinlerini kontrol edin."
                            : message ?? "Shopify GraphQL isteği reddedildi.",
                    (int)response.StatusCode,
                    throttled ? rate?.RetryAfter ?? TimeSpan.FromSeconds(5) : null,
                    remoteRequestId), rate);
            }
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return Failure<JsonDocument>(new(
                    AdapterErrorClass.ContractViolation,
                    "SHOPIFY_GRAPHQL_CONTRACT_INVALID",
                    "Shopify GraphQL yanıtı beklenen veri alanını içermiyor.",
                    (int)HttpStatusCode.BadGateway,
                    null,
                    remoteRequestId), rate);
            }

            // Shopify GraphQL wraps every successful result in a top-level
            // `data` object. Keep the adapter contract flat so the catalog,
            // order and connection checks can consume the queried fields
            // consistently.
            return AdapterResult<JsonDocument>.Success(JsonDocument.Parse(data.GetRawText()), rate);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Shopify GraphQL isteği başarısız. Shop: {Shop}", context.Shop);
            return Failure<JsonDocument>(new(AdapterErrorClass.TransientNetwork, "SHOPIFY_NETWORK_ERROR", "Shopify bağlantısı geçici olarak kurulamadı.", null, null, null));
        }
    }

    private static RemoteCatalogProduct MapProduct(JsonElement edge)
    {
        var product = edge.GetProperty("node");
        var productId = ShortId(product.GetProperty("id").GetString());
        var status = product.GetProperty("status").GetString();
        var isDraft = string.Equals(status, "DRAFT", StringComparison.OrdinalIgnoreCase);
        // Shopify treats DRAFT and ARCHIVED as separate lifecycle states. The
        // importer filters drafts independently, so a draft must not be
        // silently removed by the archived-products switch.
        var archived = string.Equals(status, "ARCHIVED", StringComparison.OrdinalIgnoreCase);
        var category = product.TryGetProperty("category", out var categoryElement) && categoryElement.ValueKind != JsonValueKind.Null ? categoryElement : default;
        var categoryId = category.ValueKind == JsonValueKind.Object && category.TryGetProperty("id", out var categoryIdElement) ? ShortId(categoryIdElement.GetString()) : null;
        var categoryName = category.ValueKind == JsonValueKind.Object && category.TryGetProperty("fullName", out var fullName) ? fullName.GetString() : product.GetProperty("productType").GetString();
        var images = product.GetProperty("images").GetProperty("nodes").EnumerateArray().Select(x => x.GetProperty("url").GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
        var currency = product.TryGetProperty("priceRange", out var priceRange)
            && priceRange.ValueKind == JsonValueKind.Object
            && priceRange.TryGetProperty("minVariantPrice", out var minPrice)
            && minPrice.ValueKind == JsonValueKind.Object
            && minPrice.TryGetProperty("currencyCode", out var currencyElement)
            ? currencyElement.GetString()
            : null;
        var variants = product.GetProperty("variants").GetProperty("nodes").EnumerateArray().Select((variant, index) => MapVariant(variant, index, archived, currency)).ToList();
        return new(productId, null, product.GetProperty("title").GetString() ?? productId, product.GetProperty("descriptionHtml").GetString() ?? "", product.GetProperty("vendor").GetString(), product.GetProperty("vendor").GetString(), categoryId, categoryName, images, variants, product.GetRawText(), isDraft);
    }

    private static RemoteCatalogVariant MapVariant(JsonElement variant, int index, bool productArchived, string? currency)
    {
        var options = variant.GetProperty("selectedOptions").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString() ?? $"Seçenek {index + 1}", x => x.GetProperty("value").GetString() ?? "", StringComparer.OrdinalIgnoreCase);
        var inventory = variant.TryGetProperty("inventoryQuantity", out var inventoryElement) && inventoryElement.ValueKind == JsonValueKind.Number && inventoryElement.TryGetDecimal(out var quantity) ? quantity : (decimal?)null;
        var price = DecimalOrNull(variant, "price");
        var compareAt = DecimalOrNull(variant, "compareAtPrice");
        var image = variant.TryGetProperty("image", out var imageElement) && imageElement.ValueKind == JsonValueKind.Object && imageElement.TryGetProperty("url", out var imageUrl) ? imageUrl.GetString() : null;
        var levels = variant.TryGetProperty("inventoryItem", out var inventoryItem)
            && inventoryItem.ValueKind == JsonValueKind.Object
            && inventoryItem.TryGetProperty("inventoryLevels", out var inventoryLevels)
            && inventoryLevels.ValueKind == JsonValueKind.Object
            && inventoryLevels.TryGetProperty("nodes", out var inventoryNodes)
            && inventoryNodes.ValueKind == JsonValueKind.Array
            ? inventoryNodes.EnumerateArray().Select(level =>
            {
                var location = level.GetProperty("location");
                var quantities = level.TryGetProperty("quantities", out var quantityNodes) && quantityNodes.ValueKind == JsonValueKind.Array
                    ? quantityNodes.EnumerateArray().FirstOrDefault()
                    : default;
                var quantity = quantities.ValueKind == JsonValueKind.Object && quantities.TryGetProperty("quantity", out var quantityValue) && quantityValue.TryGetDecimal(out var levelQuantity)
                    ? levelQuantity
                    : 0m;
                return new RemoteInventoryLevel(ShortId(location.GetProperty("id").GetString()), location.TryGetProperty("name", out var locationName) ? locationName.GetString() : null, quantity, level.GetRawText());
            }).ToList()
            : [];
        var barcode = variant.GetProperty("barcode").ValueKind == JsonValueKind.Null ? null : variant.GetProperty("barcode").GetString();
        // Shopify has no model-code field. SKU is the stock code and barcode
        // is the only identity used when an existing Ravencia product is
        // matched. Keep those values separate instead of mirroring barcode
        // into the shared model-code field.
        return new(ShortId(variant.GetProperty("id").GetString()), variant.GetProperty("sku").GetString() ?? ShortId(variant.GetProperty("id").GetString()), barcode, null, options, productArchived, price, compareAt, null, inventory, currency, variant.GetRawText(), image is null ? [] : [image], levels);
    }

    private static RemoteOrder MapOrder(JsonElement order)
    {
        var money = order.GetProperty("currentTotalPriceSet").GetProperty("shopMoney");
        var discount = order.GetProperty("totalDiscountsSet").GetProperty("shopMoney");
        var currency = money.GetProperty("currencyCode").GetString() ?? "TRY";
        var financialStatus = order.GetProperty("displayFinancialStatus").GetString() ?? "OPEN";
        var lines = order.GetProperty("lineItems").GetProperty("nodes").EnumerateArray().Select(line => MapOrderLine(line, financialStatus)).ToList();
        // In the 2026-07 Admin API, Order.fulfillments is returned as a
        // direct list. Its nested fulfillmentLineItems field remains a
        // connection and is mapped from nodes below.
        var fulfillments = order.GetProperty("fulfillments").EnumerateArray().ToList();
        var fulfilledByLine = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var packages = new List<RemotePackage>();
        foreach (var fulfillment in fulfillments)
        {
            var rawStatus = FulfillmentStatus(fulfillment);
            var tracking = fulfillment.GetProperty("trackingInfo").EnumerateArray().FirstOrDefault();
            var allocations = fulfillment.GetProperty("fulfillmentLineItems").GetProperty("nodes").EnumerateArray().Select(item =>
            {
                var lineId = ShortId(item.GetProperty("lineItem").GetProperty("id").GetString());
                var quantity = DecimalOrNull(item, "quantity") ?? 0m;
                var cancelled = rawStatus == "CANCELLED" ? quantity : 0m;
                var active = rawStatus == "CANCELLED" ? 0m : quantity;
                if (active > 0) fulfilledByLine[lineId] = fulfilledByLine.GetValueOrDefault(lineId) + active;
                var shipped = rawStatus is "SHIPPED" or "DELIVERED" ? active : 0m;
                var delivered = rawStatus == "DELIVERED" ? active : 0m;
                return new RemotePackageAllocation(lineId, active, cancelled, shipped, delivered, 0);
            }).ToList();
            packages.Add(new(
                ShortId(fulfillment.GetProperty("id").GetString()),
                null,
                rawStatus,
                FulfillmentStatusOccurredAt(fulfillment, rawStatus),
                tracking.ValueKind == JsonValueKind.Object && tracking.TryGetProperty("company", out var company) ? company.GetString() : null,
                tracking.ValueKind == JsonValueKind.Object && tracking.TryGetProperty("number", out var number) ? number.GetString() : null,
                allocations,
                DecimalOrNull(money, "amount") ?? 0,
                DecimalOrNull(discount, "amount") ?? 0,
                DecimalOrNull(money, "amount") ?? 0));
        }

        // Shopify exposes the original line quantity and the current quantity
        // separately. Keep the original quantity as the order line authority;
        // the synthetic remainder package makes partial cancellations and
        // partially fulfilled lines satisfy the shared package invariant.
        var remainderAllocations = lines.Select(line =>
        {
            var remaining = Math.Max(0, line.Quantity - line.CancelledQuantity - fulfilledByLine.GetValueOrDefault(line.ExternalLineId));
            return new RemotePackageAllocation(line.ExternalLineId, remaining, line.CancelledQuantity, 0, 0, 0);
        }).Where(allocation => allocation.AllocatedQuantity > 0 || allocation.CancelledQuantity > 0).ToList();
        if (remainderAllocations.Count > 0)
        {
            var allCancelled = remainderAllocations.All(allocation => allocation.AllocatedQuantity == 0);
            packages.Add(new(
                $"order:{ShortId(order.GetProperty("id").GetString())}:remainder",
                null,
                allCancelled ? "CANCELLED" : "UNFULFILLED",
                order.GetProperty("updatedAt").GetDateTimeOffset(),
                null,
                null,
                remainderAllocations,
                DecimalOrNull(money, "amount") ?? 0,
                DecimalOrNull(discount, "amount") ?? 0,
                DecimalOrNull(money, "amount") ?? 0));
        }

        var refunds = order.TryGetProperty("refunds", out var refundsElement) && refundsElement.ValueKind == JsonValueKind.Array
            ? refundsElement.EnumerateArray().Select(refund => new RemoteOrderRefund(
                ShortId(refund.GetProperty("id").GetString()),
                refund.GetProperty("createdAt").GetDateTimeOffset(),
                DecimalOrNull(refund.GetProperty("totalRefundedSet").GetProperty("shopMoney"), "amount") ?? 0,
                refund.GetProperty("totalRefundedSet").GetProperty("shopMoney").GetProperty("currencyCode").GetString() ?? currency,
                refund.GetRawText())).ToList()
            : [];
        var refundedAmount = refunds.Sum(refund => refund.Amount);
        var refundStatus = financialStatus.ToUpperInvariant() switch
        {
            "REFUNDED" => "REFUNDED",
            "PARTIALLY_REFUNDED" => "PARTIALLY_REFUNDED",
            _ when refundedAmount > 0 => "PARTIALLY_REFUNDED",
            _ => "NOT_REFUNDED"
        };
        var orderEmail = order.TryGetProperty("email", out var orderEmailElement) && orderEmailElement.ValueKind == JsonValueKind.String ? orderEmailElement.GetString() : null;
        var orderPhone = order.TryGetProperty("phone", out var orderPhoneElement) && orderPhoneElement.ValueKind == JsonValueKind.String ? orderPhoneElement.GetString() : null;
        var customer = order.TryGetProperty("customer", out var customerElement) ? customerElement : default;
        var shippingAddress = order.TryGetProperty("shippingAddress", out var shippingAddressElement) && shippingAddressElement.ValueKind == JsonValueKind.Object ? shippingAddressElement : default;
        var billingAddress = order.TryGetProperty("billingAddress", out var billingAddressElement) && billingAddressElement.ValueKind == JsonValueKind.Object ? billingAddressElement : default;
        var addressFirstName = TextValue(shippingAddress, "firstName") ?? TextValue(billingAddress, "firstName");
        var addressLastName = TextValue(shippingAddress, "lastName") ?? TextValue(billingAddress, "lastName");
        var addressName = TextValue(shippingAddress, "name") ?? TextValue(billingAddress, "name") ?? JoinName(addressFirstName, addressLastName);
        var customerJson = customer.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Serialize(new
            {
                id = TextValue(customer, "id"),
                customerName = TextValue(customer, "displayName") ?? JoinName(TextValue(customer, "firstName"), TextValue(customer, "lastName")),
                customerFirstName = TextValue(customer, "firstName"),
                customerLastName = TextValue(customer, "lastName"),
                email = TextValue(customer, "email") ?? orderEmail,
                phone = TextValue(customer, "phone") ?? orderPhone
            })
            : JsonSerializer.Serialize(new
            {
                customerName = addressName,
                customerFirstName = addressFirstName,
                customerLastName = addressLastName,
                email = orderEmail,
                phone = orderPhone
            });
        return new(
            ShortId(order.GetProperty("id").GetString()),
            order.GetProperty("name").GetString() ?? ShortId(order.GetProperty("id").GetString()),
            order.GetProperty("createdAt").GetDateTimeOffset(),
            order.GetProperty("updatedAt").GetDateTimeOffset(),
            currency,
            DecimalOrNull(money, "amount") ?? 0,
            DecimalOrNull(discount, "amount") ?? 0,
            DecimalOrNull(money, "amount") ?? 0,
            customerJson,
            AddressJson(order, "shippingAddress"),
            AddressJson(order, "billingAddress"),
            lines,
            packages,
            order.GetRawText(),
            null,
            financialStatus,
            order.GetProperty("cancelledAt").ValueKind == JsonValueKind.Null ? "NOT_CANCELLED" : "CANCELLED",
            refundStatus,
            refundedAmount,
            refunds);
    }

    private static RemoteOrderLine MapOrderLine(JsonElement line, string financialStatus)
    {
        var variant = line.TryGetProperty("variant", out var variantElement) && variantElement.ValueKind == JsonValueKind.Object ? variantElement : default;
        var lineSku = line.TryGetProperty("sku", out var skuElement) && skuElement.ValueKind == JsonValueKind.String ? skuElement.GetString() : null;
        var variantSku = variant.ValueKind == JsonValueKind.Object && variant.TryGetProperty("sku", out var variantSkuElement) && variantSkuElement.ValueKind == JsonValueKind.String ? variantSkuElement.GetString() : null;
        var barcode = variant.ValueKind == JsonValueKind.Object && variant.TryGetProperty("barcode", out var barcodeElement) && barcodeElement.ValueKind == JsonValueKind.String ? barcodeElement.GetString() : null;
        var sku = string.IsNullOrWhiteSpace(lineSku) ? variantSku : lineSku;
        var orderedQuantity = DecimalOrNull(line, "quantity") ?? DecimalOrNull(line, "currentQuantity") ?? 0m;
        var currentQuantity = DecimalOrNull(line, "currentQuantity") ?? orderedQuantity;
        var cancelledQuantity = Math.Clamp(orderedQuantity - currentQuantity, 0m, orderedQuantity);
        var unitPrice = DecimalOrNull(line.GetProperty("originalUnitPriceSet").GetProperty("shopMoney"), "amount") ?? 0m;
        return new(ShortId(line.GetProperty("id").GetString()), string.IsNullOrWhiteSpace(sku) ? "SHOPIFY-LINE" : sku!, barcode, line.GetProperty("name").GetString() ?? "Shopify ürünü", orderedQuantity, unitPrice, 0m, financialStatus, line.GetRawText(), cancelledQuantity);
    }

    private static string FulfillmentStatus(JsonElement fulfillment)
    {
        if (fulfillment.TryGetProperty("deliveredAt", out var deliveredAt) && deliveredAt.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            return "DELIVERED";
        if (fulfillment.TryGetProperty("displayStatus", out var displayStatus) && string.Equals(displayStatus.GetString(), "DELIVERED", StringComparison.OrdinalIgnoreCase))
            return "DELIVERED";
        if (fulfillment.TryGetProperty("events", out var events)
            && events.ValueKind == JsonValueKind.Object
            && events.TryGetProperty("nodes", out var eventNodes)
            && eventNodes.ValueKind == JsonValueKind.Array
            && eventNodes.EnumerateArray().Any(item => item.TryGetProperty("status", out var eventStatus) && string.Equals(eventStatus.GetString(), "DELIVERED", StringComparison.OrdinalIgnoreCase)))
            return "DELIVERED";
        var raw = fulfillment.TryGetProperty("status", out var status) ? status.GetString()?.Trim().ToUpperInvariant() : null;
        return raw switch
        {
            "CANCELLED" or "CANCELED" => "CANCELLED",
            "OPEN" or "PENDING" or "IN_PROGRESS" => "PROCESSING",
            "SUCCESS" or "FULFILLED" => "SHIPPED",
            _ => "SHIPPED"
        };
    }

    internal static DateTimeOffset FulfillmentStatusOccurredAt(JsonElement fulfillment, string rawStatus)
    {
        if (string.Equals(rawStatus, "DELIVERED", StringComparison.OrdinalIgnoreCase))
        {
            if (fulfillment.TryGetProperty("deliveredAt", out var deliveredAt)
                && deliveredAt.ValueKind == JsonValueKind.String
                && deliveredAt.TryGetDateTimeOffset(out var deliveredAtValue))
                return deliveredAtValue;

            if (fulfillment.TryGetProperty("events", out var events)
                && events.ValueKind == JsonValueKind.Object
                && events.TryGetProperty("nodes", out var eventNodes)
                && eventNodes.ValueKind == JsonValueKind.Array)
            {
                var deliveredEventAt = eventNodes.EnumerateArray()
                    .Where(item => item.TryGetProperty("status", out var status)
                        && string.Equals(status.GetString(), "DELIVERED", StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.TryGetProperty("happenedAt", out var happenedAt)
                        && happenedAt.ValueKind == JsonValueKind.String
                        && happenedAt.TryGetDateTimeOffset(out var happenedAtValue)
                            ? happenedAtValue
                            : (DateTimeOffset?)null)
                    .Where(value => value.HasValue)
                    .Max();
                if (deliveredEventAt.HasValue) return deliveredEventAt.Value;
            }
        }

        if (fulfillment.TryGetProperty("updatedAt", out var updatedAt)
            && updatedAt.ValueKind == JsonValueKind.String
            && updatedAt.TryGetDateTimeOffset(out var updatedAtValue))
            return updatedAtValue;

        return fulfillment.GetProperty("createdAt").GetDateTimeOffset();
    }

    private static string AddressJson(JsonElement order, string property) => order.TryGetProperty(property, out var address) && address.ValueKind != JsonValueKind.Null ? address.GetRawText() : "{}";
    private static string? TextValue(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? JoinName(string? firstName, string? lastName) => string.Join(" ", new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim() is { Length: > 0 } name ? name : null;
    private static decimal? DecimalOrNull(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;

    private async Task<AdapterResult<IReadOnlyList<ShopifyTaxonomyRoot>>> ReadTaxonomyRootsAsync(ShopifyRequestContext shop, CancellationToken cancellationToken)
    {
        var roots = new List<ShopifyTaxonomyRoot>();
        var visitedCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        RateLimitMetadata? rateLimit = null;
        do
        {
            var result = await QueryAsync(shop, TaxonomyRootsQuery, new { first = 250, after = cursor }, cancellationToken);
            if (!result.IsSuccess) return AdapterResult<IReadOnlyList<ShopifyTaxonomyRoot>>.Failure(result.Error!, result.RateLimit);
            rateLimit = result.RateLimit;
            try
            {
                var connection = result.Value!.RootElement.GetProperty("taxonomy").GetProperty("categories");
                foreach (var category in connection.GetProperty("nodes").EnumerateArray())
                {
                    var graphQlId = category.GetProperty("id").GetString();
                    var mapped = MapTaxonomyCategory(category);
                    if (string.IsNullOrWhiteSpace(graphQlId) || roots.Any(root => string.Equals(root.Reference.ExternalId, mapped.ExternalId, StringComparison.Ordinal)))
                        return Fail<IReadOnlyList<ShopifyTaxonomyRoot>>(AdapterErrorClass.ContractViolation, "SHOPIFY_REFERENCE_IDENTIFIERS_INVALID", "Shopify kategori yanıtı boş veya yinelenen kimlik içeriyor.", HttpStatusCode.BadGateway);
                    roots.Add(new(graphQlId, mapped));
                }

                var info = connection.GetProperty("pageInfo");
                var hasMore = info.GetProperty("hasNextPage").GetBoolean();
                var next = hasMore ? info.GetProperty("endCursor").GetString() : null;
                if (hasMore && (string.IsNullOrWhiteSpace(next) || !visitedCursors.Add(next)))
                    return Fail<IReadOnlyList<ShopifyTaxonomyRoot>>(AdapterErrorClass.ContractViolation, "SHOPIFY_REFERENCE_CURSOR_INVALID", "Shopify kategori yanıtı geçersiz bir sayfalama imleci içeriyor.", HttpStatusCode.BadGateway);
                cursor = next;
                if (roots.Count > 500_000)
                    return Fail<IReadOnlyList<ShopifyTaxonomyRoot>>(AdapterErrorClass.ContractViolation, "SHOPIFY_REFERENCE_RESULT_LIMIT_EXCEEDED", "Shopify kategori yanıtı güvenli işleme sınırını aştı.", HttpStatusCode.BadGateway);
                if (!hasMore) break;
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
            {
                return Fail<IReadOnlyList<ShopifyTaxonomyRoot>>(AdapterErrorClass.ContractViolation, "SHOPIFY_REFERENCE_CONTRACT_INVALID", "Shopify kategori yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
            }
        } while (!cancellationToken.IsCancellationRequested);

        cancellationToken.ThrowIfCancellationRequested();
        if (roots.Count == 0)
            return Fail<IReadOnlyList<ShopifyTaxonomyRoot>>(AdapterErrorClass.ContractViolation, "SHOPIFY_REFERENCE_EMPTY_RESPONSE", "Shopify ürün taksonomisi kategori listesi boş döndü.", HttpStatusCode.BadGateway);
        return AdapterResult<IReadOnlyList<ShopifyTaxonomyRoot>>.Success(roots, rateLimit);
    }

    internal static IReadOnlyList<RemoteReferenceItem> MapTaxonomyCategories(JsonElement categories) =>
        categories.EnumerateArray().Select(MapTaxonomyCategory).ToArray();

    internal static RemoteReferenceItem MapTaxonomyCategory(JsonElement category)
    {
        var externalId = ShortId(category.GetProperty("id").GetString());
        var name = category.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(name))
            throw new JsonException("Shopify category id/name is missing.");
        var parentExternalId = category.TryGetProperty("parentId", out var parent) && parent.ValueKind == JsonValueKind.String
            ? ShortId(parent.GetString())
            : null;
        var fullName = category.TryGetProperty("fullName", out var fullNameElement) && fullNameElement.ValueKind == JsonValueKind.String
            ? fullNameElement.GetString()
            : null;
        var depth = category.TryGetProperty("level", out var level) && level.TryGetInt32(out var categoryLevel)
            ? categoryLevel
            : Math.Max(0, (fullName ?? name).Split(" > ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length - 1);
        var isLeaf = category.TryGetProperty("isLeaf", out var leaf) && leaf.ValueKind == JsonValueKind.True;
        var isArchived = category.TryGetProperty("isArchived", out var archived) && archived.ValueKind == JsonValueKind.True;
        return new("CATEGORIES", externalId, parentExternalId, name, string.IsNullOrWhiteSpace(fullName) ? name : fullName, depth, isLeaf, !isArchived, category.GetRawText());
    }

    private static string EncodeTaxonomyCursor(ShopifyTaxonomyPageCursor cursor) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor));

    private static bool TryDecodeTaxonomyCursor(string cursor, out ShopifyTaxonomyPageCursor state)
    {
        try
        {
            var decoded = JsonSerializer.Deserialize<ShopifyTaxonomyPageCursor>(Convert.FromBase64String(cursor));
            if (decoded is null || decoded.Roots is null)
            {
                state = new([], 0, 0, null);
                return false;
            }
            state = decoded;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            state = new([], 0, 0, null);
            return false;
        }
    }

    private sealed record ShopifyTaxonomyRoot(string GraphQlId, RemoteReferenceItem Reference);
    private sealed record ShopifyTaxonomyPageCursor(ShopifyTaxonomyRoot[] Roots, int RootEmissionIndex, int DescendantRootIndex, string? DescendantsAfter);

    private static bool TryBuildProductQuery(ShopifyRequestContext context, ProductReadFilter filter, out string? query, out string? error)
    {
        var parts = new List<string>();
        error = null;
        if (filter.ModifiedAfter is { } modified) parts.Add($"updated_at:>={modified.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
        if (!string.IsNullOrWhiteSpace(filter.Barcode)) parts.Add($"barcode:{EscapeSearch(filter.Barcode)}");
        if (!string.IsNullOrWhiteSpace(filter.ProductUrl))
        {
            if (!Uri.TryCreate(filter.ProductUrl, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Host, $"{context.Shop}.myshopify.com", StringComparison.OrdinalIgnoreCase))
            {
                query = null;
                error = "Tekil Shopify ürünü yalnız bağlı mağazanın güvenli myshopify.com adresinden seçilebilir.";
                return false;
            }
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length != 2 || !string.Equals(segments[0], "products", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(segments[1]))
            {
                query = null;
                error = "Shopify ürün bağlantısı /products/{ürün-handle} biçiminde olmalıdır.";
                return false;
            }
            parts.Add($"handle:{EscapeSearch(Uri.UnescapeDataString(segments[1]))}");
        }
        else if (!string.IsNullOrWhiteSpace(filter.ProductMainId))
        {
            // Shopify has no model-code field in this integration. The shared
            // ProductMainId slot carries the single-product barcode for the
            // Shopify adapter; SKU remains the stock code and is not reused.
            parts.Add($"barcode:{EscapeSearch(filter.ProductMainId)}");
        }
        else if (!string.IsNullOrWhiteSpace(filter.ContentId))
        {
            parts.Add($"id:{EscapeSearch(filter.ContentId)}");
        }
        query = parts.Count == 0 ? null : string.Join(' ', parts);
        return true;
    }
    private static string EscapeSearch(string value) => value.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    internal static AdapterResult<IReadOnlyList<ShopifyPriceInventoryLine>> ParsePriceInventoryPayload(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is 0 or > 1000)
                return Failure<IReadOnlyList<ShopifyPriceInventoryLine>>(new(AdapterErrorClass.Validation, "SHOPIFY_PRICE_INVENTORY_PAYLOAD_INVALID", "Shopify fiyat-stok payload'ı 1-1000 satır içermelidir.", (int)HttpStatusCode.UnprocessableEntity, null, null));
            var lines = new List<ShopifyPriceInventoryLine>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                var barcode = item.TryGetProperty("barcode", out var barcodeElement) ? barcodeElement.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(barcode) || !seen.Add(barcode)
                    || !item.TryGetProperty("quantity", out var quantityElement) || !quantityElement.TryGetInt32(out var quantity) || quantity < 0
                    || !item.TryGetProperty("salePrice", out var saleElement) || !saleElement.TryGetDecimal(out var salePrice) || salePrice <= 0
                    || !item.TryGetProperty("listPrice", out var listElement) || !listElement.TryGetDecimal(out var listPrice) || listPrice < salePrice)
                    return Failure<IReadOnlyList<ShopifyPriceInventoryLine>>(new(AdapterErrorClass.Validation, "SHOPIFY_PRICE_INVENTORY_PAYLOAD_INVALID", "Shopify satırlarında benzersiz barkod, quantity >= 0 ve listPrice >= salePrice > 0 gerekir.", (int)HttpStatusCode.UnprocessableEntity, null, null));
                lines.Add(new(barcode, quantity, salePrice, listPrice));
            }
            return AdapterResult<IReadOnlyList<ShopifyPriceInventoryLine>>.Success(lines);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Failure<IReadOnlyList<ShopifyPriceInventoryLine>>(new(AdapterErrorClass.Validation, "SHOPIFY_PRICE_INVENTORY_PAYLOAD_INVALID", "Shopify fiyat-stok payload'ı geçerli JSON değil.", (int)HttpStatusCode.UnprocessableEntity, null, null));
        }
    }

    internal static AdapterResult<RemoteOperationStatus> ParseImmediateOperation(string externalOperationId)
    {
        try
        {
            var encoded = externalOperationId["SHOPIFY_IMMEDIATE:".Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var lines = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(encoded));
            if (lines is null || lines.Length is 0 or > 1000 || lines.Any(string.IsNullOrWhiteSpace) || lines.Distinct(StringComparer.OrdinalIgnoreCase).Count() != lines.Length)
                throw new JsonException("Shopify operation key list is invalid.");
            return AdapterResult<RemoteOperationStatus>.Success(new(externalOperationId, "COMPLETED", lines.Select(barcode => new RemoteOperationLine(barcode, true, null, null, false)).ToArray()));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return Failure<RemoteOperationStatus>(new(AdapterErrorClass.ContractViolation, "SHOPIFY_OPERATION_REFERENCE_INVALID", "Shopify işleminin doğrulama referansı geçersiz.", (int)HttpStatusCode.BadGateway, null, null));
        }
    }

    private static bool HasMutationErrors(JsonElement root, string field) =>
        root.TryGetProperty(field, out var mutation)
        && mutation.ValueKind == JsonValueKind.Object
        && mutation.TryGetProperty("userErrors", out var errors)
        && errors.ValueKind == JsonValueKind.Array
        && errors.GetArrayLength() > 0;
    private static string DeterministicIdempotencyKey(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D");
    }
    private static string ShortId(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Split('/').Last();
    private static string? BuildOrderQuery(OrderPollWindow window) => window.ModifiedAfter is { } from ? $"updated_at:>={from.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}" : null;
    private static AdapterError MapHttpError(HttpStatusCode status, string body, TimeSpan? retryAfter = null, string? remoteRequestId = null) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "SHOPIFY_ACCESS_DENIED", "Shopify erişim izni reddedildi.", (int)status, null, remoteRequestId),
        (HttpStatusCode)429 => new(AdapterErrorClass.RateLimit, "SHOPIFY_RATE_LIMITED", "Shopify API sınırına ulaşıldı.", (int)status, retryAfter ?? TimeSpan.FromSeconds(5), remoteRequestId),
        >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "SHOPIFY_REMOTE_ERROR", "Shopify geçici bir sunucu hatası döndürdü.", (int)status, retryAfter ?? TimeSpan.FromSeconds(15), remoteRequestId),
        _ => new(AdapterErrorClass.Validation, "SHOPIFY_HTTP_ERROR", $"Shopify isteği reddedildi ({(int)status}).", (int)status, null, remoteRequestId)
    };
    private static RateLimitMetadata? ReadRateLimit(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("extensions", out var extensions)
            || !extensions.TryGetProperty("cost", out var cost)
            || !cost.TryGetProperty("throttleStatus", out var throttle)
            || !throttle.TryGetProperty("currentlyAvailable", out var availableElement)
            || !availableElement.TryGetInt32(out var available)) return null;
        int? maximum = throttle.TryGetProperty("maximumAvailable", out var maximumElement) && maximumElement.TryGetInt32(out var maximumValue) ? maximumValue : null;
        double? restoreRate = throttle.TryGetProperty("restoreRate", out var restoreElement) && restoreElement.TryGetDouble(out var restoreValue) ? restoreValue : null;
        TimeSpan? retryAfter = maximum is { } limit && restoreRate is > 0 && available < limit
            ? TimeSpan.FromSeconds((limit - available) / restoreRate.Value)
            : null;
        return new(available, null, retryAfter);
    }
    private static CapabilityEvidence Supported(string code, ConnectionIdentity identity, string source, string note, DateTimeOffset verified, string? scope = null, string? constraintsJson = null) => new(code, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, scope, constraintsJson, note, null, verified);
    private static CapabilityEvidence Probe<T>(string code, ConnectionIdentity identity, string source, AdapterResult<T> result, string operation, DateTimeOffset verified, string scope) => result.IsSuccess
        ? Supported(code, identity, source, $"{operation} başarılı.", verified, scope)
        : new(code, "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, scope, null, $"{operation} doğrulanamadı: {result.Error?.SafeMessage ?? "Shopify yanıtı alınamadı."}", null, verified);
    private static AdapterResult<T> Fail<T>(AdapterErrorClass kind, string code, string message, HttpStatusCode status) => AdapterResult<T>.Failure(new(kind, code, message, (int)status, null, null));
    private static Task<AdapterResult<T>> Unsupported<T>(string message) => Task.FromResult(Fail<T>(AdapterErrorClass.NotSupported, "SHOPIFY_NOT_SUPPORTED", message, HttpStatusCode.NotImplemented));
    private static AdapterResult<T> Failure<T>(AdapterError error, RateLimitMetadata? rate = null) => AdapterResult<T>.Failure(error, rate);
}

internal sealed record ShopifyPriceInventoryLine(string Barcode, int Quantity, decimal SalePrice, decimal ListPrice);
internal sealed record ShopifyPriceInventoryTarget(ShopifyPriceInventoryLine Line, string VariantId, string ProductId, string InventoryItemId, string LocationId, int CurrentQuantity);
