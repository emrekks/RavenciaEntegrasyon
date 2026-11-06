using System.Security.Claims;
using MarketplaceHub.Application;

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
        var required = RolesFor(context.Request.Method, context.Request.Path.Value);
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

    // The roles a request needs, or null when this gate lets it through: sign-in, webhooks, public document reads,
    // and reads open to every signed-in role (their endpoints refuse a caller without a tenant). The route guard
    // (ApiRouteAuthorizationMatrixTests) checks every mapped route against it.
    internal static IReadOnlySet<string>? RolesFor(string method, string? path)
    {
        // Rules compare whole segments of the path with empty segments dropped, so a trailing or doubled
        // slash ("/credential/", "//deep-delete") meets the same rule as the route that serves it.
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2
            || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            || segments.Length > 2 && (segments[2].Equals("auth", StringComparison.OrdinalIgnoreCase) || segments[2].Equals("hooks", StringComparison.OrdinalIgnoreCase))
            // Public document links are read without a session; anything else sent there meets the rules below.
            || segments.Length > 2 && segments[2].Equals("public", StringComparison.OrdinalIgnoreCase) && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
            return null;

        // Reads are open to every signed-in role, except documents (invoice PDFs, labels, error files) and
        // reads that call a marketplace live and spend its quota.
        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ? ReadRoles(segments) : RequiredRoles(segments);
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
            "connections" when last is "n11-shipment-templates" or "instant-notifications" => WorkingRoles,
            "orders" when last is "finance" or "product-image" => WorkingRoles,
            "shipments" when last == "changeable-cargo-companies" => WorkingRoles,
            "returns" when last is "rejection-reasons" or "audits" or "return-shipment-companies" => WorkingRoles,
            // Who gets the operator alerts, and from which account, is for the owner and administrators.
            "settings" when last == "alert-email" => ElevatedRoles,
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
            // No invoice goes out without the owner's approval (6 Nov): issuing an e-invoice, cancelling it, and sending
            // or removing its link at the marketplace are for the owner and administrators. Drafts, validation,
            // previews, uploads and status reads stay invoice work.
            case "invoices":
                return last is "submit-jobs" or "cancellation-jobs" or "marketplace-delivery-jobs" or "delivery-retry-jobs"
                        || segments.Length == 6 && segments[4].Equals("marketplace-link-revocations", StringComparison.OrdinalIgnoreCase)
                    ? ElevatedRoles
                    : InvoiceRoles;
            // Overriding what the fiscal records say (a cancelled invoice replaced, no remote invoice, delivered by
            // the marketplace) is an administrator decision, as are issuing the previewed invoices (confirm) and sending
            // a hand-uploaded invoice's link to the marketplace (delivery-jobs); the rest of the workspace is invoice work.
            case "invoice-workspace":
                return last is "replace-cancelled-invoice" or "confirm-no-remote-invoice" or "confirm-marketplace-delivered" or "confirm" or "delivery-jobs" ? ElevatedRoles : InvoiceRoles;
            // Order work (picking, packages, Shopify status, CSV import) is daily operational work; recording that a
            // label was printed is open to every role that works with orders (accounting prints labels too).
            case "orders":
                return last == "label-printed" ? WorkingRoles : OperationalRoles;
            case "questions" or "question-templates":
                return OperationalRoles;
            case "connections":
                // Creating a connection stores the store's credentials, as rotating them does.
                if (segments.Length == 3) return ElevatedRoles;
                // PATCH /connections/{id} stays operational; the endpoint refuses its external write and invoice creation
                // changes to other roles (IsElevated), and environment and store changes to everyone.
                // Credentials, stage test orders, deleting or emptying a connection's data, and registering the
                // marketplace's notifications (a setting on the store's side) are for administrators.
                // A write switch (PUT /connections/{id}/sync-policies/PRICE_WRITE, STOCK_WRITE, ...) is a permission like
                // the master external-write switch, and as elevated (YENİ-03).
                return last is "credential" or "stage-test-order-jobs" or "deep-delete" or "data-reset" or "full-reset" or "instant-notifications"
                        || segments.Length == 6 && segments[4].Equals("sync-policies", StringComparison.OrdinalIgnoreCase) && MarketplaceExternalWritePolicies.IsPermissionPolicy(last)
                    ? ElevatedRoles
                    : OperationalRoles;
            case "shipments":
                return last == "label-capability-probes" ? ElevatedRoles : OperationalRoles;
            case "catalog" or "products" or "variants" or "files" or "imports" or "inventory" or "channel-offers" or "mappings" or "returns":
                return OperationalRoles;
            default:
                return ElevatedRoles;
        }
    }

    // Owner or administrator: for the checks an endpoint makes on what a request changes, not only on its path (G5).
    internal static bool IsElevated(string? role) => Normalize(role) is { Length: > 0 } normalized && ElevatedRoles.Contains(normalized);

    // The roles that may open invoice documents (the panel's /content routes). A bearer invoice link opens the same
    // document without a session, so it is shown only to them (InvoiceLinkVisibility, YENİ-02).
    internal static bool CanOpenInvoiceDocuments(string? role) => Normalize(role) is { Length: > 0 } normalized && InvoiceRoles.Contains(normalized);

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
