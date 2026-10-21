using System.Net;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Return view (phase 8, read only). PTT AVM has no return service: a returned order line shows as SiparisDurumu
// "iade" in the order list, after PTT AVM has decided the return in its own panel. Every order with such lines
// becomes one return record (iade:{order number}) carrying those lines, already completed. Return decisions are
// never sent from the panel: the action call is refused.
public sealed partial class PttAvmHttpClient : IReturnPort
{
    internal const string ReturnClaimPrefix = "iade:";

    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var orderPage = await PollAsync(context, new OrderPollWindow(window.ModifiedAfter, window.ModifiedBefore), page, cancellationToken);
        if (!orderPage.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(orderPage.Error!, orderPage.RateLimit);
        var claims = orderPage.Value!.Items.Select(ReturnClaim).OfType<RemoteReturnClaim>().ToList();
        return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new(claims, null, false, claims.Count), orderPage.RateLimit);
    }

    // Explicit: IOrderPort.GetAsync has the same parameters on this class.
    async Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken)
    {
        var orderNumber = externalReturnId.Trim();
        if (orderNumber.StartsWith(ReturnClaimPrefix, StringComparison.Ordinal)) orderNumber = orderNumber[ReturnClaimPrefix.Length..];
        var order = await GetAsync(context, orderNumber, cancellationToken);
        if (!order.IsSuccess) return AdapterResult<RemoteReturnClaim>.Failure(order.Error!, order.RateLimit);
        return ReturnClaim(order.Value!) is { } claim
            ? AdapterResult<RemoteReturnClaim>.Success(claim, order.RateLimit)
            : Failure<RemoteReturnClaim>(AdapterErrorClass.NotFound, "PTTAVM_RETURN_NOT_FOUND", $"PTT AVM'de {orderNumber} numaralı siparişte iade edilen satır yok.", HttpStatusCode.NotFound);
    }

    public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success([]));

    public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<ReturnActionResult>("PTTAVM_RETURN_ACTION_UNSUPPORTED", "PTT AVM iade kararları PTT AVM satıcı panelinden verilir; panelden PTT AVM'ye iade işlemi gönderilmez."));

    // The returned lines of an order as one completed return; null when no line is returned.
    internal static RemoteReturnClaim? ReturnClaim(RemoteOrder order)
    {
        var returned = order.Lines.Where(line => line.RawStatus == "Returned").ToList();
        if (returned.Count == 0) return null;
        var lines = returned.Select(line => new RemoteReturnLine(line.ExternalLineId, line.ExternalLineId, line.Quantity, RawStatus: "Completed")).ToList();
        var raw = new JsonObject
        {
            ["source"] = "PTTAVM_ORDER_LINE_STATUS",
            ["orderNumber"] = order.OrderNumber,
            ["lines"] = new JsonArray([.. returned.Select(line => (JsonNode)new JsonObject { ["lineItemId"] = line.ExternalLineId, ["barcode"] = line.Barcode, ["title"] = line.Title, ["quantity"] = line.Quantity })])
        };
        var package = order.Packages.FirstOrDefault();
        return new($"{ReturnClaimPrefix}{order.OrderNumber}", order.ExternalOrderId, "Completed", null, null, null, order.LastModifiedAt, lines, raw.ToJsonString(),
            CargoProviderName: package?.CargoProviderExternalId, CargoTrackingNumber: package?.CargoTrackingNumber, OrderCreatedAt: order.OrderedAt);
    }
}
