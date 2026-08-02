using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed class ShopifyWebhookVerifier(AppDbContext db, IDataProtectionProvider dataProtection) : IWebhookVerifier
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MarketplaceHub.WebhookVerifier.v1");

    public async ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await db.WebhookSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.ConnectionId == connectionId && x.Id == subscriptionId && x.AuthenticationType == "SHOPIFY_HMAC" && x.Status == "ACTIVE", cancellationToken);
        if (subscription is null || !Header(headers, "X-Shopify-Hmac-SHA256", out var supplied) || !Header(headers, "X-Shopify-Webhook-Id", out var webhookId) || !Header(headers, "X-Shopify-Topic", out var topic)) return Failure();
        try
        {
            var secret = JsonSerializer.Deserialize<SecretPayload>(_protector.Unprotect(subscription.ProtectedVerifierSecret))?.ClientSecret;
            if (string.IsNullOrWhiteSpace(secret)) return Failure();
            if (!VerifySignature(rawBody.Span, supplied, secret)) return Failure();
            var raw = Encoding.UTF8.GetString(rawBody.Span); using var _ = JsonDocument.Parse(raw);
            return AdapterResult<VerifiedWebhookEnvelope>.Success(new(webhookId, Convert.ToHexString(SHA256.HashData(rawBody.Span)), topic, raw));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException) { return Failure(); }
    }

    private static bool Header(IReadOnlyDictionary<string, string> headers, string name, out string value) { var pair = headers.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)); value = pair.Value ?? ""; return !string.IsNullOrWhiteSpace(value); }
    public static bool VerifySignature(ReadOnlySpan<byte> rawBody, string suppliedBase64, string clientSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(clientSecret)); var expected = hmac.ComputeHash(rawBody.ToArray());
        byte[] actual; try { actual = Convert.FromBase64String(suppliedBase64); } catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
    private static AdapterResult<VerifiedWebhookEnvelope> Failure() => AdapterResult<VerifiedWebhookEnvelope>.Failure(new(AdapterErrorClass.Authentication, "SHOPIFY_WEBHOOK_SIGNATURE_INVALID", "Shopify webhook imzası doğrulanamadı.", 401, null, null));
    private sealed record SecretPayload(string ClientSecret);
}
