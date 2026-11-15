using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Contracts;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient
{
    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken)
    {
        logger.LogInformation("Trendyol PRODUCT_CREATE request started for connection {ConnectionId} and product {ProductId}.", context.ConnectionId, publication.ProductId);
        var result = await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductCreate, publication.PayloadJson, "PRODUCT_CREATE_V2", cancellationToken);
        if (result.IsSuccess)
            logger.LogInformation("Trendyol PRODUCT_CREATE accepted for product {ProductId}; external operation {ExternalOperationId}.", publication.ProductId, result.Value!.ExternalOperationId);
        else
            logger.LogWarning("Trendyol PRODUCT_CREATE failed for product {ProductId}: {ErrorCode}.", publication.ProductId, result.Error?.Code);
        return result;
    }

    public async Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductUpdateUnapproved, publication.UnapprovedPayloadJson, "PRODUCT_UPDATE_UNAPPROVED_V2", cancellationToken);

    // Only a request whose every item names its own content goes out: one still keyed by barcode (the job did not group
    // it per content) is refused before any call, so no content is written with data meant for another.
    public async Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        !TrendyolContentGroups.IsResolved(publication.ApprovedContentPayloadJson)
            ? AdapterResult<RemoteOperationRef>.Failure(new(AdapterErrorClass.Validation, "PRODUCT_CONTENT_UNRESOLVED", "Trendyol içerik güncellemesi içerik kimliklerine ayrılmadan gönderilemez; istek gönderilmedi.", null, null, null))
            : await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductUpdateContent, publication.ApprovedContentPayloadJson, "PRODUCT_UPDATE_CONTENT_V2", cancellationToken);

    public async Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductUpdateVariant, publication.ApprovedVariantPayloadJson, "PRODUCT_UPDATE_VARIANT_V2", cancellationToken);

    public async Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductUpdateDelivery, publication.ApprovedDeliveryPayloadJson, "PRODUCT_UPDATE_DELIVERY_V2", cancellationToken);

    public async Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Trendyol PRODUCT_CREATE batch poll started for operation {ExternalOperationId}.", externalOperationId);
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteOperationStatus>.Failure(TrendyolErrorMapper.Configuration()); var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.BatchResult(authorized.Connection.ExternalStoreId, externalOperationId), null, cancellationToken); if (!response.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(response.Error!, response.RateLimit);
        try
        {
            var result = TrendyolJsonMapper.Batch(response.Value!, externalOperationId);
            logger.LogInformation("Trendyol PRODUCT_CREATE batch poll completed for operation {ExternalOperationId}: status {Status}, itemCount {ItemCount}, lines {LineCount}, accepted {AcceptedCount}, rejected {RejectedCount}, failure reasons {FailureReasons}.", externalOperationId, result.Status, result.ReportedItemCount, result.Lines.Count, result.Lines.Count(x => x.Succeeded), result.Lines.Count(x => !x.Succeeded), string.Join(" | ", result.Lines.Where(x => !x.Succeeded).Select(x => $"{x.ExternalKey}: {x.ErrorCode}").Take(20)));
            return AdapterResult<RemoteOperationStatus>.Success(result, response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemoteOperationStatus>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return AdapterResult<RemotePublicationStatus>.Failure(TrendyolErrorMapper.Contract());
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemotePublicationStatus>.Failure(TrendyolErrorMapper.Configuration());
        var encodedBarcode = Uri.EscapeDataString(barcode.Trim());
        var approved = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.ApprovedProducts(authorized.Connection.ExternalStoreId) + $"?barcode={encodedBarcode}&size=100", null, cancellationToken);
        if (!approved.IsSuccess) return AdapterResult<RemotePublicationStatus>.Failure(approved.Error!, approved.RateLimit);
        try
        {
            var approvedStatus = TrendyolJsonMapper.ApprovedPublicationStatus(approved.Value!, barcode.Trim());
            if (approvedStatus is not null) return AdapterResult<RemotePublicationStatus>.Success(approvedStatus, approved.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemotePublicationStatus>.Failure(TrendyolErrorMapper.Contract()); }

        var unapproved = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.UnapprovedProducts(authorized.Connection.ExternalStoreId) + $"?barcode={encodedBarcode}&size=1000", null, cancellationToken);
        if (!unapproved.IsSuccess) return AdapterResult<RemotePublicationStatus>.Failure(unapproved.Error!, unapproved.RateLimit);
        try
        {
            var unapprovedStatus = TrendyolJsonMapper.UnapprovedPublicationStatus(unapproved.Value!, barcode.Trim());
            return AdapterResult<RemotePublicationStatus>.Success(unapprovedStatus ?? new(barcode.Trim(), "NOT_FOUND", null, null, null, "{}"), unapproved.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemotePublicationStatus>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Put, TrendyolEndpoints.ProductArchiveState, payloadJson, "PRODUCT_ARCHIVE_STATE", cancellationToken);

    public async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.PriceAndInventory, payloadJson, "PRICE_AND_INVENTORY", cancellationToken);

    private async Task<AdapterResult<RemoteOperationRef>> SubmitBatchAsync(AdapterContext context, HttpMethod method, Func<string, string> endpointFactory, string payloadJson, string kind, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Configuration()); if (!CanWrite(authorized, context)) return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.WriteClosed());
        JsonDocument payload; try { payload = JsonDocument.Parse(payloadJson); } catch (JsonException) { return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract()); }
        using (payload)
        {
            var response = await SendAsync(authorized, method, endpointFactory(authorized.Connection.ExternalStoreId), JsonContent.Create(payload.RootElement), cancellationToken); if (!response.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(response.Error!, response.RateLimit);
            try
            {
                if (!TrendyolContractGuard.HasBatchRequestId(response.Value!)) return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract());
                using var document = JsonDocument.Parse(response.Value!);
                var id = document.RootElement.GetProperty("batchRequestId").GetString()!;
                return AdapterResult<RemoteOperationRef>.Success(new(id, kind, timeProvider.GetUtcNow()), response.RateLimit);
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract());
            }
        }
    }
}
