using System.Net;
using System.Text.RegularExpressions;
using MarketplaceHub.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The authorization matrix of the API: every mapped route with the role gate it meets (RoleAuthorizationMiddleware).
// A new route fails EveryMappedRouteIsDeclaredWithItsGate until it is declared here, so its roles are a decision,
// not whatever its path happens to match.
//   Open        no session needed: sign-in, marketplace webhooks (route token), public document links, health, hub
//               (the hub refuses a connection without a session itself)
//   SignedIn    any signed-in role, READONLY included; the endpoint refuses a caller without a tenant (401)
//   Working     OWNER, ADMINISTRATOR, OPERATIONS, ACCOUNTING
//   Operations  OWNER, ADMINISTRATOR, OPERATIONS
//   Accounting  OWNER, ADMINISTRATOR, ACCOUNTING
//   Elevated    OWNER, ADMINISTRATOR
public sealed class ApiRouteAuthorizationMatrixTests(ApiPipelineHost fixture) : IClassFixture<ApiPipelineHost>
{
    private static readonly string[] Matrix =
    [
        "POST   /api/v1/auth/change-password Open",
        "GET    /api/v1/auth/csrf Open",
        "POST   /api/v1/auth/login Open",
        "POST   /api/v1/auth/logout Open",
        "GET    /api/v1/auth/me Open",
        "POST   /api/v1/auth/mfa/challenge Open",
        "POST   /api/v1/auth/mfa/confirm Open",
        "POST   /api/v1/auth/mfa/disable Open",
        "POST   /api/v1/auth/mfa/setup Open",
        "POST   /api/v1/auth/recovery-codes/regenerate Open",
        "GET    /api/v1/auth/security-status Open",
        "GET    /api/v1/auth/sessions Open",
        "DELETE /api/v1/auth/sessions/{id:guid} Open",
        "POST   /api/v1/auth/sessions/{id:guid}/revoke Open",
        "DELETE /api/v1/auth/sessions/closed Open",
        "POST   /api/v1/auth/sessions/revoke-others Open",
        "GET    /api/v1/catalog-structure-runs/{id:guid} SignedIn",
        "GET    /api/v1/catalog/attributes SignedIn",
        "POST   /api/v1/catalog/attributes Operations",
        "PATCH  /api/v1/catalog/attributes/{id:guid} Operations",
        "DELETE /api/v1/catalog/attributes/{id:guid} Operations",
        "GET    /api/v1/catalog/attributes/{id:guid}/used-values SignedIn",
        "POST   /api/v1/catalog/attributes/{id:guid}/values Operations",
        "PUT    /api/v1/catalog/attributes/{id:guid}/values/{valueId:guid} Operations",
        "DELETE /api/v1/catalog/attributes/{id:guid}/values/{valueId:guid} Operations",
        "PUT    /api/v1/catalog/attributes/{id:guid}/values/order Operations",
        "GET    /api/v1/catalog/brands SignedIn",
        "POST   /api/v1/catalog/brands Operations",
        "GET    /api/v1/catalog/brands/{id:guid} SignedIn",
        "PATCH  /api/v1/catalog/brands/{id:guid} Operations",
        "GET    /api/v1/catalog/categories SignedIn",
        "POST   /api/v1/catalog/categories Operations",
        "GET    /api/v1/catalog/categories/{id:guid} SignedIn",
        "PATCH  /api/v1/catalog/categories/{id:guid} Operations",
        "GET    /api/v1/catalog/categories/{id:guid}/attribute-requirements SignedIn",
        "PUT    /api/v1/catalog/categories/{id:guid}/attribute-requirements Operations",
        "GET    /api/v1/catalog/model-codes/holder SignedIn",
        "POST   /api/v1/catalog/variant-codes/taken Operations",
        "POST   /api/v1/channel-offers Operations",
        "GET    /api/v1/channel-offers/{id:guid} SignedIn",
        "PATCH  /api/v1/channel-offers/{id:guid} Operations",
        "PUT    /api/v1/channel-offers/panel-price-follow Operations",
        "GET    /api/v1/connections SignedIn",
        "POST   /api/v1/connections Elevated",
        "GET    /api/v1/connections/{connectionId:guid}/attribute-defaults SignedIn",
        "PUT    /api/v1/connections/{connectionId:guid}/attribute-defaults/{externalAttributeId} Operations",
        "DELETE /api/v1/connections/{connectionId:guid}/attribute-defaults/{externalAttributeId} Operations",
        "POST   /api/v1/connections/{connectionId:guid}/catalog-structure-runs Operations",
        "GET    /api/v1/connections/{connectionId:guid}/catalog-structure-runs SignedIn",
        "GET    /api/v1/connections/{connectionId:guid}/commission-tariffs SignedIn",
        "POST   /api/v1/connections/{connectionId:guid}/commission-tariffs Operations",
        "DELETE /api/v1/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid} Operations",
        "POST   /api/v1/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/export Operations",
        "GET    /api/v1/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/facets SignedIn",
        "PUT    /api/v1/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/period Operations",
        "GET    /api/v1/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/rows SignedIn",
        "GET    /api/v1/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/selection SignedIn",
        "PUT    /api/v1/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/selection Operations",
        "POST   /api/v1/connections/{connectionId:guid}/order-sync-jobs Operations",
        "POST   /api/v1/connections/{connectionId:guid}/price-inventory-sync-jobs Operations",
        "POST   /api/v1/connections/{connectionId:guid}/product-sync-jobs Operations",
        "GET    /api/v1/connections/{connectionId:guid}/promotion-offers SignedIn",
        "POST   /api/v1/connections/{connectionId:guid}/promotion-offers Operations",
        "GET    /api/v1/connections/{connectionId:guid}/promotion-offers/{kind} SignedIn",
        "DELETE /api/v1/connections/{connectionId:guid}/promotion-offers/{kind} Operations",
        "POST   /api/v1/connections/{connectionId:guid}/reference-sync-jobs Operations",
        "POST   /api/v1/connections/{connectionId:guid}/return-sync-jobs Operations",
        "POST   /api/v1/connections/{connectionId:guid}/sale-status-jobs Operations",
        "POST   /api/v1/connections/{connectionId:guid}/stage-test-order-jobs Elevated",
        "GET    /api/v1/connections/{id:guid} SignedIn",
        "PATCH  /api/v1/connections/{id:guid} Operations",
        "PUT    /api/v1/connections/{id:guid}/active Operations",
        "GET    /api/v1/connections/{id:guid}/capabilities SignedIn",
        "PUT    /api/v1/connections/{id:guid}/credential Elevated",
        "GET    /api/v1/connections/{id:guid}/data-reset SignedIn",
        "POST   /api/v1/connections/{id:guid}/data-reset Elevated",
        "POST   /api/v1/connections/{id:guid}/deep-delete Elevated",
        "GET    /api/v1/connections/{id:guid}/full-reset SignedIn",
        "POST   /api/v1/connections/{id:guid}/full-reset Elevated",
        "POST   /api/v1/connections/{id:guid}/initial-data-sync-jobs Operations",
        "GET    /api/v1/connections/{id:guid}/instant-notifications Working",
        "POST   /api/v1/connections/{id:guid}/instant-notifications Elevated",
        "DELETE /api/v1/connections/{id:guid}/instant-notifications Elevated",
        "GET    /api/v1/connections/{id:guid}/n11-shipment-templates Working",
        "GET    /api/v1/connections/{id:guid}/sync-policies SignedIn",
        "PUT    /api/v1/connections/{id:guid}/sync-policies/{resourceType} Operations",
        "POST   /api/v1/connections/{id:guid}/sync-policies/{resourceType}/runs Operations",
        "POST   /api/v1/connections/{id:guid}/test-jobs Operations",
        "GET    /api/v1/dashboard/bootstrap SignedIn",
        "GET    /api/v1/dashboard/cancellations SignedIn",
        "GET    /api/v1/dashboard/integration-matrix SignedIn",
        "GET    /api/v1/dashboard/performance SignedIn",
        "POST   /api/v1/files/product-media Operations",
        "DELETE /api/v1/files/product-media Operations",
        "PUT    /api/v1/files/product-media-family-reorder Operations",
        "DELETE /api/v1/files/product-media-item Operations",
        "DELETE /api/v1/files/product-media-items Operations",
        "PUT    /api/v1/files/product-media-reconcile Operations",
        "POST   /api/v1/files/product-media-url Operations",
        "GET    /api/v1/files/product-media/{assetId:guid}/content SignedIn",
        "POST   /api/v1/files/return-evidence Operations",
        "POST   /api/v1/hooks/{connectionPublicId:guid}/{routeToken} Open",
        "PUT    /api/v1/hooks/{connectionPublicId:guid}/{routeToken} Open",
        "POST   /api/v1/hooks/{connectionPublicId:guid}/{routeToken}/{**eventPath} Open",
        "PUT    /api/v1/hooks/{connectionPublicId:guid}/{routeToken}/{**eventPath} Open",
        "GET    /api/v1/imports SignedIn",
        "POST   /api/v1/imports Operations",
        "GET    /api/v1/imports/{id:guid} SignedIn",
        "POST   /api/v1/imports/{id:guid}/apply-jobs Operations",
        "GET    /api/v1/imports/{id:guid}/candidates SignedIn",
        "PUT    /api/v1/imports/{id:guid}/column-mapping Operations",
        "PUT    /api/v1/imports/{id:guid}/decisions/{candidateId:guid} Operations",
        "GET    /api/v1/imports/{id:guid}/errors.csv Operations",
        "POST   /api/v1/imports/{id:guid}/preview-jobs Operations",
        "POST   /api/v1/imports/{id:guid}/source-file Operations",
        "GET    /api/v1/inventory SignedIn",
        "PUT    /api/v1/inventory/{variantId:guid}/on-hand Operations",
        "POST   /api/v1/invoice-workspace/confirm Working",
        "PUT    /api/v1/invoice-workspace/hidden Working",
        "POST   /api/v1/invoice-workspace/manual-documents Working",
        "GET    /api/v1/invoice-workspace/manual-documents/{id:guid}/content Working",
        "POST   /api/v1/invoice-workspace/manual-documents/{id:guid}/delivery-jobs Working",
        "PUT    /api/v1/invoice-workspace/manual-status Working",
        "POST   /api/v1/invoice-workspace/orders/{orderId:guid}/confirm-marketplace-delivered Elevated",
        "POST   /api/v1/invoice-workspace/orders/{orderId:guid}/confirm-no-remote-invoice Elevated",
        "POST   /api/v1/invoice-workspace/orders/{orderId:guid}/reconcile-jobs Working",
        "POST   /api/v1/invoice-workspace/orders/{orderId:guid}/replace-cancelled-invoice Elevated",
        "GET    /api/v1/invoice-workspace/page SignedIn",
        "POST   /api/v1/invoice-workspace/preview Working",
        "GET    /api/v1/invoice-workspace/summary SignedIn",
        "GET    /api/v1/invoices SignedIn",
        "POST   /api/v1/invoices Working",
        "GET    /api/v1/invoices/{id:guid} SignedIn",
        "POST   /api/v1/invoices/{id:guid}/cancellation-jobs Working",
        "POST   /api/v1/invoices/{id:guid}/delivery-retry-jobs Working",
        "POST   /api/v1/invoices/{id:guid}/documents/manual Working",
        "POST   /api/v1/invoices/{id:guid}/marketplace-delivery-jobs Working",
        "POST   /api/v1/invoices/{id:guid}/marketplace-link-revocations/{stateId:guid} Working",
        "POST   /api/v1/invoices/{id:guid}/reconcile-jobs Working",
        "PUT    /api/v1/invoices/{id:guid}/shopify-status Working",
        "POST   /api/v1/invoices/{id:guid}/stage-capability-probe-jobs Working",
        "POST   /api/v1/invoices/{id:guid}/submit-jobs Working",
        "POST   /api/v1/invoices/{id:guid}/validate Working",
        "GET    /api/v1/invoices/{invoiceId:guid}/documents/{documentId:guid}/content Working",
        "GET    /api/v1/invoices/{invoiceId:guid}/documents/latest/content Working",
        "GET    /api/v1/jobs/ SignedIn",
        "GET    /api/v1/jobs/{id:guid} SignedIn",
        "POST   /api/v1/jobs/{id:guid}/cancel Elevated",
        "POST   /api/v1/jobs/{id:guid}/invoice-delivery-once Elevated",
        "POST   /api/v1/jobs/{id:guid}/retry Elevated",
        "GET    /api/v1/jobs/counts SignedIn",
        "GET    /api/v1/jobs/metrics SignedIn",
        "GET    /api/v1/jobs/metrics/lanes SignedIn",
        "GET    /api/v1/jobs/metrics/quota SignedIn",
        "GET    /api/v1/jobs/page SignedIn",
        "GET    /api/v1/mappings/attribute-name-hints SignedIn",
        "GET    /api/v1/mappings/attribute-values SignedIn",
        "GET    /api/v1/mappings/attribute-values/{localId:guid} SignedIn",
        "PUT    /api/v1/mappings/attribute-values/{localId:guid} Operations",
        "DELETE /api/v1/mappings/attribute-values/{localId:guid} Operations",
        "GET    /api/v1/mappings/attributes SignedIn",
        "GET    /api/v1/mappings/attributes/{localId:guid} SignedIn",
        "PUT    /api/v1/mappings/attributes/{localId:guid} Operations",
        "DELETE /api/v1/mappings/attributes/{localId:guid} Operations",
        "GET    /api/v1/mappings/brands SignedIn",
        "GET    /api/v1/mappings/brands/{localId:guid} SignedIn",
        "PUT    /api/v1/mappings/brands/{localId:guid} Operations",
        "DELETE /api/v1/mappings/brands/{localId:guid} Operations",
        "GET    /api/v1/mappings/categories SignedIn",
        "GET    /api/v1/mappings/categories/{localId:guid} SignedIn",
        "PUT    /api/v1/mappings/categories/{localId:guid} Operations",
        "DELETE /api/v1/mappings/categories/{localId:guid} Operations",
        "GET    /api/v1/mappings/conflicts SignedIn",
        "GET    /api/v1/mappings/name-hints SignedIn",
        "GET    /api/v1/orders SignedIn",
        "GET    /api/v1/orders/{id:guid} SignedIn",
        "GET    /api/v1/orders/{id:guid}/finance Working",
        "POST   /api/v1/orders/{id:guid}/instant-process Operations",
        "POST   /api/v1/orders/{id:guid}/label-printed Working",
        "POST   /api/v1/orders/{id:guid}/packages Operations",
        "PUT    /api/v1/orders/{id:guid}/shopify-status Operations",
        "GET    /api/v1/orders/facets SignedIn",
        "GET    /api/v1/orders/product-image Working",
        "POST   /api/v1/orders/shopify-csv-import Operations",
        "GET    /api/v1/orders/summary SignedIn",
        "GET    /api/v1/price-stock-monitor SignedIn",
        "POST   /api/v1/price-stock-monitor/emergency-stop Elevated",
        "GET    /api/v1/price-stock-send-log SignedIn",
        "GET    /api/v1/price-stock-send-log/counts SignedIn",
        "GET    /api/v1/product-activity SignedIn",
        "GET    /api/v1/product-activity/counts SignedIn",
        "GET    /api/v1/products SignedIn",
        "POST   /api/v1/products Operations",
        "GET    /api/v1/products/{id:guid} SignedIn",
        "PATCH  /api/v1/products/{id:guid} Operations",
        "DELETE /api/v1/products/{id:guid} Operations",
        "GET    /api/v1/products/{id:guid}/listing-profiles/{connectionId:guid} SignedIn",
        "PUT    /api/v1/products/{id:guid}/listing-profiles/{connectionId:guid} Operations",
        "GET    /api/v1/products/{id:guid}/marketplace-codes SignedIn",
        "PUT    /api/v1/products/{id:guid}/marketplace-codes Operations",
        "POST   /api/v1/products/{id:guid}/publication-jobs Operations",
        "GET    /api/v1/products/{id:guid}/publication-status/{connectionId:guid} SignedIn",
        "POST   /api/v1/products/{id:guid}/publication-status/{connectionId:guid}/refresh Operations",
        "POST   /api/v1/products/{id:guid}/update-jobs Operations",
        "POST   /api/v1/products/{productId:guid}/connections/{connectionId:guid}/price-inventory-sync-jobs Operations",
        "POST   /api/v1/products/bulk-delete Operations",
        "POST   /api/v1/products/bulk-publication-jobs Operations",
        "POST   /api/v1/products/bulk-status Operations",
        "POST   /api/v1/products/bulk-vat Operations",
        "POST   /api/v1/products/delete-preview Operations",
        "GET    /api/v1/products/summary SignedIn",
        "POST   /api/v1/products/transfer/apply Operations",
        "GET    /api/v1/products/transfer/export.xlsx SignedIn",
        "POST   /api/v1/products/transfer/import Operations",
        "GET    /api/v1/products/transfer/summary SignedIn",
        "GET    /api/v1/public/invoice-documents/{token}/content Open",
        "GET    /api/v1/public/invoice-documents/{token}/invoice.pdf Open",
        "GET    /api/v1/public/manual-invoice-documents/{token}/content Open",
        "GET    /api/v1/public/manual-invoice-documents/{token}/invoice.pdf Open",
        "GET    /api/v1/public/product-media/{assetId:guid}/content Open",
        "GET    /api/v1/question-templates SignedIn",
        "POST   /api/v1/question-templates Operations",
        "PUT    /api/v1/question-templates/{id:guid} Operations",
        "DELETE /api/v1/question-templates/{id:guid} Operations",
        "GET    /api/v1/questions SignedIn",
        "GET    /api/v1/questions/{id:guid} SignedIn",
        "POST   /api/v1/questions/{id:guid}/answer Operations",
        "GET    /api/v1/questions/{id:guid}/customer-history SignedIn",
        "POST   /api/v1/questions/{id:guid}/report Operations",
        "GET    /api/v1/questions/report-reasons SignedIn",
        "POST   /api/v1/questions/sync Operations",
        "GET    /api/v1/questions/sync-state SignedIn",
        "GET    /api/v1/reference-data/brands SignedIn",
        "GET    /api/v1/reference-data/brands/lookup SignedIn",
        "GET    /api/v1/reference-data/brands/search SignedIn",
        "GET    /api/v1/reference-data/brands/summary SignedIn",
        "GET    /api/v1/reference-data/brands/unmapped SignedIn",
        "GET    /api/v1/reference-data/categories SignedIn",
        "GET    /api/v1/reference-data/categories/{categoryId}/attributes/{attributeId}/values SignedIn",
        "GET    /api/v1/reference-data/categories/{externalId}/attributes SignedIn",
        "GET    /api/v1/reports/order-profitability SignedIn",
        "GET    /api/v1/reports/payouts SignedIn",
        "GET    /api/v1/reports/payouts/lines SignedIn",
        "GET    /api/v1/reports/sales SignedIn",
        "GET    /api/v1/reports/stock SignedIn",
        "GET    /api/v1/returns SignedIn",
        "GET    /api/v1/returns/{id:guid} SignedIn",
        "POST   /api/v1/returns/{id:guid}/actions Operations",
        "GET    /api/v1/returns/{id:guid}/audits Working",
        "POST   /api/v1/returns/{id:guid}/received Operations",
        "GET    /api/v1/returns/{id:guid}/rejection-reasons Working",
        "GET    /api/v1/returns/{id:guid}/return-shipment-companies Working",
        "POST   /api/v1/returns/{id:guid}/stock-dispositions Operations",
        "GET    /api/v1/returns/summary SignedIn",
        "GET    /api/v1/settings/alert-email/ Elevated",
        "PUT    /api/v1/settings/alert-email/ Elevated",
        "POST   /api/v1/settings/alert-email/test Elevated",
        "GET    /api/v1/settings/appearance SignedIn",
        "PUT    /api/v1/settings/appearance Elevated",
        "GET    /api/v1/settings/data-reset SignedIn",
        "POST   /api/v1/settings/data-reset Elevated",
        "GET    /api/v1/settings/shipping-label SignedIn",
        "PUT    /api/v1/settings/shipping-label Elevated",
        "GET    /api/v1/shipments SignedIn",
        "GET    /api/v1/shipments/{id:guid} SignedIn",
        "POST   /api/v1/shipments/{id:guid}/actions Operations",
        "GET    /api/v1/shipments/{id:guid}/changeable-cargo-companies Working",
        "POST   /api/v1/shipments/{id:guid}/common-label-jobs Operations",
        "GET    /api/v1/shipments/{id:guid}/documents/{documentId:guid}/content Operations",
        "POST   /api/v1/shipments/{id:guid}/instant-cargo-provider Operations",
        "POST   /api/v1/shipments/{id:guid}/instant-process Operations",
        "POST   /api/v1/shipments/{id:guid}/label-capability-probes Elevated",
        "PATCH  /api/v1/variants/{id:guid}/default-prices Operations",
        "*      /health/live Open",
        "*      /health/queues Open",
        "*      /health/ready Open",
        "*      /hubs/operations Open",
        "*      /hubs/operations/negotiate Open",
        "GET    /internal/alert-settings Open",
        "POST   /internal/alert-settings/import Open",
    ];

