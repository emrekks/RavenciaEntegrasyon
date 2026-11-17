using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient
{
    // Shopify can reject protected customer fields for an otherwise valid
    // order token. Keep the order/status sync useful in that case: try the
    // rich customer projection first, then a name/address projection, and
    // finally the order-only projection. A token with read_customers still
    // gets the full customer snapshot without any special configuration.
    // paymentGatewayNames (Order) and the shop's primaryDomain url: the E-Arşiv internet sale's payment means and site
    // (report item 18), kept on the customer snapshot.
    private const string OrderIdentityFields = "id name createdAt updatedAt cancelledAt currencyCode displayFinancialStatus displayFulfillmentStatus paymentGatewayNames";
    private const string ShopDomainField = "shop { primaryDomain { url } } ";
    private const string OrderRichCustomerFields = " email phone customer { id displayName firstName lastName defaultEmailAddress { emailAddress } defaultPhoneNumber { phoneNumber } } shippingAddress { firstName lastName name company address1 address2 city province provinceCode zip country phone } billingAddress { firstName lastName name company address1 address2 city province provinceCode zip country phone }";
    private const string OrderReducedCustomerFields = " customer { id displayName firstName lastName } shippingAddress { firstName lastName name company address1 address2 city province provinceCode zip country } billingAddress { firstName lastName name company address1 address2 city province provinceCode zip country }";
    private const string OrderAddressOnlyFields = " shippingAddress { firstName lastName name } billingAddress { firstName lastName name }";
    // taxesIncluded says whether the line prices carry the tax; each line's taxLines report its tax rates (fractions).
    private const string OrderFinancialFields = " taxesIncluded currentTotalPriceSet { shopMoney { amount currencyCode } } totalDiscountsSet { shopMoney { amount currencyCode } } currentTotalDiscountsSet { shopMoney { amount currencyCode } } lineItems(first:250) { nodes { id name sku quantity currentQuantity originalUnitPriceSet { shopMoney { amount currencyCode } } taxLines { rate } variant { sku barcodes(first:1) { nodes { value } } } } } fulfillments(first:50) { id status displayStatus deliveredAt createdAt updatedAt trackingInfo { number company url } events(first:50) { nodes { status happenedAt } } fulfillmentLineItems(first:250) { nodes { id quantity lineItem { id } } } } refunds(first:100) { id createdAt totalRefundedSet { shopMoney { amount currencyCode } } }";

    private static string OrderFields(string customerFields) => $"{OrderIdentityFields}{customerFields}{OrderFinancialFields}";
    private static string OrderPageQuery(string fields) => "query($first:Int!, $after:String, $query:String) { " + ShopDomainField + "orders(first:$first, after:$after, query:$query, sortKey:UPDATED_AT, reverse:false) { edges { cursor node { " + fields + " } } pageInfo { hasNextPage endCursor } } }";
    private static string SingleOrderQuery(string fields) => "query($id:ID!) { " + ShopDomainField + "order(id:$id) { " + fields + " } }";

    // The shop's primary domain (https://...) from an order query's root; null when it is missing.
    internal static string? ShopPrimaryDomainUrl(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("shop", out var shop) && shop.ValueKind == JsonValueKind.Object
            ? NestedText(shop, "primaryDomain", "url")
            : null;

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

    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        var first = Math.Clamp(page.Limit, 1, Math.Clamp(settings.OrderPageSize, 1, 250));
        var query = BuildOrderQuery(window);
        var result = await QueryOrderAsync(shop, new { first, after = page.Cursor, query }, single: false, cancellationToken);
        // Over the cost ceiling: the same page is asked again with a quarter of the orders, down to one.
        while (!result.IsSuccess && result.Error?.Code == QueryCostExceededCode && first > 1)
        {
            first = Math.Max(1, first / 4);
            result = await QueryOrderAsync(shop, new { first, after = page.Cursor, query }, single: false, cancellationToken);
        }
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(result.Error!, result.RateLimit);
        try
        {
            var orders = result.Value!.RootElement.GetProperty("orders");
            var shopUrl = ShopPrimaryDomainUrl(result.Value!.RootElement);
            // One order the mapper cannot take does not stop the page (5 Nov 2026): it is skipped with an issue and, when
            // its id is readable, read again by number (RefusedOrders). An unreadable line alone never refuses an order:
            // the line is counted on its package (UnreadableOrderLines).
            var items = new List<RemoteOrder>();
            var issues = new List<AdapterPageIssue>();
            foreach (var edge in orders.GetProperty("edges").EnumerateArray())
            {
                var node = edge.TryGetProperty("node", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
                try { items.Add(MapOrder(node, shopUrl)); }
                catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException or FormatException)
                {
                    var orderId = node.ValueKind == JsonValueKind.Object ? TextValue(node, "id") : null;
                    var name = node.ValueKind == JsonValueKind.Object ? TextValue(node, "name") : null;
                    issues.Add(RefusedOrders.Issue(orderId is null ? "unknown" : ShortId(orderId),
                        $"Shopify siparişi {name ?? orderId ?? "(kimliksiz)"} içeri alınmadı: yanıtta beklenen bir alan eksik veya okunamadı.", orderId is null ? null : ShortId(orderId)));
                }
            }
            var info = orders.GetProperty("pageInfo");
            var hasMore = info.GetProperty("hasNextPage").GetBoolean();
            return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(new(items, hasMore ? info.GetProperty("endCursor").GetString() : null, hasMore, items.Count, issues), result.RateLimit);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.ContractViolation, "SHOPIFY_ORDER_CONTRACT_INVALID", "Shopify sipariş yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<RemoteOrder>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        var id = externalOrderId.StartsWith("gid://", StringComparison.Ordinal) ? externalOrderId : $"gid://shopify/Order/{externalOrderId}";
        var result = await QueryOrderAsync(shop, new { id }, single: true, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteOrder>.Failure(result.Error!, result.RateLimit);
        var order = result.Value!.RootElement.GetProperty("order");
        if (order.ValueKind == JsonValueKind.Null) return Fail<RemoteOrder>(AdapterErrorClass.NotFound, "SHOPIFY_ORDER_NOT_FOUND", "Shopify siparişi bulunamadı.", HttpStatusCode.NotFound);
        RemoteOrder mapped;
        // Found but not readable: a contract error, not "not found" (RefusedOrders).
        try { mapped = MapOrder(order, ShopPrimaryDomainUrl(result.Value!.RootElement)); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException or FormatException)
        {
            return Fail<RemoteOrder>(AdapterErrorClass.ContractViolation, "SHOPIFY_ORDER_CONTRACT_INVALID", "Shopify siparişi bulundu ancak yanıtta beklenen bir alan eksik veya okunamadı.", HttpStatusCode.BadGateway);
        }
        var invoiceRead = await ReadInvoiceMetafieldsAsync(shop, id, cancellationToken);
        if (!invoiceRead.IsSuccess) return AdapterResult<RemoteOrder>.Failure(invoiceRead.Error!, invoiceRead.RateLimit ?? result.RateLimit);
        mapped = AttachInvoiceMetafields(mapped, invoiceRead.Value!.PdfUrl?.Value, timeProvider.GetUtcNow());
        return AdapterResult<RemoteOrder>.Success(mapped, invoiceRead.RateLimit ?? result.RateLimit);
    }
    internal static string? BuildOrderQuery(OrderPollWindow window)
    {
        var terms = new List<string>(2);
        if (window.ModifiedAfter is { } from) terms.Add($"updated_at:>='{from.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'");
        if (window.ModifiedBefore is { } until) terms.Add($"updated_at:<='{until.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'");
        return terms.Count == 0 ? null : string.Join(' ', terms);
    }
}
