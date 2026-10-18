using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// Trendyol order-package webhooks (integration/webhook/sellers/{sellerId}/webhooks). Trendyol
// posts the full package, in the getShipmentPackages model, to the registered URL for every
// status; an empty status list subscribes to all of them and no country list means every
// storefront. The webhook is created with the Türkiye storefront header, as the documentation asks.
public sealed partial class TrendyolHttpClient : IWebhookRegistrationPort
{
    public async Task<AdapterResult<IReadOnlyList<RemoteWebhook>>> ListWebhooksAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<IReadOnlyList<RemoteWebhook>>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.Webhooks(authorized.Connection.ExternalStoreId), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<IReadOnlyList<RemoteWebhook>>.Failure(response.Error!, response.RateLimit);
        try
        {
            using var document = JsonDocument.Parse(response.Value!);
            var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray() : default;
            var webhooks = new List<RemoteWebhook>();
            foreach (var item in items)
            {
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                webhooks.Add(new(
                    id.GetString()!,
                    item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null,
                    item.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString()!.ToUpperInvariant() : "UNKNOWN",
                    item.TryGetProperty("username", out var username) && username.ValueKind == JsonValueKind.String ? username.GetString() : null));
            }
            return AdapterResult<IReadOnlyList<RemoteWebhook>>.Success(webhooks, response.RateLimit);
        }
        catch (JsonException)
        {
            return AdapterResult<IReadOnlyList<RemoteWebhook>>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit);
        }
    }

    public async Task<AdapterResult<string>> CreateWebhookAsync(AdapterContext context, string url, string username, string password, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<string>.Failure(TrendyolErrorMapper.Configuration());
        var body = JsonContent.Create(new { url, username, password, authenticationType = "BASIC_AUTHENTICATION", subscribedStatuses = Array.Empty<string>() });
        var response = await SendAsync(authorized, HttpMethod.Post, TrendyolEndpoints.Webhooks(authorized.Connection.ExternalStoreId), body, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<string>.Failure(response.Error!, response.RateLimit);
        try
        {
            using var document = JsonDocument.Parse(response.Value!);
            return document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())
                ? AdapterResult<string>.Success(id.GetString()!, response.RateLimit)
                : AdapterResult<string>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit);
        }
        catch (JsonException)
        {
            return AdapterResult<string>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit);
        }
    }

    public async Task<AdapterResult<bool>> DeleteWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<bool>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Delete, TrendyolEndpoints.Webhook(authorized.Connection.ExternalStoreId, externalId), null, cancellationToken);
        // A webhook Trendyol no longer has is already gone.
        if (!response.IsSuccess && response.Error?.HttpStatus != 404) return AdapterResult<bool>.Failure(response.Error!, response.RateLimit);
        return AdapterResult<bool>.Success(true, response.RateLimit);
    }

    public async Task<AdapterResult<bool>> ActivateWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<bool>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Put, TrendyolEndpoints.Webhook(authorized.Connection.ExternalStoreId, externalId) + "/activate", null, cancellationToken);
        return response.IsSuccess
            ? AdapterResult<bool>.Success(true, response.RateLimit)
            : AdapterResult<bool>.Failure(response.Error!, response.RateLimit);
    }
}
