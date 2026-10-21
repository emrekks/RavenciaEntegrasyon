using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed class ShopifyWebhookVerifier(AppDbContext db, IDataProtectionProvider dataProtection, TimeProvider? timeProvider = null) : IWebhookVerifier
{
    // A delivery whose X-Shopify-Triggered-At is further than this from now is acknowledged and dropped (G4).
    internal static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);
    // MarketplaceWebhookService answers this code with 200 and enqueues nothing: Shopify does not retry it, and the
    // order still arrives through the scheduled order check.
    internal const string StaleCode = "SHOPIFY_WEBHOOK_STALE";
    // The topics the panel subscribes to (ShopifyHttpClient.WebhookTopics). The header is not signed, so any other
    // value counts as one topic and cannot be varied to slip a replayed body past the deduplication.
    private static readonly string[] KnownTopics = ["orders/create", "orders/updated", "orders/cancelled", "orders/fulfilled", "orders/partially_fulfilled", "refunds/create"];
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.WebhookVerifier.v1");
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await db.WebhookSubscriptions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == connectionId && x.Id == subscriptionId && x.Status == "ACTIVE", cancellationToken);
        if (subscription is null) return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Unsupported("Aktif Shopify webhook subscription bulunamadı."));

        WebhookVerifierPayload? payload;
        try { payload = JsonSerializer.Deserialize<WebhookVerifierPayload>(BoundSecret.UnprotectForConnection(protector, subscription.TenantId, subscription.ConnectionId, subscription.ProtectedVerifierSecret)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Configuration());
        }

        // Shopify signs with the app's client secret; the API key field is an older place for it.
        // A subscription made without the secret (ROUTE_TOKEN) is admitted by its secret route alone:
        // the delivery only names an order, which the panel then reads from Shopify itself.
        var secret = payload?.ClientSecret ?? payload?.ApiKey;
        var signature = Header(headers, "X-Shopify-Hmac-Sha256");
        var routeOnly = subscription.AuthenticationType == "ROUTE_TOKEN" && string.IsNullOrWhiteSpace(secret);
        if (!routeOnly && (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature) || !VerifySignature(rawBody.Span, signature, secret)))
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(new(AdapterErrorClass.Authentication, "SHOPIFY_WEBHOOK_SIGNATURE_INVALID", "Shopify webhook imzası doğrulanamadı.", 401, null, null));

        // Only the body is signed (and on a ROUTE_TOKEN subscription nothing is): X-Shopify-Webhook-Id and the other
        // headers can be changed freely, so a captured delivery sent again under a new id would pass a header-based
        // deduplication. The id is therefore the topic and the body's digest; a retry of one delivery carries the same
        // body and is still recognised. A delivery that names its trigger time is accepted only within five minutes of
        // it; one without the header is kept (older deliveries and the route-only mode do not always send it).
        var topic = Header(headers, "X-Shopify-Topic") ?? "orders/updated";
        if (!IsFresh(Header(headers, "X-Shopify-Triggered-At"), time.GetUtcNow()))
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(new(AdapterErrorClass.Validation, StaleCode, "Shopify bildirimi beş dakikadan eski ya da ileri tarihli; işlenmedi.", 200, null, null));
        var externalMessageId = MessageId(topic, rawBody.Span);

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Contract());
        }
        catch (JsonException)
        {
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Contract());
        }

        var payloadHash = Convert.ToHexString(SHA256.HashData(rawBody.Span));
        // A refund's id is the refund's; it names its order in order_id.
        var resourceType = topic.StartsWith("refunds/", StringComparison.OrdinalIgnoreCase) ? "REFUNDS" : "ORDERS";
        return AdapterResult<VerifiedWebhookEnvelope>.Success(new(externalMessageId, payloadHash, resourceType, Encoding.UTF8.GetString(rawBody.Span)));
    }

    internal static string MessageId(string? topic, ReadOnlySpan<byte> rawBody)
    {
        var normalized = (topic ?? string.Empty).Trim().ToLowerInvariant();
        var key = Array.IndexOf(KnownTopics, normalized) >= 0 ? normalized : "other";
        return $"{key}:{Convert.ToHexString(SHA256.HashData(rawBody))}";
    }

    // Missing or unreadable: fresh (the header is optional). Otherwise within the window on either side of now.
    internal static bool IsFresh(string? triggeredAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(triggeredAt)
            || !DateTimeOffset.TryParse(triggeredAt.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            return true;
        return (now - at).Duration() <= FreshnessWindow;
    }

    private static bool VerifySignature(ReadOnlySpan<byte> rawBody, string actualBase64, string secret)
    {
        try
        {
            var actual = Convert.FromBase64String(actualBase64.Trim());
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name) => headers.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    private sealed record WebhookVerifierPayload(string? Username, string? Password, string? ApiKey, string? ClientSecret);
}