    private static readonly Dictionary<string, string[]?> Gates = new()
    {
        ["Open"] = null,
        ["SignedIn"] = null,
        ["Working"] = ["ACCOUNTING", "ADMINISTRATOR", "OPERATIONS", "OWNER"],
        ["Operations"] = ["ADMINISTRATOR", "OPERATIONS", "OWNER"],
        ["Accounting"] = ["ACCOUNTING", "ADMINISTRATOR", "OWNER"],
        ["Elevated"] = ["ADMINISTRATOR", "OWNER"]
    };

    private sealed record Route(string Method, string Pattern, string Gate);

    private static IEnumerable<Route> Declared() => Matrix.Select(line =>
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new Route(parts[0], parts[1], parts[2]);
    });

    private List<(string Method, string Pattern)> Mapped()
    {
        _ = fixture.Host.CreateClient();
        return fixture.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (method, "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))))
            .ToList();
    }

    // A request path that matches the pattern: route values become a fixed id or a plain segment.
    private static string Sample(string pattern) =>
        Regex.Replace(Regex.Replace(pattern, @"\{\*\*[^}]+\}", "event/path"), @"\{[^}]+\}",
            match => match.Value.Contains(":guid", StringComparison.Ordinal) ? "6f1c2a3e-0000-4000-8000-000000000001" : "x");

    private static bool IsOpenPath(string pattern) =>
        !pattern.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(pattern, "^/api/v1/(auth|hooks|public)/", RegexOptions.IgnoreCase);

    [Fact]
    public void EveryMappedRouteIsDeclaredWithItsGate()
    {
        var declared = Declared().ToList();
        var mapped = Mapped();

        var undeclared = mapped.Where(route => !declared.Any(d => d.Method == route.Method && d.Pattern == route.Pattern))
            .Select(route => $"{route.Method} {route.Pattern} (gate: {Describe(RoleAuthorizationMiddleware.RolesFor(route.Method, Sample(route.Pattern)))})").ToList();
        var gone = declared.Where(d => !mapped.Contains((d.Method, d.Pattern))).Select(d => $"{d.Method} {d.Pattern}").ToList();
        Assert.True(undeclared.Count == 0, "Declare these routes in the matrix with their gate:\n" + string.Join("\n", undeclared));
        Assert.True(gone.Count == 0, "These declared routes are no longer mapped:\n" + string.Join("\n", gone));
        Assert.Equal(declared.Count, declared.Select(d => (d.Method, d.Pattern)).Distinct().Count());

        var wrong = new List<string>();
        foreach (var route in declared)
        {
            Assert.True(Gates.ContainsKey(route.Gate), $"Unknown gate {route.Gate} for {route.Method} {route.Pattern}");
            var actual = RoleAuthorizationMiddleware.RolesFor(route.Method, Sample(route.Pattern));
            var expected = Gates[route.Gate];
            var matches = expected is null
                ? actual is null && (route.Gate == "Open") == IsOpenPath(route.Pattern)
                : actual is not null && actual.OrderBy(role => role, StringComparer.Ordinal).SequenceEqual(expected);
            if (!matches) wrong.Add($"{route.Method} {route.Pattern}: declared {route.Gate}, gate is {Describe(actual)}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // READONLY only reads; outside sign-in (its own session, password and second factor) and the webhooks,
    // no write is open to it.
    [Fact]
    public void NoWriteIsOpenToReadOnly()
    {
        var open = Declared()
            .Where(route => route.Method is not ("GET" or "HEAD") && route.Pattern.StartsWith("/api/v1/", StringComparison.Ordinal)
                && !Regex.IsMatch(route.Pattern, "^/api/v1/(auth|hooks)/"))
            .Where(route => RoleAuthorizationMiddleware.RolesFor(route.Method, Sample(route.Pattern)) is not { } roles || roles.Contains("READONLY"))
            .Select(route => $"{route.Method} {route.Pattern}").ToList();
        Assert.True(open.Count == 0, "Writes open to READONLY:\n" + string.Join("\n", open));
    }

    // The gate lets every GET that is open to all roles through without a session; each such endpoint must
    // then refuse a caller without a tenant before it reads anything (the test host has no database).
    [Fact]
    public async Task ReadsOpenToEveryRoleRefuseACallerWithoutASession()
    {
        using var client = fixture.Host.CreateClient();
        var answered = new List<string>();
        foreach (var route in Declared().Where(route => route.Method == "GET" && route.Gate == "SignedIn"))
        {
            // The ids some reads require in the query string, so binding reaches the handler.
            using var response = await client.GetAsync(Sample(route.Pattern) + "?connectionId=6f1c2a3e-0000-4000-8000-000000000002&attributeId=6f1c2a3e-0000-4000-8000-000000000003");
            if (response.StatusCode != HttpStatusCode.Unauthorized) answered.Add($"{route.Pattern}: {(int)response.StatusCode}");
        }
        Assert.True(answered.Count == 0, "Answered without a session:\n" + string.Join("\n", answered));
    }

    private static string Describe(IReadOnlySet<string>? roles) => roles is null ? "none" : string.Join(",", roles.OrderBy(role => role, StringComparer.Ordinal));
}
