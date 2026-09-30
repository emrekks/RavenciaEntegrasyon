using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed class HepsiburadaAuthenticationHandler(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    IOptions<HepsiburadaOptions> options,
    ILogger<HepsiburadaAuthenticationHandler> logger)
{
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");
    private readonly HepsiburadaOptions settings = options.Value;

    public async Task<HepsiburadaRequestContext?> LoadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "HEPSIBURADA" && x.ApiVersion == "V1.0",
            cancellationToken);
        if (connection is null) return null;

        var credential = await db.PlatformCredentials.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (credential is null) return null;

        CredentialPayload? payload;
        try { payload = JsonSerializer.Deserialize<CredentialPayload>(protector.Unprotect(credential.ProtectedPayload)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            logger.LogWarning(exception, "Hepsiburada credential çözülemedi. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.ApiKey) || string.IsNullOrWhiteSpace(payload.ApiSecret)) return null;
        if (!TryResolveBaseAddresses(connection.Environment, out var omsBaseAddress, out var listingBaseAddress))
        {
            logger.LogWarning("Hepsiburada bağlantısı için geçersiz ortam yapılandırması: {Environment}. ConnectionId: {ConnectionId}", connection.Environment, connectionId);
            return null;
        }

        var credentials = ResolveBasicCredentials(payload.ApiKey, payload.ApiSecret);
        return new(connection, omsBaseAddress, listingBaseAddress, credentials.Username, credentials.Password)
        {
            IntegratorName = payload.IntegratorName ?? credentials.Username,
            CatalogBaseAddress = ResolveCatalogBaseAddress(connection.Environment),
            StageTestOrderBaseAddress = string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) ? settings.StageTestOrderBaseAddress : null
        };
    }

    internal static (string Username, string Password) ResolveBasicCredentials(string integratorUsername, string serviceKey) =>
        (integratorUsername.Trim(), serviceKey.Trim());

    internal static string? MerchantIdUsernameFallback(string currentUsername, string merchantId)
    {
        var candidate = merchantId.Trim();
        return candidate.Length == 0 || string.Equals(currentUsername.Trim(), candidate, StringComparison.OrdinalIgnoreCase)
            ? null
            : candidate;
    }

    public async Task<bool> SaveBasicUsernameAsync(Guid tenantId, Guid connectionId, string username, CancellationToken cancellationToken)
    {
        var credential = await db.PlatformCredentials
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.CredentialType == "BASIC" && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (credential is null) return false;

        CredentialPayload? payload;
        try { payload = JsonSerializer.Deserialize<CredentialPayload>(protector.Unprotect(credential.ProtectedPayload)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            logger.LogWarning(exception, "Hepsiburada credential çözülemedi; doğrulanan Basic kullanıcı adı saklanamadı. ConnectionId: {ConnectionId}", connectionId);
            return false;
        }
        if (payload is null || string.IsNullOrWhiteSpace(payload.ApiSecret) || string.IsNullOrWhiteSpace(username)) return false;
        if (string.Equals(payload.ApiKey, username, StringComparison.Ordinal)) return true;

        credential.ProtectedPayload = protector.Protect(JsonSerializer.Serialize(payload with { ApiKey = username, IntegratorName = payload.IntegratorName ?? payload.ApiKey.Trim() }));
        credential.Version++;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Hepsiburada bağlantısı için doğrulanan Basic kullanıcı adı şifreli kayda alındı. ConnectionId: {ConnectionId}", connectionId);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(credential).State = EntityState.Detached;
            logger.LogInformation("Hepsiburada credential eşzamanlı değişti; doğrulanan Basic kullanıcı adı saklanmadı. ConnectionId: {ConnectionId}", connectionId);
            return false;
        }
    }

    public async Task<bool> HasVerifiedWriteEvidenceAsync(PlatformConnection connection, CancellationToken cancellationToken, params string[] capabilityCodes)
    {
        var capabilities = await db.PlatformCapabilities.AsNoTracking()
            .Where(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.Id && capabilityCodes.Contains(x.Code))
            .ToListAsync(cancellationToken);
        return capabilityCodes.All(code => CapabilityEvidencePolicy.IsVerifiedWriteCapability(capabilities.SingleOrDefault(x => x.Code == code), connection, code));
    }

    private Uri? ResolveCatalogBaseAddress(string environment)
    {
        var address = string.Equals(environment, "STAGE", StringComparison.OrdinalIgnoreCase)
            ? settings.StageCatalogBaseAddress
            : string.Equals(environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase)
                ? settings.ProductionCatalogBaseAddress
                : null;
        if (address is null) return null;
        return address.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? address : new Uri(address.AbsoluteUri + "/");
    }

    private bool TryResolveBaseAddresses(string environment, out Uri oms, out Uri listing)
    {
        if (string.Equals(environment, "STAGE", StringComparison.OrdinalIgnoreCase))
        {
            oms = settings.StageOmsBaseAddress;
            listing = settings.StageListingBaseAddress;
            return true;
        }
        if (string.Equals(environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase))
        {
            oms = settings.ProductionOmsBaseAddress;
            listing = settings.ProductionListingBaseAddress;
            return true;
        }
        oms = new Uri("https://invalid.local");
        listing = new Uri("https://invalid.local");
        return false;
    }

    private sealed record CredentialPayload(string ApiKey, string ApiSecret)
    {
        public string? IntegratorName { get; init; }
    }
}

public sealed record HepsiburadaRequestContext(PlatformConnection Connection, Uri OmsBaseAddress, Uri ListingBaseAddress, string Username, string Password)
{
    public string? IntegratorName { get; init; }
    public Uri? CatalogBaseAddress { get; init; }
    public Uri? StageTestOrderBaseAddress { get; init; }
}
