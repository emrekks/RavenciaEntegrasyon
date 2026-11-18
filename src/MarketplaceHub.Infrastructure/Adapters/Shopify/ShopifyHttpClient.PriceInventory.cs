using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient
{
    private const string PriceInventoryVariantFields = "id product{id} inventoryItem{id tracked inventoryLevels(first:250){nodes{location{id isActive} quantities(names:[\"available\"]){name quantity}} pageInfo{hasNextPage}}}";

    // An import stores a variant's numeric id ("44791234567890"); a full gid is taken as it is.
    internal static string? VariantGid(string? value)
    {
        var id = value?.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        if (id.StartsWith("gid://shopify/ProductVariant/", StringComparison.Ordinal)) return id;
        return id.All(char.IsAsciiDigit) ? $"gid://shopify/ProductVariant/{id}" : null;
    }

    public async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<RemoteOperationRef>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        if (!await CanWriteAsync(shop, context, MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite, cancellationToken)) return WriteClosed<RemoteOperationRef>();
        return await PushPriceAndInventoryAsync(shop, context, payloadJson, cancellationToken);
    }

    // The send itself, once the credential and the write gate passed (the contract fixtures drive it with canned answers).
    internal async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(ShopifyRequestContext shop, AdapterContext context, string payloadJson, CancellationToken cancellationToken)
    {
        JsonDocument payload;
        try { payload = JsonDocument.Parse(payloadJson); }
        catch (JsonException) { return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok gönderimi geçersiz JSON içeriyor.", HttpStatusCode.BadRequest); }
        using (payload)
        {
            if (!payload.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                return Fail<RemoteOperationRef>(AdapterErrorClass.Validation, "SHOPIFY_PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok gönderimi en az bir ürün satırı içermelidir.", HttpStatusCode.BadRequest);
            var results = new List<RemoteOperationLine>();
            var itemList = items.EnumerateArray().ToList();
            // ADAPTOR-DIGER-1: an error part-way through the list (a rate limit, network or server error, and also a lasting
            // one such as a refused token or a refused request) keeps what was already written. With at least one line
            // applied (a price written alone counts), the answer is those lines' results, the interrupted line and the ones
            // after it named NOT_SENT (the job records them as failed, not sent, and the same values go with the next send),
            // and a line whose price was written before its stock write was cut off named SHOPIFY_PRICE_ONLY_APPLIED (the job
            // records the price as sent and sends the stock again). With nothing applied, the whole send fails as before.
            AdapterResult<RemoteOperationRef> Interrupted(int index, AdapterError error, RateLimitMetadata? rateLimit, RemoteOperationLine? priceOnly = null)
            {
                var notSent = itemList.Skip(priceOnly is null ? index : index + 1).Select(item => TextValue(item, "barcode")?.Trim() ?? "").ToList();
                var answer = InterruptedPriceInventory(results, notSent, priceOnly, error, rateLimit, timeProvider.GetUtcNow());
                if (answer.IsSuccess)
                    logger.LogWarning("Shopify fiyat-stok gönderimi {Applied} satır yazıldıktan sonra {ErrorClass} {ErrorCode} ile kesildi; {NotSent} satır gönderilmedi.",
                        results.Count(line => line.Succeeded), error.Class, error.Code, notSent.Count);
                return answer;
            }
            for (var index = 0; index < itemList.Count; index++)
            {
                var item = itemList[index];
                cancellationToken.ThrowIfCancellationRequested();
                var barcode = TextValue(item, "barcode")?.Trim();
                var quantity = DecimalOrNull(item, "quantity");
                var salePrice = DecimalOrNull(item, "salePrice");
                var listPrice = DecimalOrNull(item, "listPrice");
                // A stock change alone carries no prices: the variant's price is left as it is.
                var priced = salePrice is not null || listPrice is not null;
                if (string.IsNullOrWhiteSpace(barcode) || quantity is null || quantity < 0 || quantity != decimal.Truncate(quantity.Value) || quantity > int.MaxValue || priced && (salePrice is null || salePrice <= 0 || listPrice is null || listPrice < salePrice))
                {
                    results.Add(new(barcode ?? "", false, null, "SHOPIFY_PRICE_INVENTORY_LINE_INVALID", false, "REJECTED"));
                    continue;
                }
                // The variant id an import stored addresses the variant directly; a variant known only by its barcode
                // (published from the panel, never imported) is searched by it.
                JsonElement variant;
                if (VariantGid(TextValue(item, "variantId")) is { } knownVariantId)
                {
                    var byId = await QueryAsync(shop, "query($id:ID!){productVariant(id:$id){" + PriceInventoryVariantFields + "}}", new { id = knownVariantId }, cancellationToken);
                    if (!byId.IsSuccess) return Interrupted(index, byId.Error!, byId.RateLimit);
                    if (!byId.Value!.RootElement.TryGetProperty("productVariant", out var found) || found.ValueKind != JsonValueKind.Object)
                    {
                        results.Add(new(barcode, false, null, "SHOPIFY_VARIANT_NOT_FOUND", false, "REJECTED"));
                        continue;
                    }
                    variant = found;
                }
                else
                {
                    var lookup = await QueryAsync(shop, "query($query:String!){productVariants(first:2,query:$query){nodes{" + PriceInventoryVariantFields + "}}}", new { query = $"barcode:\"{EscapeSearch(barcode)}\"" }, cancellationToken);
                    if (!lookup.IsSuccess) return Interrupted(index, lookup.Error!, lookup.RateLimit);
                    var variants = lookup.Value!.RootElement.GetProperty("productVariants").GetProperty("nodes");
                    if (variants.GetArrayLength() != 1)
                    {
                        results.Add(new(barcode, false, null, variants.GetArrayLength() == 0 ? "SHOPIFY_VARIANT_NOT_FOUND" : "SHOPIFY_BARCODE_AMBIGUOUS", false, "REJECTED"));
                        continue;
                    }
                    variant = variants[0];
                }
                if (!variant.TryGetProperty("inventoryItem", out var inventoryItem) || inventoryItem.ValueKind != JsonValueKind.Object || !inventoryItem.GetProperty("tracked").GetBoolean())
                {
                    results.Add(new(barcode, false, null, "SHOPIFY_INVENTORY_NOT_TRACKED", false, "REJECTED"));
                    continue;
                }
                var inventoryLevels = inventoryItem.GetProperty("inventoryLevels");
                if (inventoryLevels.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())
                {
                    results.Add(new(barcode, false, null, "SHOPIFY_INVENTORY_LOCATION_LIST_INCOMPLETE", false, "REJECTED"));
                    continue;
                }
                var activeLevels = inventoryLevels.GetProperty("nodes").EnumerateArray()
                    .Where(level => level.GetProperty("location").GetProperty("isActive").GetBoolean()).ToArray();
                if (activeLevels.Length != 1)
                {
                    results.Add(new(barcode, false, null, "SHOPIFY_INVENTORY_LOCATION_AMBIGUOUS", false, "REJECTED"));
                    continue;
                }
                // quantities is a list of {name, quantity}, one per requested name.
                if (AvailableQuantity(activeLevels[0]) is not { } compareQuantity)
                {
                    results.Add(new(barcode, false, null, "SHOPIFY_INVENTORY_QUANTITY_MISSING", false, "REJECTED"));
                    continue;
                }
                var variantId = variant.GetProperty("id").GetString()!;
                var productId = variant.GetProperty("product").GetProperty("id").GetString()!;
                var inventoryItemId = inventoryItem.GetProperty("id").GetString()!;
                var locationId = activeLevels[0].GetProperty("location").GetProperty("id").GetString()!;
                // Review of 18 Nov 2026: a refused price does not hold the stock back (the price now rides along every stock
                // send); the stock is written and the line says the price was refused (STOCK_ONLY_APPLIED).
                string? priceRefusal = null;
                if (priced)
                {
                    var priceMutation = await QueryAsync(shop, "mutation($productId:ID!,$variants:[ProductVariantsBulkInput!]!){productVariantsBulkUpdate(productId:$productId,variants:$variants){userErrors{field message}}}", new { productId, variants = new[] { new { id = variantId, price = salePrice!.Value.ToString("0.00##", CultureInfo.InvariantCulture), compareAtPrice = listPrice!.Value > salePrice.Value ? listPrice.Value.ToString("0.00##", CultureInfo.InvariantCulture) : null } } }, cancellationToken);
                    if (!priceMutation.IsSuccess) return Interrupted(index, priceMutation.Error!, priceMutation.RateLimit);
                    if (HasUserErrors(priceMutation.Value!.RootElement.GetProperty("productVariantsBulkUpdate"), out var priceError))
                        priceRefusal = priceError;
                }
                var stockMutation = await SetAvailableQuantityAsync(shop, context, barcode, inventoryItemId, locationId, (int)quantity.Value, compareQuantity, cancellationToken);
                if (!stockMutation.IsSuccess)
                    return Interrupted(index, stockMutation.Error!, stockMutation.RateLimit,
                        priced ? new RemoteOperationLine(barcode, false, variantId, PriceOnlyAppliedCode, true, PriceOnlyAppliedStatus) : null);
                if (HasUserErrors(stockMutation.Value!.RootElement.GetProperty("inventorySetQuantities"), out var stockError))
                {
                    results.Add(new(barcode, false, null, "SHOPIFY_INVENTORY_REJECTED", false, stockError));
                    continue;
                }
                results.Add(priceRefusal is null
                    ? new(barcode, true, variantId, null, false, "COMPLETED")
                    : new(barcode, false, variantId, StockOnlyAppliedCode, false, priceRefusal));
            }
            var operationId = EncodeCompletedOperation(results);
            return AdapterResult<RemoteOperationRef>.Success(new(operationId, "SHOPIFY_PRICE_INVENTORY", timeProvider.GetUtcNow()));
        }
    }

    // A line of an interrupted send that was never sent (retryable): the same values go with the next send.
    internal const string NotSentStatus = "NOT_SENT";
    // A line whose price was written before its stock write was cut off: the price is Shopify's, the stock goes again.
    internal const string PriceOnlyAppliedCode = "SHOPIFY_PRICE_ONLY_APPLIED";
    internal const string PriceOnlyAppliedStatus = "PRICE_ONLY_APPLIED";
    // A line whose stock was written while Shopify refused its price: the stock is Shopify's, the price goes again.
    internal const string StockOnlyAppliedCode = "SHOPIFY_STOCK_ONLY_APPLIED";

    // The answer of a price-stock send cut off by an error after `results` (ADAPTOR-DIGER-1, see PushPriceAndInventoryAsync).
    // Whatever the error's class: a line Shopify applied stays applied, so a lasting error (Authentication, Validation)
    // no longer throws away the results of the lines written before it. notSent: the barcodes from the interrupted line
    // on, named NOT_SENT with the interrupting error's code; priceOnly: the interrupted line when its price was written.
    internal static AdapterResult<RemoteOperationRef> InterruptedPriceInventory(IReadOnlyList<RemoteOperationLine> results, IReadOnlyList<string> notSent, RemoteOperationLine? priceOnly, AdapterError error, RateLimitMetadata? rateLimit, DateTimeOffset now)
    {
        if (!results.Any(line => line.Succeeded) && priceOnly is null) return AdapterResult<RemoteOperationRef>.Failure(error, rateLimit);
        var lines = results.ToList();
        if (priceOnly is not null) lines.Add(priceOnly);
        lines.AddRange(notSent.Select(barcode => new RemoteOperationLine(barcode, false, null, error.Code, true, NotSentStatus)));
        return AdapterResult<RemoteOperationRef>.Success(new(EncodeCompletedOperation(lines), "SHOPIFY_PRICE_INVENTORY", now), rateLimit);
    }

    // The level's "available" quantity from its quantities list; null when Shopify did not return it.
    internal static int? AvailableQuantity(JsonElement level) =>
        level.TryGetProperty("quantities", out var quantities) && quantities.ValueKind == JsonValueKind.Array
            ? quantities.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object
                    && (!entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() == "available"))
                .Select(entry => entry.TryGetProperty("quantity", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var amount) ? amount : (int?)null)
                .FirstOrDefault(amount => amount is not null)
            : null;

    // Admin API 2026-10 inventorySetQuantities: the @idempotent key is required since 2026-04 and each quantity
    // carries changeFromQuantity (the "available" amount read just before), so a retried write with the same
    // effect key is applied once and a stock that moved in between is refused.
    internal const string InventorySetQuantitiesMutation = "mutation($input:InventorySetQuantitiesInput!,$idempotencyKey:String!){inventorySetQuantities(input:$input) @idempotent(key:$idempotencyKey){userErrors{field message}}}";

    internal Task<AdapterResult<JsonDocument>> SetAvailableQuantityAsync(ShopifyRequestContext shop, AdapterContext context, string barcode, string inventoryItemId, string locationId, int quantity, int compareQuantity, CancellationToken cancellationToken)
    {
        var effect = string.IsNullOrWhiteSpace(context.IdempotencyKey) ? Guid.NewGuid().ToString("N") : context.IdempotencyKey;
        var key = IdempotencyKey($"{effect}:{barcode}:inventory");
        return QueryAsync(shop, InventorySetQuantitiesMutation, new { input = new { name = "available", reason = "correction", referenceDocumentUri = $"marketplacehub://{context.TenantId:D}/{context.ConnectionId:D}/{barcode}", quantities = new[] { new { inventoryItemId, locationId, quantity, changeFromQuantity = compareQuantity } } }, idempotencyKey = key }, cancellationToken);
    }

    internal static string IdempotencyKey(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
