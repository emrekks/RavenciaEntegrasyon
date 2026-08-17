namespace MarketplaceHub.Api.IntegrationTests;

public sealed class ApiSurfaceTests
{
    [Fact]
    public void Api_source_keeps_F2_and_adds_only_approved_F3_route_families()
    {
        var root = FindRoot(); var f2 = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Api", "F2", "F2Endpoints.cs"));
        foreach (var required in new[] { "/catalog/categories", "/catalog/brands", "/catalog/attributes", "/attribute-requirements", "/values", "/products", "/publication-jobs", "/publication-status/", "/files/product-media-url", "/imports", "/inventory", "/channel-offers", "/reference-data", "/mappings" }) Assert.Contains(required, f2, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "/orders", "/shipments", "/returns", "/invoices", "/integrations", "/webhooks", "/tenants", "/users" }) Assert.DoesNotContain(forbidden, f2, StringComparison.Ordinal);
        Assert.Contains("connectionId, \"CATEGORIES\", null", f2, StringComparison.Ordinal);
        Assert.Contains("connectionId, \"CATEGORY_ATTRIBUTES\", externalId", f2, StringComparison.Ordinal);
        Assert.Contains("connectionId, \"ATTRIBUTE_VALUES\", $\"{categoryId}/{attributeId}\"", f2, StringComparison.Ordinal);
        Assert.DoesNotContain("connectionId, \"CATEGORY\", null", f2, StringComparison.Ordinal);
        var f3 = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Api", "F3", "F3Endpoints.cs"));
        foreach (var required in new[] { "/connections", "/orders", "/shipments", "/returns", "/hooks/{connectionPublicId:guid}/{routeToken}" }) Assert.Contains(required, f3, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "/invoices", "/billing", "/efaturam-settings", "/reports", "/tenants", "/users", "Shopify", "Hepsiburada", "N11", "Pazarama" }) Assert.DoesNotContain(forbidden, f3, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EnqueueTestAsync(tenant.TenantId, id, http.Request.Headers[\"Idempotency-Key\"].ToString()", f3, StringComparison.Ordinal);
    }

    [Fact]
    public void F4_surface_contains_only_invoice_and_billing_families_with_write_guards()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Api", "F4", "F4Endpoints.cs"));
        foreach (var required in new[] { "/billing/invoice-policies/", "/invoices", "/submit-jobs", "/reconcile-jobs", "/marketplace-delivery-jobs", "/cancellation-jobs", "/documents/manual", "/documents/{documentId:guid}/content" }) Assert.Contains(required, source, StringComparison.Ordinal);
        foreach (var guard in new[] { "Idempotency-Key", "If-Match", "REAUTHENTICATION_FAILED", "EXPLICIT_CONFIRMATION_REQUIRED", "no-store" }) Assert.Contains(guard, source, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "/billing/taxpayers", "/billing/legal-entity-profile", "/reports", "/accounting", "/erp", "/tenants", "/users", "Shopify", "Hepsiburada", "N11", "Pazarama" }) Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void F4_protected_write_endpoint_skips_confirmation_only_for_stage_and_keeps_production_guards()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Api", "F4", "F4Endpoints.cs"));
        var stageGate = source.IndexOf("if (!IntegrationRuntimePolicy.RequiresSensitiveConfirmation(connection))", StringComparison.Ordinal);
        var explicitConfirmation = source.IndexOf("if (!command.Confirmed)", StringComparison.Ordinal);
        var passwordCheck = source.IndexOf("users.CheckPasswordAsync", StringComparison.Ordinal);

        Assert.True(stageGate >= 0, "Stage manual short-circuit must remain in the protected invoice endpoint.");
        Assert.True(explicitConfirmation > stageGate, "Explicit confirmation must apply only after the Stage short-circuit.");
        Assert.True(passwordCheck > explicitConfirmation, "Production password re-authentication must remain after explicit confirmation.");
        Assert.Contains("return Accepted(await enqueue(tenant.TenantId, stageVersion", source, StringComparison.Ordinal);
        Assert.Contains("EXPLICIT_CONFIRMATION_REQUIRED", source, StringComparison.Ordinal);
        Assert.Contains("REAUTHENTICATION_FAILED", source, StringComparison.Ordinal);
    }
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
