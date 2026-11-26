using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient
{
    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        if (!IntegrationRuntimePolicy.AllowsManualRead(shop.Connection)) return Fail<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.NotSupported, "SHOPIFY_CONNECTION_INACTIVE", "Shopify iadeleri yalnızca etkin bağlantıdan okunabilir.", HttpStatusCode.Forbidden);
        return await PollReturnsAsync(shop, window, page, cancellationToken);
    }

    // Nested page sizes (20 returns per order, 50 lines per return) keep a page of orders nearer Shopify's 1,000-point
    // single-query ceiling; past it the page shrinks below. An order with more returns, or a return with more lines, is
    // completed by the continuation queries below (G32).
    // returnLineItems lists the ReturnLineItemType interface; fulfillmentLineItem exists only on the ReturnLineItem object,
    // so it is asked through an inline fragment (C1). returnReason is deprecated: returnReasonDefinition replaces it (C26).
    internal const string ReturnLineFields = "nodes{id quantity returnReasonNote returnReasonDefinition{handle name} ... on ReturnLineItem{fulfillmentLineItem{id lineItem{id}}}} pageInfo{hasNextPage endCursor}";
    internal const string ReturnFields = "id status createdAt returnLineItems(first:50){" + ReturnLineFields + "}";
    internal const string ReturnPageQuery = "query($first:Int!,$after:String,$query:String){orders(first:$first,after:$after,query:$query,sortKey:UPDATED_AT,reverse:false){edges{node{id name createdAt updatedAt returns(first:20){nodes{" + ReturnFields + "} pageInfo{hasNextPage endCursor}}}} pageInfo{hasNextPage endCursor}}}";
    internal const string OrderReturnsContinuationQuery = "query($id:ID!,$after:String){order(id:$id){id name createdAt returns(first:20,after:$after){nodes{" + ReturnFields + "} pageInfo{hasNextPage endCursor}}}}";
    internal const string ReturnLinesContinuationQuery = "query($id:ID!,$after:String){return(id:$id){id returnLineItems(first:100,after:$after){" + ReturnLineFields + "}}}";
    internal const string SingleReturnQuery = "query($id:ID!){return(id:$id){id status createdAt order{id name createdAt} returnLineItems(first:100){" + ReturnLineFields + "}}}";
    // A runaway continuation (Shopify repeating a cursor) stops here; the return is then reported, never silently cut.
    private const int ReturnContinuationPageLimit = 50;

    internal async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollReturnsAsync(ShopifyRequestContext shop, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var search = window.ModifiedAfter is { } from ? $"updated_at:>='{from.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'" : null;
        var first = Math.Clamp(page.Limit, 1, 100);
        var result = await QueryAsync(shop, ReturnPageQuery, new { first, after = page.Cursor, query = search }, cancellationToken);
        // Over the cost ceiling: the same page is asked again with a quarter of the orders, down to one (as for orders).
        while (!result.IsSuccess && result.Error?.Code == QueryCostExceededCode && first > 1)
        {
            first = Math.Max(1, first / 4);
            result = await QueryAsync(shop, ReturnPageQuery, new { first, after = page.Cursor, query = search }, cancellationToken);
        }
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(result.Error!, result.RateLimit);
        try
        {
            var orders = result.Value!.RootElement.GetProperty("orders");
            var items = new List<RemoteReturnClaim>();
            var rateLimit = result.RateLimit;
            DateTimeOffset? lastModifiedSeen = null;
            foreach (var edge in orders.GetProperty("edges").EnumerateArray())
            {
                var order = edge.GetProperty("node");
                // Every order of the page counts, with or without a return: the read restarts its query from the latest one.
                if (order.TryGetProperty("updatedAt", out var updated) && updated.ValueKind == JsonValueKind.String && updated.TryGetDateTimeOffset(out var updatedAt)
                    && (lastModifiedSeen is null || updatedAt > lastModifiedSeen)) lastModifiedSeen = updatedAt;
                var returns = order.GetProperty("returns");
                var returnNodes = returns.GetProperty("nodes").EnumerateArray().ToList();
                // G32: an order with more than 20 returns is read on with the order's own returns cursor.
                var continuation = await ContinueAsync(returns, after => QueryAsync(shop, OrderReturnsContinuationQuery, new { id = TextValue(order, "id"), after }, cancellationToken),
                    root => root.GetProperty("order").GetProperty("returns"), returnNodes);
                if (continuation is not null) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(continuation, rateLimit);
                foreach (var item in returnNodes)
                {
                    var lines = await ReadAllReturnLinesAsync(shop, item, cancellationToken);
                    if (!lines.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(lines.Error!, lines.RateLimit ?? rateLimit);
                    items.Add(MapShopifyReturn(item, order, lines.Value!));
                }
            }
            var info = orders.GetProperty("pageInfo");
            var hasMore = info.GetProperty("hasNextPage").GetBoolean();
            return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new(items, hasMore ? info.GetProperty("endCursor").GetString() : null, hasMore, items.Count, LastModifiedSeen: lastModifiedSeen), rateLimit);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.ContractViolation, "SHOPIFY_RETURN_CONTRACT_INVALID", "Shopify iade yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
    }

    // G32: a return with more lines than the first page holds is read on through return(id).returnLineItems(after:).
    private async Task<AdapterResult<IReadOnlyList<JsonElement>>> ReadAllReturnLinesAsync(ShopifyRequestContext shop, JsonElement item, CancellationToken cancellationToken)
    {
        var connection = item.GetProperty("returnLineItems");
        var lines = connection.GetProperty("nodes").EnumerateArray().ToList();
        var error = await ContinueAsync(connection, after => QueryAsync(shop, ReturnLinesContinuationQuery, new { id = TextValue(item, "id"), after }, cancellationToken),
            root => root.GetProperty("return").GetProperty("returnLineItems"), lines);
        return error is null ? AdapterResult<IReadOnlyList<JsonElement>>.Success(lines) : AdapterResult<IReadOnlyList<JsonElement>>.Failure(error);
    }

    // Follows a nested connection's pageInfo until it ends; nodes are cloned because each page owns its own document.
    // Returns null when the connection was read whole, otherwise the error that stopped it (incomplete when the cursor
    // is missing or the page limit is reached; a return's error when none is given).
    private static async Task<AdapterError?> ContinueAsync(JsonElement connection, Func<string?, Task<AdapterResult<JsonDocument>>> next, Func<JsonElement, JsonElement> select, List<JsonElement> nodes, AdapterError? incomplete = null)
    {
        var info = connection.TryGetProperty("pageInfo", out var pageInfo) && pageInfo.ValueKind == JsonValueKind.Object ? pageInfo : default;
        var pages = 0;
        while (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("hasNextPage", out var hasNext) && hasNext.ValueKind == JsonValueKind.True)
        {
            var after = TextValue(info, "endCursor");
            if (string.IsNullOrWhiteSpace(after) || ++pages > ReturnContinuationPageLimit)
                return incomplete ?? new(AdapterErrorClass.ContractViolation, "SHOPIFY_RETURN_CONTINUATION_INCOMPLETE", "Shopify iadesinin devam sayfaları okunamadı; iade eksik kaydedilmedi.", (int)HttpStatusCode.BadGateway, null, null);
            var page = await next(after);
            if (!page.IsSuccess) return page.Error;
            var nested = select(page.Value!.RootElement);
            nodes.AddRange(nested.GetProperty("nodes").EnumerateArray().Select(node => node.Clone()));
            info = nested.TryGetProperty("pageInfo", out var nextInfo) && nextInfo.ValueKind == JsonValueKind.Object ? nextInfo.Clone() : default;
        }
        return null;
    }

    // True when a connection says more nodes follow its first page; a connection without pageInfo reads as whole.
    internal static bool HasNextPage(JsonElement connection) =>
        connection.ValueKind == JsonValueKind.Object && connection.TryGetProperty("pageInfo", out var info) && info.ValueKind == JsonValueKind.Object
        && info.TryGetProperty("hasNextPage", out var hasNext) && hasNext.ValueKind == JsonValueKind.True;

    // B-10: reads the parent's nested connection to its end (ContinueAsync) and returns the parent carrying every node, so
    // a list cut at its first page is never taken as whole. The parent comes back as it was when nothing follows; a
    // continuation that cannot be read whole fails with its own error, or with incomplete.
    private static async Task<AdapterResult<JsonElement>> WithWholeConnectionAsync(JsonElement parent, string name, Func<string?, Task<AdapterResult<JsonDocument>>> next, Func<JsonElement, JsonElement> select, AdapterError incomplete)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var connection) || !HasNextPage(connection))
            return AdapterResult<JsonElement>.Success(parent);
        var nodes = connection.TryGetProperty("nodes", out var firstNodes) && firstNodes.ValueKind == JsonValueKind.Array ? firstNodes.EnumerateArray().ToList() : new List<JsonElement>();
        AdapterError? error;
        try
        {
            error = await ContinueAsync(connection, next, select, nodes, incomplete);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            error = incomplete;
        }
        if (error is not null) return AdapterResult<JsonElement>.Failure(error);
        var whole = new JsonObject
        {
            ["nodes"] = new JsonArray(nodes.Select(node => JsonNode.Parse(node.GetRawText())).ToArray()),
            ["pageInfo"] = new JsonObject { ["hasNextPage"] = false, ["endCursor"] = null }
        };
        return AdapterResult<JsonElement>.Success(WithProperty(parent, name, whole));
    }

    // A copy of the object with one property set.
    private static JsonElement WithProperty(JsonElement parent, string name, JsonNode? value)
    {
        var copy = JsonNode.Parse(parent.GetRawText())!.AsObject();
        copy[name] = value;
        return JsonSerializer.SerializeToElement(copy);
    }

    async Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<RemoteReturnClaim>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        var id = externalReturnId.StartsWith("gid://", StringComparison.Ordinal) ? externalReturnId : $"gid://shopify/Return/{externalReturnId}";
        var result = await QueryAsync(shop, SingleReturnQuery, new { id }, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteReturnClaim>.Failure(result.Error!, result.RateLimit);
        var item = result.Value!.RootElement.GetProperty("return");
        if (item.ValueKind == JsonValueKind.Null) return Fail<RemoteReturnClaim>(AdapterErrorClass.NotFound, "SHOPIFY_RETURN_NOT_FOUND", "Shopify iadesi bulunamadı.", HttpStatusCode.NotFound);
        var lines = await ReadAllReturnLinesAsync(shop, item, cancellationToken);
        if (!lines.IsSuccess) return AdapterResult<RemoteReturnClaim>.Failure(lines.Error!, lines.RateLimit ?? result.RateLimit);
        return AdapterResult<RemoteReturnClaim>.Success(MapShopifyReturn(item, item.GetProperty("order"), lines.Value!), result.RateLimit);
    }

    // C25: returnDeclineRequest only sends the decline note to the customer when notifyCustomer is true (default false).
    internal static object ReturnActionVariables(string action, string id, string? explanation) =>
        action == "APPROVE"
            ? new { input = new { id } }
            : new { input = new { id, declineReason = "OTHER", notifyCustomer = true, declineNote = explanation!.Trim()[..Math.Min(500, explanation.Trim().Length)] } };

    public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success([new("OTHER", "Diğer", false)]));

    public async Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<ReturnActionResult>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        if (!await CanWriteAsync(shop, context, MarketplaceCapabilities.ReturnWrite, cancellationToken)) return WriteClosed<ReturnActionResult>();
        if (string.IsNullOrWhiteSpace(command.ExternalClaimId)) return Fail<ReturnActionResult>(AdapterErrorClass.Validation, "SHOPIFY_RETURN_ID_REQUIRED", "Shopify iade kimliği zorunludur.", HttpStatusCode.BadRequest);
        var id = command.ExternalClaimId.StartsWith("gid://", StringComparison.Ordinal) ? command.ExternalClaimId : $"gid://shopify/Return/{command.ExternalClaimId}";
        var action = command.Action.Trim().ToUpperInvariant();
        var query = action switch
        {
            "APPROVE" => "mutation($input:ReturnApproveRequestInput!){returnApproveRequest(input:$input){return{id status} userErrors{field message}}}",
            "REJECT" => "mutation($input:ReturnDeclineRequestInput!){returnDeclineRequest(input:$input){return{id status} userErrors{field message}}}",
            _ => null
        };
        if (query is null) return Fail<ReturnActionResult>(AdapterErrorClass.NotSupported, "SHOPIFY_RETURN_ACTION_UNSUPPORTED", "Shopify iade işlemi APPROVE veya REJECT olmalıdır.", HttpStatusCode.NotImplemented);
        if (action == "REJECT" && string.IsNullOrWhiteSpace(command.Explanation)) return Fail<ReturnActionResult>(AdapterErrorClass.Validation, "SHOPIFY_RETURN_DECLINE_NOTE_REQUIRED", "İade reddi için müşteriye gönderilecek açıklama zorunludur.", HttpStatusCode.BadRequest);
        var variables = ReturnActionVariables(action, id, command.Explanation);
        var result = await QueryAsync(shop, query, variables, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<ReturnActionResult>.Failure(result.Error!, result.RateLimit);
        var field = action == "APPROVE" ? "returnApproveRequest" : "returnDeclineRequest";
        var response = result.Value!.RootElement.GetProperty(field);
        if (HasUserErrors(response, out var error)) return Fail<ReturnActionResult>(AdapterErrorClass.Validation, "SHOPIFY_RETURN_ACTION_REJECTED", error ?? "Shopify iade kararını reddetti.", HttpStatusCode.UnprocessableEntity);
        var returned = response.GetProperty("return");
        return AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, TextValue(returned, "status") ?? (action == "APPROVE" ? "OPEN" : "DECLINED"), null), result.RateLimit);
    }

    internal static RemoteReturnClaim MapShopifyReturn(JsonElement item, JsonElement order, IReadOnlyList<JsonElement> lineNodes)
    {
        var lines = lineNodes.Select(line =>
        {
            var fulfillment = line.TryGetProperty("fulfillmentLineItem", out var fulfillmentLine) && fulfillmentLine.ValueKind == JsonValueKind.Object ? fulfillmentLine : default;
            var orderLine = fulfillment.ValueKind == JsonValueKind.Object && fulfillment.TryGetProperty("lineItem", out var lineItem) && lineItem.ValueKind == JsonValueKind.Object ? lineItem : default;
            // An UnverifiedReturnLineItem (no fulfillmentLineItem) names no order line; it is kept with an empty order line
            // so the read records it as an issue instead of dropping it without a trace (report item 10).
            return new RemoteReturnLine(ShortId(TextValue(line, "id")), orderLine.ValueKind == JsonValueKind.Object ? ShortId(TextValue(orderLine, "id")) : string.Empty, DecimalOrNull(line, "quantity") ?? 0m);
        }).Where(line => !string.IsNullOrWhiteSpace(line.ExternalLineId)).ToArray();
        var createdAt = item.GetProperty("createdAt").GetDateTimeOffset();
        // Shopify keeps the reason on each line (returnReasonDefinition); the claim carries the first line's reason.
        var reasonLine = lineNodes.FirstOrDefault(line => NestedText(line, "returnReasonDefinition", "handle") is not null || TextValue(line, "returnReasonNote") is not null);
        var reasonCode = reasonLine.ValueKind == JsonValueKind.Object ? NestedText(reasonLine, "returnReasonDefinition", "handle") : null;
        var reasonText = reasonLine.ValueKind == JsonValueKind.Object ? TextValue(reasonLine, "returnReasonNote") ?? NestedText(reasonLine, "returnReasonDefinition", "name") : null;
        return new(ShortId(TextValue(item, "id")), ShortId(TextValue(order, "id")), TextValue(item, "status") ?? "UNKNOWN", reasonCode, reasonText, null, createdAt, lines, item.GetRawText(), OrderCreatedAt: order.GetProperty("createdAt").GetDateTimeOffset(), RequestedAt: createdAt);
    }
}
