using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolWebhookVerifier(AppDbContext db, IDataProtectionProvider dataProtection) : IWebhookVerifier
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MarketplaceHub.WebhookVerifier.v1");

    public async ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await db.WebhookSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.ConnectionId == connectionId && x.Id == subscriptionId && x.Status == "ACTIVE", cancellationToken); if (subscription is null) return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Unsupported("Aktif webhook subscription bulunamadı."));
        VerifierPayload? payload; try { payload = JsonSerializer.Deserialize<VerifierPayload>(_protector.Unprotect(subscription.ProtectedVerifierSecret)); } catch (Exception exception) when (exception is CryptographicException or JsonException) { return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Configuration()); }
        if (payload is null || !Authorized(subscription.AuthenticationType, payload, headers)) return AdapterResult<VerifiedWebhookEnvelope>.Failure(new(AdapterErrorClass.Authentication, "WEBHOOK_AUTHENTICATION_FAILED", "Webhook kimlik doğrulaması başarısız.", 401, null, null));
        try { using var document = JsonDocument.Parse(rawBody); if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Contract()); }
        catch (JsonException) { return AdapterResult<VerifiedWebhookEnvelope>.Failure(TrendyolErrorMapper.Contract()); }
        var hash = Convert.ToHexString(SHA256.HashData(rawBody.Span)); return AdapterResult<VerifiedWebhookEnvelope>.Success(new(hash, hash, "ORDERS", Encoding.UTF8.GetString(rawBody.Span)));
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
