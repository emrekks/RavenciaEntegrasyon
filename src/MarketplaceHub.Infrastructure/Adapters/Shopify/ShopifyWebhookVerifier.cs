using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed class ShopifyWebhookVerifier(AppDbContext db, IDataProtectionProvider dataProtection) : IWebhookVerifier
{
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.WebhookVerifier.v1");

    public async ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await db.WebhookSubscriptions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == connectionId && x.Id == subscriptionId && x.Status == "ACTIVE", cancellationToken);
        if (subscription is null) return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Unsupported("Aktif Shopify webhook subscription bulunamadı."));

        WebhookVerifierPayload? payload;
        try { payload = JsonSerializer.Deserialize<WebhookVerifierPayload>(protector.Unprotect(subscription.ProtectedVerifierSecret)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Configuration());
        }

        var secret = payload?.ApiKey ?? payload?.ClientSecret;
        var signature = Header(headers, "X-Shopify-Hmac-Sha256");
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature) || !VerifySignature(rawBody.Span, signature, secret))
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(new(AdapterErrorClass.Authentication, "SHOPIFY_WEBHOOK_SIGNATURE_INVALID", "Shopify webhook imzası doğrulanamadı.", 401, null, null));

        var externalMessageId = Header(headers, "X-Shopify-Event-Id");
        var topic = Header(headers, "X-Shopify-Topic") ?? "orders/update";
        if (string.IsNullOrWhiteSpace(externalMessageId))
        {
            var digest = Convert.ToHexString(SHA256.HashData(rawBody.Span));
            externalMessageId = $"{topic}:{digest}";
        }

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
        return AdapterResult<VerifiedWebhookEnvelope>.Success(new(externalMessageId.Trim(), payloadHash, "ORDERS", Encoding.UTF8.GetString(rawBody.Span)));
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
