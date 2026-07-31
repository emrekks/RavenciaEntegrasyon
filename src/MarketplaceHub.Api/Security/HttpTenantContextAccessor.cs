using System.Security.Claims;
using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Security;

public sealed class HttpTenantContextAccessor(IHttpContextAccessor httpContextAccessor) : ITenantContextAccessor
{
    public TenantContext? Current
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            return Guid.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                && Guid.TryParse(user.FindFirstValue("tenant_id"), out var tenantId)
                && user.FindFirstValue(ClaimTypes.Role) is { Length: > 0 } role
                ? new TenantContext(userId, tenantId, role) : null;
        }
    }
}
