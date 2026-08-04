using System.Security.Claims;

namespace MarketplaceHub.Api.Security;

public sealed class RoleAuthorizationMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> ElevatedRoles = new(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR" };
    private static readonly HashSet<string> OperationalRoles = new(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR", "OPERATIONS" };
    private static readonly HashSet<string> InvoiceRoles = new(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR", "OPERATIONS", "ACCOUNTING" };
    private static readonly HashSet<string> AccountingRoles = new(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR", "ACCOUNTING" };

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api/v1") || path.StartsWithSegments("/api/v1/auth") || HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        var role = Normalize(context.User.FindFirstValue(ClaimTypes.Role));
        var allowed = !string.IsNullOrEmpty(role) && RequiredRoles(path).Contains(role);
        if (allowed)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://marketplacehub.invalid/problems/permission-denied",
            title = "Bu işlem için yeterli yetkiniz yok.",
            status = 403,
            code = "PERMISSION_DENIED",
            correlationId = context.TraceIdentifier,
            retryable = false
        }, context.RequestAborted);
    }

    private static HashSet<string> RequiredRoles(PathString path)
    {
        if (path.StartsWithSegments("/api/v1/billing")) return AccountingRoles;
        if (path.StartsWithSegments("/api/v1/invoices")) return InvoiceRoles;
        if (path.StartsWithSegments("/api/v1/connections") && path.Value?.EndsWith("/credential", StringComparison.OrdinalIgnoreCase) == true) return ElevatedRoles;
        if (path.StartsWithSegments("/api/v1/connections")) return OperationalRoles;
        if (path.StartsWithSegments("/api/v1/jobs")) return ElevatedRoles;
        if (path.StartsWithSegments("/api/v1/catalog") || path.StartsWithSegments("/api/v1/products") || path.StartsWithSegments("/api/v1/files") ||
            path.StartsWithSegments("/api/v1/imports") || path.StartsWithSegments("/api/v1/inventory") || path.StartsWithSegments("/api/v1/channel-offers") ||
            path.StartsWithSegments("/api/v1/mappings") || path.StartsWithSegments("/api/v1/shipments") || path.StartsWithSegments("/api/v1/returns"))
            return OperationalRoles;
        return ElevatedRoles;
    }

    private static string Normalize(string? role) => role?.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
    {
        "OWNER" => "OWNER",
        "ADMINISTRATOR" => "ADMINISTRATOR",
        "OPERATIONS" => "OPERATIONS",
        "ACCOUNTING" => "ACCOUNTING",
        "READONLY" => "READONLY",
        _ => string.Empty
    };
}
