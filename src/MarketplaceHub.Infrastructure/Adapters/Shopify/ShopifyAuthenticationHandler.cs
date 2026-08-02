using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed class ShopifyAuthenticationHandler(AppDbContext db, IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");

    public async Task<ShopifyRequestContext?> LoadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == ShopifyContract.PlatformCode, cancellationToken);
        if (connection is null || connection.ApiVersion != ShopifyContract.ApiVersion || !ShopifyContract.TryNormalizeShopDomain(connection.ExternalStoreId, out var domain)) return null;
        var credential = await db.PlatformCredentials.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (credential is null) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<CredentialPayload>(_protector.Unprotect(credential.ProtectedPayload));
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken)) return null;
            return new(connection.Environment, domain, payload.AccessToken, payload.ClientSecret, new Uri($"https://{domain}/admin/api/{ShopifyContract.ApiVersion}/graphql.json"));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException) { return null; }
    }

    private sealed record CredentialPayload(string AccessToken, string ClientSecret);
}

public sealed record ShopifyRequestContext(string Environment, string ShopDomain, string AccessToken, string ClientSecret, Uri GraphQlEndpoint);
