using System.Security.Claims;

namespace MarketplaceHub.Api.Security;

// With Security:RequireMfaForElevatedRoles on, an owner or administrator without an enabled
// authenticator can still sign in, read and set up MFA (every /api/v1/auth route stays open), but
// cannot change anything else until MFA is enabled. Off by default; the former Security:TotpEnabled
// key was read nowhere and is gone.
public sealed class ElevatedRoleMfaMiddleware(RequestDelegate next)
{
    public const string SettingKey = "Security:RequireMfaForElevatedRoles";

    public async Task InvokeAsync(HttpContext context, IConfiguration configuration)
    {
        if (MustEnrollFirst(context, configuration.GetValue<bool>(SettingKey)))
        {
            await ApiProblems.WriteAsync(context, "MFA_ENROLLMENT_REQUIRED", "Yönetici rolleri için iki adımlı doğrulama zorunludur; değişiklik yapmadan önce Güvenlik ayarlarından kurun.", StatusCodes.Status403Forbidden);
            return;
        }
        await next(context);
    }

    internal static bool MustEnrollFirst(HttpContext context, bool enforced)
    {
        if (!enforced) return false;
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return false;
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api/v1") || path.StartsWithSegments("/api/v1/auth") || path.StartsWithSegments("/api/v1/hooks")) return false;
        var role = context.User.FindFirstValue(ClaimTypes.Role);
        if (role is not ("OWNER" or "ADMINISTRATOR")) return false;
        return context.User.FindFirstValue(SessionAuthMiddleware.MfaClaim) != "enabled";
    }
}
