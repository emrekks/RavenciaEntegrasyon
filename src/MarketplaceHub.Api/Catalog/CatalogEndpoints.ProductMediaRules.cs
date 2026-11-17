using System.Net;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Catalog;

public static partial class CatalogEndpoints
{
    // Oldest active upload with this content. The unique index keeps it to one (K11); FirstOrDefault stays so a
    // database restored from before the index cannot make the lookup throw.
    private static Task<FileAsset?> FindUploadedAssetAsync(AppDbContext db, Guid tenantId, string hash, CancellationToken cancellationToken) =>
        db.FileAssets.Where(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA" && x.Sha256 == hash && x.ArchivedAt == null)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static bool IsPublicAddress(IPAddress address) => PublicNetwork.IsPublic(address);

    private static bool IsPublicHost(string host)
    {
        var normalized = host.TrimEnd('.');
        return !string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetStoredProductMediaAssetId(string? value, out Guid assetId)
    {
        assetId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split('/', StringSplitOptions.None);
        return parts.Length == 7
            && parts[0].Length == 0
            && parts[1] == "api"
            && parts[2] == "v1"
            && parts[3] == "files"
            && parts[4] == "product-media"
            && parts[6] == "content"
            && Guid.TryParseExact(parts[5], "D", out assetId);
    }

    private static bool TryNormalizePublicProductMediaUrl(string? value, out string? normalized, out string? issue)
    {
        normalized = null;
        issue = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsoluteUri.Length > 512)
        {
            issue = "Kalıcı, kullanıcı bilgisi içermeyen ve en fazla 512 karakterlik HTTPS görsel URL'si gereklidir.";
            return false;
        }
        if (uri.IsLoopback || !IsPublicHost(uri.Host) || IPAddress.TryParse(uri.Host, out var address) && !IsPublicAddress(address))
        {
            issue = "Yerel, özel ağ veya loopback görsel adresi kullanılamaz.";
            return false;
        }
        normalized = uri.AbsoluteUri;
        return true;
    }

    internal static string MediaRole(string? value)
    {
        var role = string.IsNullOrWhiteSpace(value) ? "GALLERY" : value.Trim().ToUpperInvariant();
        return role[..Math.Min(role.Length, 32)];
    }

    private static string? SafeAltText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text[..Math.Min(text.Length, 320)];
    }
}
