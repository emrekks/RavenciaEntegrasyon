using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed class HepsiburadaOptions
{
    public const string SectionName = "Hepsiburada";
    public Uri SitOrdersBaseAddress { get; init; } = new("https://oms-external-sit.hepsiburada.com/");
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class HepsiburadaAuthenticationHandler(AppDbContext db, IDataProtectionProvider dataProtection, IOptions<HepsiburadaOptions> options)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");

    public async Task<HepsiburadaRequestContext?> LoadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == HepsiburadaContract.PlatformCode && x.Environment == "STAGE",
            cancellationToken);
        var credential = await db.PlatformCredentials.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection is null || credential is null) return null;

        try
        {
            var payload = JsonSerializer.Deserialize<CredentialPayload>(_protector.Unprotect(credential.ProtectedPayload));
            var settings = JsonSerializer.Deserialize<ConnectionSettings>(connection.SettingsJson);
            if (payload is null || settings is null || string.IsNullOrWhiteSpace(settings.UserAgentIdentity)) return null;
            if (!string.Equals(payload.Username, connection.ExternalStoreId, StringComparison.OrdinalIgnoreCase)) return null;
            return new(connection, options.Value.SitOrdersBaseAddress, payload.Username, payload.Password, settings.UserAgentIdentity);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private sealed record CredentialPayload(string Username, string Password);
    private sealed record ConnectionSettings(string UserAgentIdentity, bool ExternalWritesEnabled);
}

public sealed class HepsiburadaConnectionProbe(IHttpClientFactory clients, HepsiburadaAuthenticationHandler authentication, IOptions<HepsiburadaOptions> options, TimeProvider timeProvider)
{
    public async Task<AdapterResult<HepsiburadaProbeEvidence>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return Failure("HEPSIBURADA_CONFIGURATION_UNAVAILABLE", "Hepsiburada SIT bağlantısı veya şifreli credential kullanılamıyor.", AdapterErrorClass.Authentication, 422);

        var relative = $"orders/merchantid/{Uri.EscapeDataString(authorized.Connection.ExternalStoreId)}?offset=0&limit=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(authorized.BaseAddress, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{authorized.Username}:{authorized.Password}")));
        request.Headers.TryAddWithoutValidation("User-Agent", authorized.UserAgentIdentity);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(options.Value.Timeout);

        try
        {
            using var response = await clients.CreateClient("Hepsiburada").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
            var rate = new RateLimitMetadata(
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && int.TryParse(remaining.FirstOrDefault(), out var remainingValue) ? remainingValue : null,
                null,
                retryAfter);
            if (!response.IsSuccessStatusCode)
                return AdapterResult<HepsiburadaProbeEvidence>.Failure(HepsiburadaErrorClassifier.FromHttpStatus((int)response.StatusCode, retryAfter, requestId), rate);

            var body = await response.Content.ReadAsByteArrayAsync(linked.Token);
            if (body.Length is 0 or > 1_048_576 || !HepsiburadaSitEnvelope.TryValidate(body, out var itemCount))
                return Failure("HEPSIBURADA_CONTRACT_VIOLATION", "Hepsiburada SIT bağlantı yanıtı doğrulanmış anonim zarfla eşleşmedi.", AdapterErrorClass.ContractViolation, (int)response.StatusCode, rate);

            var identity = new ConnectionIdentity(HepsiburadaContract.PlatformCode, authorized.Connection.Environment, authorized.Connection.ExternalStoreId, HepsiburadaContract.DocumentedApiVersion, authorized.Connection.ExternalStoreId);
            var evidence = new HepsiburadaProbeEvidence(identity, Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), itemCount, timeProvider.GetUtcNow());
            return AdapterResult<HepsiburadaProbeEvidence>.Success(evidence, rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AdapterResult<HepsiburadaProbeEvidence>.Failure(HepsiburadaErrorClassifier.Timeout());
        }
        catch (HttpRequestException)
        {
            return Failure("HEPSIBURADA_NETWORK_ERROR", "Hepsiburada SIT bağlantısına ulaşılamadı.", AdapterErrorClass.TransientNetwork, null);
        }
    }

    private static AdapterResult<HepsiburadaProbeEvidence> Failure(string code, string message, AdapterErrorClass errorClass, int? status, RateLimitMetadata? rate = null) =>
        AdapterResult<HepsiburadaProbeEvidence>.Failure(new(errorClass, code, message, status, TimeSpan.FromSeconds(5), null), rate);
}

public static class HepsiburadaSitEnvelope
{
    public static bool TryValidate(ReadOnlySpan<byte> utf8Json, out int itemCount)
    {
        itemCount = 0;
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return false;
            foreach (var required in new[] { "limit", "offset", "pageCount", "totalCount" })
                if (!root.TryGetProperty(required, out var value) || value.ValueKind != JsonValueKind.Number) return false;
            itemCount = items.GetArrayLength();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record HepsiburadaRequestContext(PlatformConnection Connection, Uri BaseAddress, string Username, string Password, string UserAgentIdentity);
public sealed record HepsiburadaProbeEvidence(ConnectionIdentity Identity, string ResponseSha256, int ItemCount, DateTimeOffset VerifiedAt);
