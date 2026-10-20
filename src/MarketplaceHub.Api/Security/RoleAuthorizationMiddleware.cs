using System.Security.Claims;

namespace MarketplaceHub.Api.Security;

public sealed class RoleAuthorizationMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> ElevatedRoles = new(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR" };
    private static readonly HashSet<string> OperationalRoles = new(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR", "OPERATIONS" };
    private static readonly HashSet<string> InvoiceRoles = new(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR", "OPERATIONS", "ACCOUNTING" };
    private static readonly HashSet<string> AccountingRoles = new(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR", "ACCOUNTING" };
    // Every role that works with orders; READONLY is left out.
    private static readonly HashSet<string> WorkingRoles = new(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR", "OPERATIONS", "ACCOUNTING" };

    public async Task InvokeAsync(HttpContext context)
    {
        // Rules compare whole segments of the path with empty segments dropped, so a trailing or doubled
        // slash ("/credential/", "//deep-delete") meets the same rule as the route that serves it.
        var segments = (context.Request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2
            || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            || segments.Length > 2 && (segments[2].Equals("auth", StringComparison.OrdinalIgnoreCase) || segments[2].Equals("hooks", StringComparison.OrdinalIgnoreCase) || segments[2].Equals("public", StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        // Reads are open to every signed-in role, except documents (invoice PDFs, labels, error files) and
        // reads that call a marketplace live and spend its quota.
        var reading = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var required = reading ? ReadRoles(segments) : RequiredRoles(segments);
        if (required is null)
        {
            await next(context);
            return;
        }

        var role = Normalize(context.User.FindFirstValue(ClaimTypes.Role));
        var allowed = !string.IsNullOrEmpty(role) && required.Contains(role);
        if (allowed)
        {
            await next(context);
            return;
        }

        await ApiProblems.WriteAsync(context, "PERMISSION_DENIED", "Bu işlem için yeterli yetkiniz yok.", StatusCodes.Status403Forbidden);
    }

    // Null: any signed-in role may read it.
    private static HashSet<string>? ReadRoles(string[] segments)
    {
        var area = segments.Length > 2 ? segments[2].ToLowerInvariant() : string.Empty;
        var last = segments.Length > 3 ? segments[^1].ToLowerInvariant() : string.Empty;
        return area switch
        {
            // Invoice documents (and the manual ones in the workspace) are for those who work with invoices.
            "invoices" or "invoice-workspace" when last == "content" => InvoiceRoles,
            // Shipping labels and documents, import error files.
            "shipments" when last == "content" => OperationalRoles,
            "imports" when last == "errors.csv" => OperationalRoles,
            // Reads answered by a live marketplace call.
            "connections" when last is "n11-shipment-templates" or "instant-notifications" or "product-matches" => WorkingRoles,
            "orders" when last is "finance" or "product-image" => WorkingRoles,
            "shipments" when last == "changeable-cargo-companies" => WorkingRoles,
            "returns" when last == "rejection-reasons" => WorkingRoles,
            _ => null
        };
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
            case "invoices" or "invoice-workspace":
                return InvoiceRoles;
            // Order work (picking, packages, Shopify status, CSV import) is daily operational work.
            case "orders":
                return OperationalRoles;
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
