using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using ListingSaleState = MarketplaceHub.Domain.ListingSaleState;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient
{
    // The store's own product types are the categories mapped for Shopify (productTypes, 2026-10, read_products).
    private const string ProductTypesQuery = "query($first:Int!, $after:String) { productTypes(first:$first, after:$after) { nodes pageInfo { hasNextPage endCursor } } }";

    public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (!string.Equals(resource.ResourceType, "CATEGORIES", StringComparison.Ordinal) || resource.ParentExternalId is not null)
            return Fail<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.NotSupported, "SHOPIFY_REFERENCE_NOT_SUPPORTED", "Shopify bağlantısında yalnızca mağazanın ürün türleri kategori olarak okunabilir.", HttpStatusCode.NotImplemented);

        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));

        var first = Math.Clamp(page.Limit, 1, 1_000);
        var after = string.IsNullOrWhiteSpace(page.Cursor) ? null : page.Cursor;
        var result = await QueryAsync(shop, ProductTypesQuery, new { first, after }, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(result.Error!, result.RateLimit);
        try
        {
            var connection = result.Value!.RootElement.GetProperty("productTypes");
            var items = MapProductTypes(connection.GetProperty("nodes"));
            var info = connection.GetProperty("pageInfo");
            var hasMore = info.GetProperty("hasNextPage").GetBoolean();
            var next = hasMore ? info.GetProperty("endCursor").GetString() : null;
            if (hasMore && (string.IsNullOrWhiteSpace(next) || string.Equals(next, after, StringComparison.Ordinal)))
                return Fail<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "SHOPIFY_REFERENCE_CURSOR_INVALID", "Shopify ürün türü yanıtı geçersiz bir sayfalama imleci içeriyor.", HttpStatusCode.BadGateway);
            return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(items, next, hasMore), result.RateLimit);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "SHOPIFY_REFERENCE_CONTRACT_INVALID", "Shopify ürün türü yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
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
        if (shop is null) return CredentialFailure(Fail<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
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
        const string gql = "query($first:Int!, $after:String, $query:String) { products(first:$first, after:$after, query:$query, sortKey:UPDATED_AT) { edges { cursor node { id title descriptionHtml vendor productType status updatedAt category { id name fullName } media(first:10) { nodes { ... on MediaImage { image { url } } } } priceRangeV2 { minVariantPrice { currencyCode } } variants(first:250) { nodes { " + CatalogVariantFields + " } pageInfo { hasNextPage endCursor } } } } pageInfo { hasNextPage endCursor } } }";
        var variables = new { first, after = page.Cursor, query };
        var result = await QueryAsync(shop, gql, variables, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(result.Error!, result.RateLimit);
        try
        {
            var products = result.Value!.RootElement.GetProperty("products");
            var items = new List<RemoteCatalogProduct>();
            foreach (var edge in products.GetProperty("edges").EnumerateArray())
            {
                // A product is never taken with a cut variant list: a continuation that fails stops the page.
                var whole = await ReadWholeProductEdgeAsync(shop, edge, cancellationToken);
                if (!whole.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(whole.Error!, whole.RateLimit ?? result.RateLimit);
                items.Add(MapProduct(whole.Value));
            }
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

    private const string CatalogVariantFields = "id sku barcodes(first:1) { nodes { value } } price compareAtPrice inventoryQuantity selectedOptions { name value } image { url }";
    // B-10: a product's variants beyond the first 250 are read on by the product's id and cursor.
    internal const string ProductVariantsContinuationQuery = "query($id:ID!, $after:String) { product(id:$id) { variants(first:250, after:$after) { nodes { " + CatalogVariantFields + " } pageInfo { hasNextPage endCursor } } } }";
    internal const string ProductVariantsIncompleteCode = "SHOPIFY_PRODUCT_VARIANTS_INCOMPLETE";
    private static readonly AdapterError ProductVariantsIncomplete = new(AdapterErrorClass.ContractViolation, ProductVariantsIncompleteCode, "Shopify ürününün devam eden varyant sayfaları okunamadı; ürün eksik varyantla alınmadı.", (int)HttpStatusCode.BadGateway, null, null);

    // The product edge with its node's variants read to their end; returned as it came when no further page follows.
    internal async Task<AdapterResult<JsonElement>> ReadWholeProductEdgeAsync(ShopifyRequestContext shop, JsonElement edge, CancellationToken cancellationToken)
    {
        var product = edge.ValueKind == JsonValueKind.Object && edge.TryGetProperty("node", out var node) ? node : default;
        if (product.ValueKind != JsonValueKind.Object || !product.TryGetProperty("variants", out var variants) || !HasNextPage(variants))
            return AdapterResult<JsonElement>.Success(edge);
        var productId = TextValue(product, "id");
        var whole = await WithWholeConnectionAsync(product, "variants",
            after => QueryAsync(shop, ProductVariantsContinuationQuery, new { id = productId, after }, cancellationToken),
            root => root.GetProperty("product").GetProperty("variants"), ProductVariantsIncomplete);
        return whole.IsSuccess
            ? AdapterResult<JsonElement>.Success(WithProperty(edge, "node", JsonNode.Parse(whole.Value.GetRawText())))
            : whole;
    }

    public async Task<AdapterResult<RemoteProduct?>> FindByBarcodeAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        var result = await ListCatalogAsync(context, new(null, 50), new(null, barcode.Trim()), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteProduct?>.Failure(result.Error!, result.RateLimit);
        var match = result.Value!.Items.SelectMany(product => product.Variants.Select(variant => new RemoteProduct(product.ExternalProductId, variant.ExternalVariantId, variant.Barcode, variant.Sku, variant.RawJson))).FirstOrDefault(x => string.Equals(x.Barcode, barcode.Trim(), StringComparison.OrdinalIgnoreCase));
        return AdapterResult<RemoteProduct?>.Success(match, result.RateLimit);
    }

    internal static RemoteCatalogProduct MapProduct(JsonElement edge)
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
        var images = ProductImageUrls(product);
        var currency = (product.TryGetProperty("priceRangeV2", out var priceRange) || product.TryGetProperty("priceRange", out priceRange))
            && priceRange.ValueKind == JsonValueKind.Object
            && priceRange.TryGetProperty("minVariantPrice", out var minPrice)
            && minPrice.ValueKind == JsonValueKind.Object
            && minPrice.TryGetProperty("currencyCode", out var currencyElement)
            ? currencyElement.GetString()
            : null;
        // Sale state from the product status, which "controls visibility across all sales channels": DRAFT and ARCHIVED
        // are closed. ACTIVE is not proof of sale (a sales channel may not publish it; publications are not read), so it
        // stays unknown.
        var (saleState, saleReason) = isDraft ? (ListingSaleState.OffSale, ListingSaleState.Draft)
            : archived ? (ListingSaleState.OffSale, ListingSaleState.Archived)
            : ((string?)null, (string?)null);
        var variants = product.GetProperty("variants").GetProperty("nodes").EnumerateArray().Select((variant, index) => MapVariant(variant, index, archived, currency) with { SaleState = saleState, SaleStateReason = saleReason }).ToList();
        return new(productId, null, product.GetProperty("title").GetString() ?? productId, product.GetProperty("descriptionHtml").GetString() ?? "", product.GetProperty("vendor").GetString(), product.GetProperty("vendor").GetString(), categoryId, categoryName, images, variants, product.GetRawText(), isDraft);
    }

    private static RemoteCatalogVariant MapVariant(JsonElement variant, int index, bool productArchived, string? currency)
    {
        var options = variant.GetProperty("selectedOptions").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString() ?? $"Seçenek {index + 1}", x => x.GetProperty("value").GetString() ?? "", StringComparer.OrdinalIgnoreCase);
        var inventory = DecimalOrNull(variant, "inventoryQuantity");
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
                var quantity = DecimalOrNull(quantities, "quantity") ?? 0m;
                return new RemoteInventoryLevel(ShortId(location.GetProperty("id").GetString()), location.TryGetProperty("name", out var locationName) ? locationName.GetString() : null, quantity, level.GetRawText());
            }).ToList()
            : [];
        var barcode = VariantBarcode(variant);
        // Shopify has no model-code field. SKU is the stock code and barcode
        // is the only identity used when an existing Ravencia product is
        // matched. Keep those values separate instead of mirroring barcode
        // into the shared model-code field.
        return new(ShortId(variant.GetProperty("id").GetString()), variant.GetProperty("sku").GetString() ?? ShortId(variant.GetProperty("id").GetString()), barcode, null, options, productArchived, price, compareAt, null, inventory, currency, variant.GetRawText(), image is null ? [] : [image], levels);
    }

    // C26: Product.images is deprecated; media nodes that are images (MediaImage) carry image.url. The legacy shape is
    // still read so stored snapshots and older fixtures keep mapping.
    private static List<string> ProductImageUrls(JsonElement product)
    {
        if (product.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
            return media.GetProperty("nodes").EnumerateArray()
                .Select(node => NestedText(node, "image", "url"))
                .Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url!).ToList();
        return product.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object
            ? images.GetProperty("nodes").EnumerateArray().Select(x => TextValue(x, "url")).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList()
            : [];
    }

    // C26: ProductVariant.barcode is deprecated in favour of the barcodes connection; the first barcode is the variant's.
    internal static string? VariantBarcode(JsonElement variant)
    {
        if (variant.TryGetProperty("barcodes", out var barcodes) && barcodes.ValueKind == JsonValueKind.Object
            && barcodes.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            return nodes.EnumerateArray().Select(node => TextValue(node, "value")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return variant.TryGetProperty("barcode", out var barcode) && barcode.ValueKind == JsonValueKind.String ? barcode.GetString() : null;
    }

    internal static IReadOnlyList<RemoteReferenceItem> MapProductTypes(JsonElement productTypes)
    {
        // The store's product types are its own flat category list: the type text is the
        // identity, so a mapping survives every resync while the store keeps the type. The
        // identity keeps the exact text, since publication writes it back as productType.
        var items = new List<RemoteReferenceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in productTypes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.String) throw new JsonException("Shopify product type is not a string.");
            var productType = node.GetString();
            if (string.IsNullOrWhiteSpace(productType) || !seen.Add(productType)) continue;
            var name = productType.Trim();
            items.Add(new("CATEGORIES", productType, null, name, name, 0, true, true, JsonSerializer.Serialize(productType)));
        }
        return items;
    }

    private static bool TryBuildProductQuery(ShopifyRequestContext context, ProductReadFilter filter, out string? query, out string? error)
    {
        var parts = new List<string>();
        error = null;
        if (filter.ModifiedAfter is { } modified) parts.Add($"updated_at:>='{modified.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'");
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
}
