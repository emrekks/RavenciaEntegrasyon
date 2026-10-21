using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

// Shopify order notifications: one webhook subscription per topic, all pointing at the panel's
// address. Shopify signs every delivery with the app's client secret; the address carries no
// Basic Auth. The panel keeps the subscription ids joined with commas as one external id.
public sealed partial class ShopifyHttpClient : IWebhookRegistrationPort
{
    // FULFILLMENTS_* would need read_fulfillments; order topics already report fulfilment changes.
    internal static readonly string[] WebhookTopics = ["ORDERS_CREATE", "ORDERS_UPDATED", "ORDERS_CANCELLED", "ORDERS_FULFILLED", "ORDERS_PARTIALLY_FULFILLED", "REFUNDS_CREATE"];
    internal const char WebhookIdSeparator = ',';
    private const string WebhookGidPrefix = "gid://shopify/WebhookSubscription/";

    // Ids are kept as their number so six fit the panel column; Shopify is given the full gid.
    internal static string? CompactWebhookId(string? id) => id is not null && id.StartsWith(WebhookGidPrefix, StringComparison.Ordinal) ? id[WebhookGidPrefix.Length..] : id;
    internal static string WebhookGid(string id) => id.StartsWith("gid://", StringComparison.Ordinal) ? id : WebhookGidPrefix + id;

    public async Task<AdapterResult<IReadOnlyList<RemoteWebhook>>> ListWebhooksAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<IReadOnlyList<RemoteWebhook>>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        var result = await QueryAsync(shop, "query{webhookSubscriptions(first:100){nodes{id topic uri}}}", null, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<IReadOnlyList<RemoteWebhook>>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        var webhooks = new List<RemoteWebhook>();
        foreach (var node in document.RootElement.GetProperty("webhookSubscriptions").GetProperty("nodes").EnumerateArray())
        {
            var id = CompactWebhookId(node.GetProperty("id").GetString());
            if (string.IsNullOrWhiteSpace(id)) continue;
            // Shopify removes a subscription whose deliveries keep failing, so a listed one is active.
            webhooks.Add(new(id, node.TryGetProperty("uri", out var uri) ? uri.GetString() : null, "ACTIVE", null, node.TryGetProperty("topic", out var topic) ? topic.GetString() : null));
        }
        return AdapterResult<IReadOnlyList<RemoteWebhook>>.Success(webhooks, result.RateLimit);
    }

    // Creates a subscription for every topic. If one is refused, those already made are removed.
    public async Task<AdapterResult<string>> CreateWebhookAsync(AdapterContext context, string url, string username, string password, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<string>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        var created = new List<string>();
        foreach (var topic in WebhookTopics)
        {
            var result = await CreateSubscriptionAsync(shop, topic, url, cancellationToken);
            if (result.IsSuccess) { created.Add(result.Value!); continue; }
            foreach (var id in created) await DeleteSubscriptionAsync(shop, id, cancellationToken);
            return AdapterResult<string>.Failure(result.Error!, result.RateLimit);
        }
        return AdapterResult<string>.Success(string.Join(WebhookIdSeparator, created));
    }

    public async Task<AdapterResult<bool>> DeleteWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<bool>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        foreach (var id in externalId.Split(WebhookIdSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var deleted = await DeleteSubscriptionAsync(shop, id, cancellationToken);
            if (!deleted.IsSuccess) return deleted;
        }
        return AdapterResult<bool>.Success(true);
    }

