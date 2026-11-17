using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Webhooks;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed class HepsiburadaWebhookVerifier(AppDbContext db, IDataProtectionProvider dataProtection) : IWebhookVerifier
{
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.WebhookVerifier.v1");

    public async ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(
        ReadOnlyMemory<byte> rawBody,
        IReadOnlyDictionary<string, string> headers,
        Guid connectionId,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        var subscription = await db.WebhookSubscriptions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == connectionId && x.Id == subscriptionId && x.Status == "ACTIVE", cancellationToken);
        if (subscription is null)
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(AdapterErrors.Unsupported("Aktif Hepsiburada webhook subscription bulunamadı."));

        WebhookVerifierPayload? payload;
        try { payload = JsonSerializer.Deserialize<WebhookVerifierPayload>(BoundSecret.UnprotectForConnection(protector, subscription.TenantId, subscription.ConnectionId, subscription.ProtectedVerifierSecret)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(AdapterErrors.Configuration());
        }

        if (payload is null || !Authorized(subscription.AuthenticationType, payload, headers))
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(WebhookAuth.AuthenticationFailed());

        var identity = HepsiburadaWebhookIdentity.Create(rawBody, Header(headers, WebhookEventPath.Header));
        if (identity is null)
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(AdapterErrors.Contract());
        return AdapterResult<VerifiedWebhookEnvelope>.Success(new(
            identity.ExternalMessageId,
            identity.PayloadHash,
            identity.ResourceType,
            Encoding.UTF8.GetString(rawBody.Span)));
    }

    private static bool Authorized(string type, WebhookVerifierPayload payload, IReadOnlyDictionary<string, string> headers)
        => IsAuthenticated(type, payload.Username, payload.Password, payload.ApiKey, headers);

    internal static bool IsAuthenticated(string type, string? username, string? password, string? apiKey, IReadOnlyDictionary<string, string> headers)
        => WebhookAuth.IsAuthenticated(type, username, password, apiKey, headers);

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name) => WebhookAuth.Header(headers, name);

    private sealed record WebhookVerifierPayload(string? Username, string? Password, string? ApiKey);
}

internal static class HepsiburadaWebhookIdentity
{
    public static HepsiburadaWebhookIdentityValue? Create(ReadOnlyMemory<byte> rawBody, string? eventPath = null)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            string identity;
            string resourceType;
            if (WebhookEventPath.IsHepsiburadaOrderEvent(eventPath))
            {
                // Order events only say which order changed; the order is then read from Hepsiburada.
                identity = WebhookEventPath.Normalize(eventPath)!;
                resourceType = "ORDER_EVENT";
            }
            else if (ClaimEventIdentity(root, eventPath) is { } claimEvent)
            {
                (identity, resourceType) = claimEvent;
            }
            else if (HepsiburadaJsonMapper.IsClaimPackageNotification(root))
            {
                var package = HepsiburadaJsonMapper.ClaimPackageOrder(root);
                identity = package.Packages.Single().ExternalPackageId;
                resourceType = "CLAIM_PACKAGE";
            }
            else
            {
                var claim = HepsiburadaJsonMapper.ReturnClaim(root);
                identity = claim.ExternalClaimId;
                resourceType = "RETURNS";
            }
            var canonicalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Write(document.RootElement))));
            var payloadHash = Convert.ToHexString(SHA256.HashData(rawBody.Span));
            return new($"{resourceType.ToLowerInvariant()}:{identity}:{canonicalHash}", payloadHash, resourceType);
        }
        catch (JsonException) { return null; }
    }

    // A claim notification whose body is not a claim the mapper knows (for example the
    // pre-approval notice) is a trigger: the claims are read again from Hepsiburada.
    private static (string Identity, string ResourceType)? ClaimEventIdentity(JsonElement root, string? eventPath)
    {
        if (!WebhookEventPath.IsHepsiburadaClaimEvent(eventPath)) return null;
        // awaitingaction and awaitingpreapproval bodies carry no status date: applied as they are, they count as older
        // than a claim the panel saw change since and are skipped. The claims are read again instead.
        if (WebhookEventPath.Normalize(eventPath) is "claims/awaitingaction" or "claims/awaitingpreapproval") return (WebhookEventPath.Normalize(eventPath)!, "CLAIM_EVENT");
        try
        {
            if (HepsiburadaJsonMapper.IsClaimPackageNotification(root)) return null;
            _ = HepsiburadaJsonMapper.ReturnClaim(root);
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return (WebhookEventPath.Normalize(eventPath)!, "CLAIM_EVENT");
        }
    }
}

internal sealed record HepsiburadaWebhookIdentityValue(string ExternalMessageId, string PayloadHash, string ResourceType);
