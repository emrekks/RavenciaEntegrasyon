using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Persistence;
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
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Unsupported("Aktif Hepsiburada webhook subscription bulunamadı."));

        WebhookVerifierPayload? payload;
        try { payload = JsonSerializer.Deserialize<WebhookVerifierPayload>(protector.Unprotect(subscription.ProtectedVerifierSecret)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Configuration());
        }

        if (payload is null || !Authorized(subscription.AuthenticationType, payload, headers))
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(new(AdapterErrorClass.Authentication, "WEBHOOK_AUTHENTICATION_FAILED", "Webhook kimlik doğrulaması başarısız.", 401, null, null));

        var identity = HepsiburadaWebhookIdentity.Create(rawBody);
        if (identity is null)
            return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Contract());
        return AdapterResult<VerifiedWebhookEnvelope>.Success(new(
            identity.ExternalMessageId,
            identity.PayloadHash,
            identity.ResourceType,
            Encoding.UTF8.GetString(rawBody.Span)));
    }

    private static bool Authorized(string type, WebhookVerifierPayload payload, IReadOnlyDictionary<string, string> headers)
        => IsAuthenticated(type, payload.Username, payload.Password, payload.ApiKey, headers);

    internal static bool IsAuthenticated(string type, string? username, string? password, string? apiKey, IReadOnlyDictionary<string, string> headers)
    {
        if (type == "API_KEY") return apiKey is not null && Header(headers, "x-api-key") is { } actual && Fixed(actual, apiKey);
        if (type == "BASIC_AUTHENTICATION" && username is not null && password is not null)
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            return Header(headers, "Authorization") is { } actual && Fixed(actual, expected);
        }
        return false;
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool Fixed(string actual, string expected)
    {
        var left = Encoding.UTF8.GetBytes(actual);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private sealed record WebhookVerifierPayload(string? Username, string? Password, string? ApiKey);
}

internal static class HepsiburadaWebhookIdentity
{
    public static HepsiburadaWebhookIdentityValue? Create(ReadOnlyMemory<byte> rawBody)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            string identity;
            string resourceType;
            if (HepsiburadaJsonMapper.IsClaimPackageNotification(root))
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
            var canonicalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(document.RootElement))));
            var payloadHash = Convert.ToHexString(SHA256.HashData(rawBody.Span));
            return new($"{resourceType.ToLowerInvariant()}:{identity}:{canonicalHash}", payloadHash, resourceType);
        }
        catch (JsonException) { return null; }
    }

    private static string CanonicalJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}

internal sealed record HepsiburadaWebhookIdentityValue(string ExternalMessageId, string PayloadHash, string ResourceType);
