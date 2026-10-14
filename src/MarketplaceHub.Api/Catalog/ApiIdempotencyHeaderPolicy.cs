using Microsoft.AspNetCore.Http;

namespace MarketplaceHub.Api.Catalog;

public static class ApiIdempotencyHeaderPolicy
{
    public static bool TryGetKey(IHeaderDictionary headers, out string key, out bool conflictingKeys)
    {
        var standard = headers["Idempotency-Key"].ToString().Trim();
        var legacy = headers["X-Idempotency-Key"].ToString().Trim();
        conflictingKeys = standard.Length > 0 && legacy.Length > 0
            && !string.Equals(standard, legacy, StringComparison.Ordinal);
        key = standard.Length > 0 ? standard : legacy;
        return !conflictingKeys && key.Length > 0;
    }
}
