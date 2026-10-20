using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
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
        var subscription = await db.WebhookSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.ConnectionId == connectionId && x.Id == subscriptionId && x.Status == "ACTIVE", cancellationToken); if (subscription is null) return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Unsupported("Aktif webhook subscription bulunamadı."));
        VerifierPayload? payload; try { payload = JsonSerializer.Deserialize<VerifierPayload>(BoundSecret.UnprotectForConnection(_protector, subscription.TenantId, subscription.ConnectionId, subscription.ProtectedVerifierSecret)); } catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException) { return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Configuration()); }
        if (payload is null || !Authorized(subscription.AuthenticationType, payload, headers)) return AdapterResult<VerifiedWebhookEnvelope>.Failure(new(AdapterErrorClass.Authentication, "WEBHOOK_AUTHENTICATION_FAILED", "Webhook kimlik doğrulaması başarısız.", 401, null, null));
        var identity = TrendyolWebhookIdentity.Create(rawBody);
        if (identity is null) return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Contract());
        return AdapterResult<VerifiedWebhookEnvelope>.Success(new(identity.Value.ExternalMessageId, identity.Value.PayloadHash, "ORDERS", identity.Value.PageJson));
    }

    private static bool Authorized(string type, VerifierPayload payload, IReadOnlyDictionary<string, string> headers)
    {
        if (type == "API_KEY") return payload.ApiKey is not null && Header(headers, "x-api-key") is { } actual && Fixed(actual, payload.ApiKey);
        if (type == "BASIC_AUTHENTICATION" && payload.Username is not null && payload.Password is not null) { var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload.Username}:{payload.Password}")); return Header(headers, "Authorization") is { } actual && Fixed(actual, expected); }
        return false;
    }
    private static string? Header(IReadOnlyDictionary<string, string> headers, string name) => headers.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    private static bool Fixed(string actual, string expected) { var left = Encoding.UTF8.GetBytes(actual); var right = Encoding.UTF8.GetBytes(expected); return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right); }
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
                .Select(CanonicalJson)
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
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
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
