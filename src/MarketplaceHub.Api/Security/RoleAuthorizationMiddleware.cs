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
        // Rules compare whole segments of the path with empty segments dropped, so a trailing or doubled
        // slash ("/credential/", "//deep-delete") meets the same rule as the route that serves it.
        var segments = (context.Request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2
            || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            || segments.Length > 2 && (segments[2].Equals("auth", StringComparison.OrdinalIgnoreCase) || segments[2].Equals("hooks", StringComparison.OrdinalIgnoreCase))
            || HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        var role = Normalize(context.User.FindFirstValue(ClaimTypes.Role));
        var allowed = !string.IsNullOrEmpty(role) && RequiredRoles(segments).Contains(role);
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

    // segments[0..1] are "api" and "v1".
    private static HashSet<string> RequiredRoles(string[] segments)
    {
        var area = segments.Length > 2 ? segments[2].ToLowerInvariant() : string.Empty;
        var last = segments.Length > 3 ? segments[^1].ToLowerInvariant() : string.Empty;
        switch (area)
        {
            case "billing":
                return AccountingRoles;
            case "invoices":
                return InvoiceRoles;
            case "questions" or "question-templates":
                return OperationalRoles;
            case "connections":
                // Creating a connection stores the store's credentials, as rotating them does.
                if (segments.Length == 3) return ElevatedRoles;
                if (last == "evidence" && segments.Any(segment => segment.Equals("capabilities", StringComparison.OrdinalIgnoreCase))) return ElevatedRoles;
                // Credentials, stage test orders, deleting, emptying or hiding a connection's data, and registering
                // the marketplace's notifications (a setting on the store's side) are for administrators.
                return last is "credential" or "stage-test-order-jobs" or "deep-delete" or "data-reset" or "data-visibility" or "instant-notifications"
                    ? ElevatedRoles
                    : OperationalRoles;
            case "shipments":
                return last == "label-capability-probes" ? ElevatedRoles : OperationalRoles;
            case "catalog" or "products" or "files" or "imports" or "inventory" or "channel-offers" or "mappings" or "returns":
                return OperationalRoles;
            default:
                return ElevatedRoles;
        }
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
