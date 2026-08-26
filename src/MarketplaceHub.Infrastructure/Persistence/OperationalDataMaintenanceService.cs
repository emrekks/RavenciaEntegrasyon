using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class OperationalDataMaintenanceService(AppDbContext db, TimeProvider timeProvider) : IOperationalDataMaintenanceService
{
    private static readonly HashSet<string> AllowedScopes = new(StringComparer.Ordinal) { "PRODUCTS", "CATEGORIES", "BRANDS", "OPTIONS", "ORDERS", "RETURNS", "INVOICES" };
    private static readonly HashSet<string> ConnectionDeleteScopes = new(StringComparer.Ordinal) { "PRODUCTS", "ORDERS", "RETURNS", "INVOICES" };

    public async Task<ServiceResult<OperationalDataResetView>> ResetAsync(Guid tenantId, Guid actorUserId, ResetOperationalDataCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var scopes = command.Scopes.Select(value => value.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (scopes.Count == 0 || scopes.Any(scope => !AllowedScopes.Contains(scope))) return Invalid("Geçerli en az bir veri alanı seçin.");
        if (!string.Equals(command.Confirmation?.Trim(), "VERİLERİ SİL", StringComparison.Ordinal)) return Invalid("Onay alanına tam olarak VERİLERİ SİL yazın.");

        var counts = await CountsAsync(tenantId, null, scopes, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (scopes.Contains("ORDERS"))
        {
            await DeleteReturnsAsync(tenantId, null, cancellationToken);
            await DeleteInvoicesAsync(tenantId, null, cancellationToken);
            await DeleteOrdersAsync(tenantId, null, cancellationToken);
        }
        else
        {
            if (scopes.Contains("RETURNS")) await DeleteReturnsAsync(tenantId, null, cancellationToken);
            if (scopes.Contains("INVOICES")) await DeleteInvoicesAsync(tenantId, null, cancellationToken);
        }
        if (scopes.Contains("CATEGORIES")) await DeleteCategoriesAsync(tenantId, cancellationToken);
        if (scopes.Contains("BRANDS")) await DeleteBrandsAsync(tenantId, cancellationToken);
        if (scopes.Contains("OPTIONS")) await DeleteOptionsAsync(tenantId, cancellationToken);
        if (scopes.Contains("PRODUCTS")) await DeleteProductsAsync(tenantId, null, cancellationToken);
        db.AuditLogs.Add(Audit(tenantId, actorUserId, "OPERATIONAL_DATA_RESET", "Tenant", tenantId.ToString("D"), string.Join(',', scopes.Order()), correlationId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<OperationalDataResetView>.Ok(counts);
    }

    public async Task<ServiceResult<OperationalDataResetView>> DeleteConnectionAsync(Guid tenantId, Guid actorUserId, Guid connectionId, long expectedVersion, DeleteConnectionCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED", cancellationToken);
        if (connection is null) return ServiceResult<OperationalDataResetView>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        if (connection.Version != expectedVersion) return ServiceResult<OperationalDataResetView>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{connection.Version}.", 412);
        if (!string.Equals(command.Confirmation?.Trim(), connection.DisplayName, StringComparison.Ordinal)) return Invalid($"Onay alanına bağlantı adını tam olarak yazın: {connection.DisplayName}");

        var scopes = new HashSet<string>(ConnectionDeleteScopes, StringComparer.Ordinal);
        var counts = await CountsAsync(tenantId, connectionId, scopes, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DeleteReturnsAsync(tenantId, connectionId, cancellationToken);
        await DeleteInvoicesAsync(tenantId, connectionId, cancellationToken);
        await DeleteOrdersAsync(tenantId, connectionId, cancellationToken);
        await DeleteProductsAsync(tenantId, connectionId, cancellationToken);
        await DeleteConnectionArtifactsAsync(tenantId, connectionId, cancellationToken);
        connection.Status = "DELETED";
        connection.LastErrorCode = null;
        connection.Version++;
        db.AuditLogs.Add(Audit(tenantId, actorUserId, "CONNECTION_DEEP_DELETED", "PlatformConnection", connectionId.ToString("D"), $"{connection.PlatformCode}:{connection.ExternalStoreId}", correlationId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<OperationalDataResetView>.Ok(counts with { ConnectionDeleted = true });
    }

    private async Task<OperationalDataResetView> CountsAsync(Guid tenantId, Guid? connectionId, HashSet<string> scopes, CancellationToken cancellationToken)
    {
        var products = scopes.Contains("PRODUCTS") ? connectionId is null ? await db.Products.CountAsync(x => x.TenantId == tenantId, cancellationToken) : await db.MarketplaceProductLinks.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).Select(x => x.ProductId).Distinct().CountAsync(cancellationToken) : 0;
        var orders = scopes.Contains("ORDERS") ? await db.Orders.CountAsync(x => x.TenantId == tenantId && (connectionId == null || x.ConnectionId == connectionId), cancellationToken) : 0;
        var returns = scopes.Contains("RETURNS") || scopes.Contains("ORDERS") ? await db.ReturnClaims.CountAsync(x => x.TenantId == tenantId && (connectionId == null || x.ConnectionId == connectionId), cancellationToken) : 0;
        var invoices = scopes.Contains("INVOICES") || scopes.Contains("ORDERS") ? await db.Invoices.CountAsync(x => x.TenantId == tenantId && (connectionId == null || x.ProviderConnectionId == connectionId || db.Orders.Any(order => order.TenantId == tenantId && order.Id == x.OrderId && order.ConnectionId == connectionId)), cancellationToken) : 0;
        var categories = scopes.Contains("CATEGORIES") ? await db.Categories.CountAsync(x => x.TenantId == tenantId, cancellationToken) : 0;
        var brands = scopes.Contains("BRANDS") ? await db.Brands.CountAsync(x => x.TenantId == tenantId, cancellationToken) : 0;
        var options = scopes.Contains("OPTIONS") ? await db.ProductOptions.CountAsync(x => x.TenantId == tenantId, cancellationToken) : 0;
        return new(products, orders, returns, invoices, categories, brands, options);
    }

    private Task DeleteReturnsAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        DELETE FROM sales.return_evidence e USING sales.return_claims c WHERE e."TenantId"={{tenantId}} AND c."TenantId"={{tenantId}} AND e."ClaimId"=c."Id" AND (CAST({{connectionId}} AS uuid) IS NULL OR c."ConnectionId"={{connectionId}});
        DELETE FROM sales.return_stock_dispositions d USING sales.return_claims c WHERE d."TenantId"={{tenantId}} AND c."TenantId"={{tenantId}} AND d."ClaimId"=c."Id" AND (CAST({{connectionId}} AS uuid) IS NULL OR c."ConnectionId"={{connectionId}});
        DELETE FROM sales.return_decisions d USING sales.return_claims c WHERE d."TenantId"={{tenantId}} AND c."TenantId"={{tenantId}} AND d."ClaimId"=c."Id" AND (CAST({{connectionId}} AS uuid) IS NULL OR c."ConnectionId"={{connectionId}});
        DELETE FROM sales.return_lines l USING sales.return_claims c WHERE l."TenantId"={{tenantId}} AND c."TenantId"={{tenantId}} AND l."ClaimId"=c."Id" AND (CAST({{connectionId}} AS uuid) IS NULL OR c."ConnectionId"={{connectionId}});
        DELETE FROM sales.return_claims WHERE "TenantId"={{tenantId}} AND (CAST({{connectionId}} AS uuid) IS NULL OR "ConnectionId"={{connectionId}});
        """, cancellationToken);

    private Task DeleteInvoicesAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        CREATE TEMP TABLE IF NOT EXISTS purge_invoices ("Id" uuid PRIMARY KEY) ON COMMIT DROP;
        TRUNCATE purge_invoices;
        INSERT INTO purge_invoices SELECT i."Id" FROM billing.invoices i LEFT JOIN sales.orders o ON o."TenantId"=i."TenantId" AND o."Id"=i."OrderId" WHERE i."TenantId"={{tenantId}} AND (CAST({{connectionId}} AS uuid) IS NULL OR i."ProviderConnectionId"={{connectionId}} OR o."ConnectionId"={{connectionId}});
        DELETE FROM billing.marketplace_deliveries d USING purge_invoices p WHERE d."TenantId"={{tenantId}} AND d."InvoiceId"=p."Id";
        DELETE FROM billing.invoice_submission_attempts a USING purge_invoices p WHERE a."TenantId"={{tenantId}} AND a."InvoiceId"=p."Id";
        DELETE FROM billing.invoice_documents d USING purge_invoices p WHERE d."TenantId"={{tenantId}} AND d."InvoiceId"=p."Id";
        DELETE FROM billing.invoice_party_snapshots s USING purge_invoices p WHERE s."TenantId"={{tenantId}} AND s."InvoiceId"=p."Id";
        DELETE FROM billing.invoice_lines l USING purge_invoices p WHERE l."TenantId"={{tenantId}} AND l."InvoiceId"=p."Id";
        DELETE FROM billing.invoices i USING purge_invoices p WHERE i."TenantId"={{tenantId}} AND i."Id"=p."Id";
        """, cancellationToken);

    private Task DeleteOrdersAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        CREATE TEMP TABLE IF NOT EXISTS purge_orders ("Id" uuid PRIMARY KEY) ON COMMIT DROP;
        TRUNCATE purge_orders;
        INSERT INTO purge_orders SELECT "Id" FROM sales.orders WHERE "TenantId"={{tenantId}} AND (CAST({{connectionId}} AS uuid) IS NULL OR "ConnectionId"={{connectionId}});
        DELETE FROM sales.shipment_document_attempts a USING sales.shipment_packages p, purge_orders o WHERE a."TenantId"={{tenantId}} AND a."PackageId"=p."Id" AND p."OrderId"=o."Id";
        DELETE FROM sales.shipment_documents d USING sales.shipment_packages p, purge_orders o WHERE d."TenantId"={{tenantId}} AND d."PackageId"=p."Id" AND p."OrderId"=o."Id";
        DELETE FROM sales.order_status_history h USING purge_orders o WHERE h."TenantId"={{tenantId}} AND h."OrderId"=o."Id";
        DELETE FROM sales.package_line_allocations a USING sales.shipment_packages p, purge_orders o WHERE a."TenantId"={{tenantId}} AND a."PackageId"=p."Id" AND p."OrderId"=o."Id";
        DELETE FROM sales.shipment_packages p USING purge_orders o WHERE p."TenantId"={{tenantId}} AND p."OrderId"=o."Id";
        DELETE FROM sales.order_financial_allocations a USING purge_orders o WHERE a."TenantId"={{tenantId}} AND a."OrderId"=o."Id";
        DELETE FROM sales.order_lines l USING purge_orders o WHERE l."TenantId"={{tenantId}} AND l."OrderId"=o."Id";
        DELETE FROM sales.orders o USING purge_orders p WHERE o."TenantId"={{tenantId}} AND o."Id"=p."Id";
        """, cancellationToken);

    private Task DeleteCategoriesAsync(Guid tenantId, CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        UPDATE catalog.products SET "CategoryId"=NULL WHERE "TenantId"={{tenantId}};
        DELETE FROM catalog.category_mappings WHERE "TenantId"={{tenantId}};
        UPDATE catalog.categories SET "ParentId"=NULL WHERE "TenantId"={{tenantId}};
        DELETE FROM catalog.categories WHERE "TenantId"={{tenantId}};
        """, cancellationToken);

    private Task DeleteBrandsAsync(Guid tenantId, CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        UPDATE catalog.products SET "BrandId"=NULL WHERE "TenantId"={{tenantId}};
        DELETE FROM catalog.brand_mappings WHERE "TenantId"={{tenantId}};
        DELETE FROM catalog.brands WHERE "TenantId"={{tenantId}};
        """, cancellationToken);

    private Task DeleteOptionsAsync(Guid tenantId, CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        DELETE FROM catalog.variant_option_values WHERE "TenantId"={{tenantId}};
        DELETE FROM catalog.product_option_values WHERE "TenantId"={{tenantId}};
        DELETE FROM catalog.product_options WHERE "TenantId"={{tenantId}};
        """, cancellationToken);

    private Task DeleteProductsAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        CREATE TEMP TABLE IF NOT EXISTS purge_products ("Id" uuid PRIMARY KEY) ON COMMIT DROP;
        TRUNCATE purge_products;
        INSERT INTO purge_products SELECT p."Id" FROM catalog.products p WHERE p."TenantId"={{tenantId}} AND (CAST({{connectionId}} AS uuid) IS NULL OR (EXISTS (SELECT 1 FROM catalog.marketplace_product_links l WHERE l."TenantId"={{tenantId}} AND l."ProductId"=p."Id" AND l."ConnectionId"={{connectionId}}) AND NOT EXISTS (SELECT 1 FROM catalog.marketplace_product_links other_link WHERE other_link."TenantId"={{tenantId}} AND other_link."ProductId"=p."Id" AND other_link."ConnectionId"<> {{connectionId}})));
        UPDATE sales.order_lines SET "VariantId"=NULL WHERE "TenantId"={{tenantId}} AND "VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_products p ON p."Id"=v."ProductId" WHERE v."TenantId"={{tenantId}});
        DELETE FROM catalog.import_decisions WHERE "TenantId"={{tenantId}} AND "CandidateId" IN (SELECT c."Id" FROM catalog.import_match_candidates c WHERE c."TenantId"={{tenantId}} AND (c."ProductId" IN (SELECT "Id" FROM purge_products) OR c."VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_products p ON p."Id"=v."ProductId")));
        DELETE FROM catalog.import_match_candidates c WHERE c."TenantId"={{tenantId}} AND (c."ProductId" IN (SELECT "Id" FROM purge_products) OR c."VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_products p ON p."Id"=v."ProductId"));
        DELETE FROM catalog.field_provenance f WHERE f."TenantId"={{tenantId}} AND (f."ProductId" IN (SELECT "Id" FROM purge_products) OR f."VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_products p ON p."Id"=v."ProductId"));
        DELETE FROM inventory.channel_price_history h USING inventory.channel_offers o, catalog.product_variants v, purge_products p WHERE h."TenantId"={{tenantId}} AND h."OfferId"=o."Id" AND o."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM inventory.channel_offers o USING catalog.product_variants v, purge_products p WHERE o."TenantId"={{tenantId}} AND o."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM inventory.stock_reservations r USING inventory.inventory_items i, catalog.product_variants v, purge_products p WHERE r."TenantId"={{tenantId}} AND r."InventoryItemId"=i."Id" AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM inventory.stock_ledger_entries l USING inventory.inventory_items i, catalog.product_variants v, purge_products p WHERE l."TenantId"={{tenantId}} AND l."InventoryItemId"=i."Id" AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM inventory.inventory_items i USING catalog.product_variants v, purge_products p WHERE i."TenantId"={{tenantId}} AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM catalog.marketplace_listing_states s USING catalog.product_variants v, purge_products p WHERE s."TenantId"={{tenantId}} AND s."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM catalog.marketplace_variant_links l USING catalog.product_variants v, purge_products p WHERE l."TenantId"={{tenantId}} AND l."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM catalog.channel_listing_profiles p USING purge_products targets WHERE p."TenantId"={{tenantId}} AND p."ProductId"=targets."Id";
        DELETE FROM catalog.marketplace_product_links l USING purge_products p WHERE l."TenantId"={{tenantId}} AND l."ProductId"=p."Id";
        DELETE FROM catalog.product_media m USING purge_products p WHERE m."TenantId"={{tenantId}} AND m."ProductId"=p."Id";
        DELETE FROM catalog.product_attribute_assignments a USING purge_products p WHERE a."TenantId"={{tenantId}} AND a."ProductId"=p."Id";
        DELETE FROM catalog.variant_option_values x USING catalog.product_variants v, purge_products p WHERE x."TenantId"={{tenantId}} AND x."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM catalog.product_option_values x USING catalog.product_options o, purge_products p WHERE x."TenantId"={{tenantId}} AND x."OptionId"=o."Id" AND o."ProductId"=p."Id";
        DELETE FROM catalog.product_options o USING purge_products p WHERE o."TenantId"={{tenantId}} AND o."ProductId"=p."Id";
        DELETE FROM catalog.product_variants v USING purge_products p WHERE v."TenantId"={{tenantId}} AND v."ProductId"=p."Id";
        DELETE FROM catalog.products x USING purge_products p WHERE x."TenantId"={{tenantId}} AND x."Id"=p."Id";
        """, cancellationToken);

    private Task DeleteConnectionArtifactsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        DELETE FROM integration.job_attempts a USING integration.jobs j WHERE a."TenantId"={{tenantId}} AND a."JobId"=j."Id" AND j."ConnectionId"={{connectionId}};
        DELETE FROM integration.jobs WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.reconciliation_differences d USING integration.reconciliation_runs r WHERE d."TenantId"={{tenantId}} AND d."RunId"=r."Id" AND r."ConnectionId"={{connectionId}};
        DELETE FROM integration.reconciliation_runs WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM catalog.category_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM catalog.brand_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM catalog.attribute_value_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM catalog.attribute_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.reference_items i USING integration.reference_snapshots s WHERE i."TenantId"={{tenantId}} AND i."SnapshotId"=s."Id" AND s."ConnectionId"={{connectionId}};
        DELETE FROM integration.reference_snapshots WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM catalog.channel_listing_profiles WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM catalog.marketplace_listing_states WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM catalog.marketplace_variant_links WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM catalog.marketplace_product_links WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM catalog.external_identifier_aliases WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM sales.cargo_provider_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM inventory.channel_price_history h USING inventory.channel_offers o WHERE h."TenantId"={{tenantId}} AND h."OfferId"=o."Id" AND o."ConnectionId"={{connectionId}};
        DELETE FROM inventory.channel_offers WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM inventory.connection_location_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM inventory.connection_inventory_policies WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.webhook_subscriptions WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.sync_cursors WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.connection_sync_policies WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.platform_capabilities WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.platform_credentials WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM billing.invoice_policies WHERE "TenantId"={{tenantId}} AND "ProviderConnectionId"={{connectionId}};
        """, cancellationToken);

    private AuditLog Audit(Guid tenantId, Guid actorUserId, string action, string targetType, string targetId, string reason, string correlationId) => new() { TenantId = tenantId, ActorUserId = actorUserId, Action = action, TargetType = targetType, TargetId = targetId, Reason = reason, CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow() };
    private static ServiceResult<OperationalDataResetView> Invalid(string message) => ServiceResult<OperationalDataResetView>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { ["confirmation"] = [message] });
}