    // Shopify has no passive state; a removed subscription is created again by the upkeep instead.
    public Task<AdapterResult<bool>> ActivateWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken) =>
        Unsupported<bool>("Shopify bildirim aboneliklerinin pasif durumu yoktur.");

    internal async Task<AdapterResult<string>> CreateSubscriptionAsync(ShopifyRequestContext shop, string topic, string url, CancellationToken cancellationToken)
    {
        var result = await QueryAsync(shop,
            "mutation($topic:WebhookSubscriptionTopic!,$webhookSubscription:WebhookSubscriptionInput!){webhookSubscriptionCreate(topic:$topic,webhookSubscription:$webhookSubscription){webhookSubscription{id}userErrors{field message}}}",
            new { topic, webhookSubscription = new { uri = url, format = "JSON" } }, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<string>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        var response = document.RootElement.GetProperty("webhookSubscriptionCreate");
        if (HasUserErrors(response, out var error))
            return Fail<string>(AdapterErrorClass.Validation, "SHOPIFY_WEBHOOK_REJECTED", error ?? $"Shopify {topic} bildirimini kabul etmedi.", HttpStatusCode.UnprocessableEntity);
        var id = response.TryGetProperty("webhookSubscription", out var subscription) && subscription.ValueKind == JsonValueKind.Object ? subscription.GetProperty("id").GetString() : null;
        return string.IsNullOrWhiteSpace(id)
            ? Fail<string>(AdapterErrorClass.ContractViolation, "SHOPIFY_WEBHOOK_CONTRACT_INVALID", "Shopify bildirim aboneliğinin kimliğini döndürmedi.", HttpStatusCode.BadGateway)
            : AdapterResult<string>.Success(CompactWebhookId(id)!, result.RateLimit);
    }

    private async Task<AdapterResult<bool>> DeleteSubscriptionAsync(ShopifyRequestContext shop, string id, CancellationToken cancellationToken)
    {
        var result = await QueryAsync(shop, "mutation($id:ID!){webhookSubscriptionDelete(id:$id){deletedWebhookSubscriptionId userErrors{field message}}}", new { id = WebhookGid(id) }, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<bool>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        var response = document.RootElement.GetProperty("webhookSubscriptionDelete");
        // A subscription Shopify already removed counts as removed.
        if (HasUserErrors(response, out var error) && error?.Contains("not exist", StringComparison.OrdinalIgnoreCase) != true)
            return Fail<bool>(AdapterErrorClass.Validation, "SHOPIFY_WEBHOOK_DELETE_REJECTED", error ?? "Shopify bildirim aboneliğini silmedi.", HttpStatusCode.UnprocessableEntity);
        return AdapterResult<bool>.Success(true, result.RateLimit);
    }

    // Shopify removes a subscription after repeated failed deliveries. The topics of the panel's
    // subscriptions that are no longer listed are created again at the address the remaining ones
    // use (the panel keeps only a hash of its secret route). Returns the new id list, or null when
    // nothing was missing; fails when none is left, since the address is then unknown.
    public async Task<AdapterResult<string?>> RestoreMissingWebhooksAsync(AdapterContext context, string externalId, CancellationToken cancellationToken)
    {
        var listed = await ListWebhooksAsync(context, cancellationToken);
        if (!listed.IsSuccess) return AdapterResult<string?>.Failure(listed.Error!, listed.RateLimit);
        var ids = externalId.Split(WebhookIdSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var ours = listed.Value!.Where(webhook => ids.Contains(webhook.Id)).ToList();
        if (ours.Count == ids.Count) return AdapterResult<string?>.Success(null);
        if (ours.FirstOrDefault(webhook => !string.IsNullOrWhiteSpace(webhook.Url))?.Url is not { } url)
            return Fail<string?>(AdapterErrorClass.NotFound, "SHOPIFY_WEBHOOKS_REMOVED", "Shopify panelin bütün bildirim aboneliklerini kaldırmış; bildirimleri kapatıp yeniden açın.", HttpStatusCode.NotFound);
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<string?>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        var restored = ours.Select(webhook => webhook.Id).ToList();
        foreach (var topic in WebhookTopics.Where(topic => ours.All(webhook => webhook.Topic != topic)))
        {
            var created = await CreateSubscriptionAsync(shop, topic, url, cancellationToken);
            if (!created.IsSuccess) return AdapterResult<string?>.Failure(created.Error!, created.RateLimit);
            restored.Add(created.Value!);
        }
        return AdapterResult<string?>.Success(string.Join(WebhookIdSeparator, restored));
    }
}
