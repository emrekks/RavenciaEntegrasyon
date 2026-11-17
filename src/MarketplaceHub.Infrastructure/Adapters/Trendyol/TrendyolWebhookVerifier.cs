using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Webhooks;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolWebhookVerifier(AppDbContext db, IDataProtectionProvider dataProtection) : IWebhookVerifier
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MarketplaceHub.WebhookVerifier.v1");

    public async ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await db.WebhookSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.ConnectionId == connectionId && x.Id == subscriptionId && x.Status == "ACTIVE", cancellationToken); if (subscription is null) return AdapterResult<VerifiedWebhookEnvelope>.Failure(AdapterErrors.Unsupported("Aktif webhook subscription bulunamadı."));
        VerifierPayload? payload; try { payload = JsonSerializer.Deserialize<VerifierPayload>(BoundSecret.UnprotectForConnection(_protector, subscription.TenantId, subscription.ConnectionId, subscription.ProtectedVerifierSecret)); } catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException) { return AdapterResult<VerifiedWebhookEnvelope>.Failure(AdapterErrors.Configuration()); }
        if (payload is null || !Authorized(subscription.AuthenticationType, payload, headers)) return AdapterResult<VerifiedWebhookEnvelope>.Failure(WebhookAuth.AuthenticationFailed());
        var identity = TrendyolWebhookIdentity.Create(rawBody);
        if (identity is null) return AdapterResult<VerifiedWebhookEnvelope>.Failure(AdapterErrors.Contract());
        return AdapterResult<VerifiedWebhookEnvelope>.Success(new(identity.Value.ExternalMessageId, identity.Value.PayloadHash, "ORDERS", identity.Value.PageJson));
    }

    private static bool Authorized(string type, VerifierPayload payload, IReadOnlyDictionary<string, string> headers) =>
        WebhookAuth.IsAuthenticated(type, payload.Username, payload.Password, payload.ApiKey, headers);
    private sealed record VerifierPayload(string? Username, string? Password, string? ApiKey);
}

internal static class TrendyolWebhookIdentity
{
    // The documented body is a package page ({"content":[...]}); live deliveries carry one package object on its own.
    // Both are accepted and handed on as a page, so the order mapper reads them the same way.
    public static (string ExternalMessageId, string PayloadHash, string PageJson)? Create(ReadOnlyMemory<byte> rawBody)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            JsonElement[] packages;
            string pageJson;
            if (root.TryGetProperty("content", out var content))
            {
                if (content.ValueKind != JsonValueKind.Array) return null;
                packages = [.. content.EnumerateArray()];
                pageJson = Encoding.UTF8.GetString(rawBody.Span);
            }
            else if (root.TryGetProperty("orderNumber", out _))
            {
                packages = [root];
                pageJson = "{\"content\":[" + Encoding.UTF8.GetString(rawBody.Span) + "]}";
            }
            else return null;

            // Trendyol retries the complete package body and does not provide a
            // webhook event id. Normalize the package collection so whitespace,
            // object-property order, or page metadata cannot create a second job.
            var normalizedPackages = packages
                .Select(CanonicalJson.Write)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var normalized = string.Join('\n', normalizedPackages);
            var eventHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            var payloadHash = Convert.ToHexString(SHA256.HashData(rawBody.Span));
            return ($"content:{eventHash}", payloadHash, pageJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
