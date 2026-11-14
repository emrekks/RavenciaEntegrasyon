using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class OperationalDataMaintenanceService(AppDbContext db, TimeProvider timeProvider, IDashboardReadService dashboard, ILogger<OperationalDataMaintenanceService> logger, IInstantNotificationService? instantNotifications = null) : IOperationalDataMaintenanceService
{
    private static readonly HashSet<string> ConnectionDeleteScopes = new(StringComparer.Ordinal) { "PRODUCTS", "ORDERS", "RETURNS", "INVOICES" };

    public async Task<ServiceResult<OperationalDataResetView>> DeleteConnectionAsync(Guid tenantId, Guid actorUserId, Guid connectionId, long expectedVersion, DeleteConnectionCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED", cancellationToken);
        if (connection is null) return ServiceResult<OperationalDataResetView>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        if (connection.Version != expectedVersion) return ServiceResult<OperationalDataResetView>.Fail("CONCURRENCY_CONFLICT", "Kayıt siz düzenlerken başka bir işlemle güncellendi. Sayfayı yenileyip tekrar deneyin.", 412);
        if (!string.Equals(command.Confirmation?.Trim(), connection.DisplayName, StringComparison.Ordinal)) return Invalid($"Onay alanına bağlantı adını tam olarak yazın: {connection.DisplayName}");

        // A test environment's invoices (an e-invoice provider's STAGE) are no fiscal record: they go with the connection.
        // Whether an invoice is a test is the environment of the provider that issued it, not of the connection being
        // deleted: a live marketplace's orders invoiced through a STAGE provider are tests, a STAGE marketplace's orders
        // invoiced through the live provider are not.
        if (await IssuedInvoicesAsync(tenantId, connectionId, cancellationToken, fiscalOnly: true) is > 0 and var issued) return IssuedInvoicesKept(issued);
        // The order notification registered at the marketplace goes first, as Webhook'u Kapat does: once the
        // connection's keys are gone nothing could remove it any more.
        var webhookNotice = await CloseMarketplaceWebhookAsync(connection, correlationId, cancellationToken);
        var scopes = new HashSet<string>(ConnectionDeleteScopes, StringComparer.Ordinal);
        var counts = await CountsAsync(tenantId, connectionId, scopes, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await BeginFilePurgeAsync(cancellationToken);
        await DeleteReturnsAsync(tenantId, connectionId, cancellationToken);
        await DeleteInvoicesAsync(tenantId, connectionId, cancellationToken);
        await DeleteOrdersAsync(tenantId, connectionId, cancellationToken);
        await DeleteProductsAsync(tenantId, connectionId, cancellationToken);
        await DeleteConnectionArtifactsAsync(tenantId, connectionId, cancellationToken);
        await ArchivePurgedFilesAsync(tenantId, cancellationToken);
        connection.Status = "DELETED";
        connection.LastErrorCode = null;
        connection.LastErrorClass = null;
        connection.Version++;
        db.AuditLogs.Add(Audit(tenantId, actorUserId, "CONNECTION_DEEP_DELETED", "PlatformConnection", connectionId.ToString("D"), $"{connection.PlatformCode}:{connection.ExternalStoreId}", correlationId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await RefreshDashboardBestEffortAsync(tenantId);
        return ServiceResult<OperationalDataResetView>.Ok(counts with { ConnectionDeleted = true, WebhookNotice = webhookNotice });
    }

    // Removes the connection's active order notification at the marketplace (Trendyol, Shopify) through the same call as
    // Webhook'u Kapat. A failure does not stop the deletion: the panel refuses that address's deliveries anyway, and the
    // returned notice tells the seller to remove it at the marketplace. Hepsiburada registers by hand, so only the seller
    // can have it removed there.
    private async Task<string?> CloseMarketplaceWebhookAsync(PlatformConnection connection, string correlationId, CancellationToken cancellationToken)
    {
        var externalId = await db.WebhookSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.Id && (x.Status == "ACTIVE" || x.Status == InstantNotificationService.PausedStatus) && x.ExternalSubscriptionId != null)
            .OrderBy(x => x.Status == "ACTIVE" ? 0 : 1)
            .Select(x => x.ExternalSubscriptionId).FirstOrDefaultAsync(cancellationToken);
        if (externalId is null) return null;
        var platform = connection.PlatformCode switch { "SHOPIFY" => "Shopify", "HEPSIBURADA" => "Hepsiburada", _ => "Trendyol" };
        if (externalId == InstantNotificationService.ManualRegistration)
            return "Hepsiburada’ya bildirdiğiniz webhook adresi panelden kaldırılamaz; Hepsiburada’dan bu adresin silinmesini isteyin.";
        if (instantNotifications is null) return $"{platform} webhook kaydı panelden kapatılamadı; {platform} tarafından silin.";
        try
        {
            var closed = await instantNotifications.DisableAsync(connection.TenantId, connection.Id, correlationId, cancellationToken);
            if (closed.Succeeded) return null;
            logger.LogWarning("Order webhook of deleted connection {ConnectionId} could not be removed at {Platform}: {Code}", connection.Id, connection.PlatformCode, closed.Error?.Code);
            return $"{closed.Error?.Message} Bağlantı silindi; webhook kaydını {platform} tarafında silin.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Order webhook of deleted connection {ConnectionId} could not be removed at {Platform}", connection.Id, connection.PlatformCode);
            return $"{platform} webhook kaydı kapatılamadı; {platform} satıcı panelinden silin.";
        }
    }

    // A connection's list sizes come from its current snapshots' item counts: counting the rows themselves scanned the
    // whole reference table (13 million rows, 15-18 s a count on 26 Oct 2026).
    private Task<int> CurrentListSizeAsync(Guid tenantId, Guid connectionId, string[] resourceTypes, CancellationToken cancellationToken) =>
        db.ReferenceSnapshots.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && resourceTypes.Contains(x.ResourceType) && x.IsCurrent).SumAsync(x => x.ItemCount, cancellationToken);

    private async Task<OperationalDataResetView> CountsAsync(Guid tenantId, Guid? connectionId, HashSet<string> scopes, CancellationToken cancellationToken)
    {
        var products = scopes.Contains("PRODUCTS") ? connectionId is null ? await db.Products.CountAsync(x => x.TenantId == tenantId, cancellationToken) : await db.MarketplaceProductLinks.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).Select(x => x.ProductId).Distinct().CountAsync(cancellationToken) : 0;
        var orders = scopes.Contains("ORDERS") ? await db.Orders.CountAsync(x => x.TenantId == tenantId && (connectionId == null || x.ConnectionId == connectionId), cancellationToken) : 0;
        var returns = scopes.Contains("RETURNS") || scopes.Contains("ORDERS") ? await db.ReturnClaims.CountAsync(x => x.TenantId == tenantId && (connectionId == null || x.ConnectionId == connectionId), cancellationToken) : 0;
        var invoices = scopes.Contains("INVOICES") || scopes.Contains("ORDERS") ? await db.Invoices.CountAsync(x => x.TenantId == tenantId && (connectionId == null || x.ProviderConnectionId == connectionId || db.Orders.Any(order => order.TenantId == tenantId && order.Id == x.OrderId && order.ConnectionId == connectionId)), cancellationToken) : 0;
        var categories = scopes.Contains("CATEGORIES") ? connectionId is null ? await db.Categories.CountAsync(x => x.TenantId == tenantId, cancellationToken) : await CurrentListSizeAsync(tenantId, connectionId.Value, ["CATEGORIES"], cancellationToken) : 0;
        var brands = scopes.Contains("BRANDS") ? connectionId is null ? await db.Brands.CountAsync(x => x.TenantId == tenantId, cancellationToken) : await CurrentListSizeAsync(tenantId, connectionId.Value, ["BRANDS"], cancellationToken) : 0;
        var options = scopes.Contains("OPTIONS") ? await db.ProductOptions.CountAsync(x => x.TenantId == tenantId, cancellationToken) : 0;
        var categoryAttributes = scopes.Contains("CATEGORY_ATTRIBUTES") || connectionId is not null && scopes.Contains("CATEGORIES") ? connectionId is null ? await db.AttributeDefinitions.CountAsync(x => x.TenantId == tenantId, cancellationToken) : await CurrentListSizeAsync(tenantId, connectionId.Value, ["CATEGORY_ATTRIBUTES", "ATTRIBUTE_VALUES"], cancellationToken) : 0;
        var commissionTariffs = connectionId is not null && scopes.Contains("COMMISSION_TARIFFS") ? await db.CommissionTariffs.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken) : 0;
        var promotionOffers = connectionId is not null && scopes.Contains("PROMOTION_OFFERS") ? await db.PromotionOfferFiles.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken) : 0;
        return new(products, orders, returns, invoices, categories, brands, options, categoryAttributes, CommissionTariffs: commissionTariffs, PromotionOffers: promotionOffers);
    }

    // KeepIssued: the returns of an order with an issued invoice stay with it.
    private Task DeleteReturnsAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken, bool keepIssued = false) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        INSERT INTO purge_files SELECT e."FileAssetId" FROM sales.return_evidence e JOIN sales.return_claims c ON c."Id"=e."ClaimId" AND c."TenantId"={{tenantId}} WHERE e."TenantId"={{tenantId}} AND (CAST({{connectionId}} AS uuid) IS NULL OR c."ConnectionId"={{connectionId}}) AND NOT ({{keepIssued}} AND EXISTS (SELECT 1 FROM billing.invoices i WHERE i."TenantId"={{tenantId}} AND i."OrderId"=c."OrderId" AND (i."Status" NOT IN ('Draft', 'Validating', 'ValidationFailed', 'Ready', 'Rejected', 'CancelledLocal') OR i."IssuedAt" IS NOT NULL OR i."EttnUuid" IS NOT NULL OR i."InvoiceNumber" IS NOT NULL))) ON CONFLICT DO NOTHING;
        DELETE FROM sales.return_evidence e USING sales.return_claims c WHERE e."TenantId"={{tenantId}} AND c."TenantId"={{tenantId}} AND e."ClaimId"=c."Id" AND (CAST({{connectionId}} AS uuid) IS NULL OR c."ConnectionId"={{connectionId}}) AND NOT ({{keepIssued}} AND EXISTS (SELECT 1 FROM billing.invoices i WHERE i."TenantId"={{tenantId}} AND i."OrderId"=c."OrderId" AND (i."Status" NOT IN ('Draft', 'Validating', 'ValidationFailed', 'Ready', 'Rejected', 'CancelledLocal') OR i."IssuedAt" IS NOT NULL OR i."EttnUuid" IS NOT NULL OR i."InvoiceNumber" IS NOT NULL)));
        DELETE FROM sales.return_stock_dispositions d USING sales.return_claims c WHERE d."TenantId"={{tenantId}} AND c."TenantId"={{tenantId}} AND d."ClaimId"=c."Id" AND (CAST({{connectionId}} AS uuid) IS NULL OR c."ConnectionId"={{connectionId}}) AND NOT ({{keepIssued}} AND EXISTS (SELECT 1 FROM billing.invoices i WHERE i."TenantId"={{tenantId}} AND i."OrderId"=c."OrderId" AND (i."Status" NOT IN ('Draft', 'Validating', 'ValidationFailed', 'Ready', 'Rejected', 'CancelledLocal') OR i."IssuedAt" IS NOT NULL OR i."EttnUuid" IS NOT NULL OR i."InvoiceNumber" IS NOT NULL)));
        DELETE FROM sales.return_decisions d USING sales.return_claims c WHERE d."TenantId"={{tenantId}} AND c."TenantId"={{tenantId}} AND d."ClaimId"=c."Id" AND (CAST({{connectionId}} AS uuid) IS NULL OR c."ConnectionId"={{connectionId}}) AND NOT ({{keepIssued}} AND EXISTS (SELECT 1 FROM billing.invoices i WHERE i."TenantId"={{tenantId}} AND i."OrderId"=c."OrderId" AND (i."Status" NOT IN ('Draft', 'Validating', 'ValidationFailed', 'Ready', 'Rejected', 'CancelledLocal') OR i."IssuedAt" IS NOT NULL OR i."EttnUuid" IS NOT NULL OR i."InvoiceNumber" IS NOT NULL)));
        DELETE FROM sales.return_lines l USING sales.return_claims c WHERE l."TenantId"={{tenantId}} AND c."TenantId"={{tenantId}} AND l."ClaimId"=c."Id" AND (CAST({{connectionId}} AS uuid) IS NULL OR c."ConnectionId"={{connectionId}}) AND NOT ({{keepIssued}} AND EXISTS (SELECT 1 FROM billing.invoices i WHERE i."TenantId"={{tenantId}} AND i."OrderId"=c."OrderId" AND (i."Status" NOT IN ('Draft', 'Validating', 'ValidationFailed', 'Ready', 'Rejected', 'CancelledLocal') OR i."IssuedAt" IS NOT NULL OR i."EttnUuid" IS NOT NULL OR i."InvoiceNumber" IS NOT NULL)));
        DELETE FROM sales.return_claims c WHERE c."TenantId"={{tenantId}} AND (CAST({{connectionId}} AS uuid) IS NULL OR c."ConnectionId"={{connectionId}}) AND NOT ({{keepIssued}} AND EXISTS (SELECT 1 FROM billing.invoices i WHERE i."TenantId"={{tenantId}} AND i."OrderId"=c."OrderId" AND (i."Status" NOT IN ('Draft', 'Validating', 'ValidationFailed', 'Ready', 'Rejected', 'CancelledLocal') OR i."IssuedAt" IS NOT NULL OR i."EttnUuid" IS NOT NULL OR i."InvoiceNumber" IS NOT NULL)));
        """, cancellationToken);

    // KeepIssued: an issued invoice stays; drafts and failed ones go.
    private Task DeleteInvoicesAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken, bool keepIssued = false) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        CREATE TEMP TABLE IF NOT EXISTS purge_invoices ("Id" uuid PRIMARY KEY) ON COMMIT DROP;
        TRUNCATE purge_invoices;
        INSERT INTO purge_invoices SELECT i."Id" FROM billing.invoices i LEFT JOIN sales.orders o ON o."TenantId"=i."TenantId" AND o."Id"=i."OrderId" WHERE i."TenantId"={{tenantId}} AND (CAST({{connectionId}} AS uuid) IS NULL OR i."ProviderConnectionId"={{connectionId}} OR o."ConnectionId"={{connectionId}}) AND NOT ({{keepIssued}} AND (i."Status" NOT IN ('Draft', 'Validating', 'ValidationFailed', 'Ready', 'Rejected', 'CancelledLocal') OR i."IssuedAt" IS NOT NULL OR i."EttnUuid" IS NOT NULL OR i."InvoiceNumber" IS NOT NULL));
        INSERT INTO purge_files SELECT d."FileAssetId" FROM billing.invoice_documents d JOIN purge_invoices p ON p."Id"=d."InvoiceId" WHERE d."TenantId"={{tenantId}} ON CONFLICT DO NOTHING;
        DELETE FROM billing.marketplace_delivery_states s USING purge_invoices p WHERE s."TenantId"={{tenantId}} AND s."InvoiceId"=p."Id";
        DELETE FROM billing.marketplace_deliveries d USING purge_invoices p WHERE d."TenantId"={{tenantId}} AND d."InvoiceId"=p."Id";
        UPDATE billing.invoices i SET "OriginalInvoiceId"=NULL FROM purge_invoices p WHERE i."TenantId"={{tenantId}} AND i."OriginalInvoiceId"=p."Id";
        DELETE FROM billing.invoice_submission_attempts a USING purge_invoices p WHERE a."TenantId"={{tenantId}} AND a."InvoiceId"=p."Id";
        DELETE FROM billing.invoice_documents d USING purge_invoices p WHERE d."TenantId"={{tenantId}} AND d."InvoiceId"=p."Id";
        DELETE FROM billing.invoice_party_snapshots s USING purge_invoices p WHERE s."TenantId"={{tenantId}} AND s."InvoiceId"=p."Id";
        DELETE FROM billing.invoice_lines l USING purge_invoices p WHERE l."TenantId"={{tenantId}} AND l."InvoiceId"=p."Id";
        DELETE FROM billing.invoices i USING purge_invoices p WHERE i."TenantId"={{tenantId}} AND i."Id"=p."Id";
        """, cancellationToken);

    // KeepIssued: an order with an issued invoice stays with its packages and lines.
    // CarryStock: the units a removed line took from stock stay taken. Its ORDER_LINE ledger rows and reservations
    // move from the line's id to its carry key (OrderInventoryStockPolicy.ResetCarryKey), and the line the next sync
    // imports under the same marketplace ids takes them over instead of taking the units again (29 Oct 2026, P-2).
    // A reservation another removal already left under the same key and item keeps the line's id.
    private Task DeleteOrdersAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken, bool keepIssued = false, bool carryStock = false) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        CREATE TEMP TABLE IF NOT EXISTS purge_orders ("Id" uuid PRIMARY KEY) ON COMMIT DROP;
        TRUNCATE purge_orders;
        INSERT INTO purge_orders SELECT o."Id" FROM sales.orders o WHERE o."TenantId"={{tenantId}} AND (CAST({{connectionId}} AS uuid) IS NULL OR o."ConnectionId"={{connectionId}}) AND NOT ({{keepIssued}} AND EXISTS (SELECT 1 FROM billing.invoices i WHERE i."TenantId"={{tenantId}} AND i."OrderId"=o."Id" AND (i."Status" NOT IN ('Draft', 'Validating', 'ValidationFailed', 'Ready', 'Rejected', 'CancelledLocal') OR i."IssuedAt" IS NOT NULL OR i."EttnUuid" IS NOT NULL OR i."InvoiceNumber" IS NOT NULL)));
        CREATE TEMP TABLE IF NOT EXISTS carry_order_lines ("SourceId" text PRIMARY KEY, "CarryKey" text NOT NULL) ON COMMIT DROP;
        TRUNCATE carry_order_lines;
        INSERT INTO carry_order_lines SELECT l."Id"::text, {{OrderInventoryStockPolicy.ResetCarryPrefix}} || o."ConnectionId"::text || ':' || o."ExternalOrderId" || ':' || l."ExternalLineId" FROM sales.order_lines l JOIN sales.orders o ON o."TenantId"=l."TenantId" AND o."Id"=l."OrderId" JOIN purge_orders p ON p."Id"=o."Id" WHERE {{carryStock}} AND l."TenantId"={{tenantId}};
        UPDATE inventory.stock_reservations r SET "SourceId"=c."CarryKey", "Version"=r."Version"+1 FROM carry_order_lines c WHERE r."TenantId"={{tenantId}} AND r."SourceType"='ORDER_LINE' AND r."SourceId"=c."SourceId" AND NOT EXISTS (SELECT 1 FROM inventory.stock_reservations x WHERE x."TenantId"={{tenantId}} AND x."SourceType"='ORDER_LINE' AND x."SourceId"=c."CarryKey" AND x."InventoryItemId"=r."InventoryItemId");
        UPDATE inventory.stock_ledger_entries e SET "SourceId"=c."CarryKey" FROM carry_order_lines c WHERE e."TenantId"={{tenantId}} AND e."SourceType"='ORDER_LINE' AND e."SourceId"=c."SourceId";
        INSERT INTO purge_files SELECT d."FileAssetId" FROM sales.shipment_documents d JOIN sales.shipment_packages p ON p."Id"=d."PackageId" JOIN purge_orders o ON o."Id"=p."OrderId" WHERE d."TenantId"={{tenantId}} ON CONFLICT DO NOTHING;
        -- An order's uploaded invoice documents and the invoice references the marketplace reported hold a restricting key
        -- to the order and its packages: left behind, they failed the whole reset (P-6 reading, 29 Oct 2026).
        INSERT INTO purge_files SELECT d."FileAssetId" FROM billing.manual_order_invoice_documents d JOIN purge_orders o ON o."Id"=d."OrderId" WHERE d."TenantId"={{tenantId}} AND d."FileAssetId" IS NOT NULL ON CONFLICT DO NOTHING;
        DELETE FROM billing.manual_order_invoice_delivery_states s USING billing.manual_order_invoice_documents d, purge_orders o WHERE s."TenantId"={{tenantId}} AND d."TenantId"={{tenantId}} AND s."DocumentId"=d."Id" AND d."OrderId"=o."Id";
        DELETE FROM billing.manual_order_invoice_delivery_states s USING sales.shipment_packages p, purge_orders o WHERE s."TenantId"={{tenantId}} AND p."TenantId"={{tenantId}} AND s."PackageId"=p."Id" AND p."OrderId"=o."Id";
        DELETE FROM billing.manual_order_invoice_documents d USING purge_orders o WHERE d."TenantId"={{tenantId}} AND d."OrderId"=o."Id";
        DELETE FROM sales.marketplace_invoice_reference_history h USING purge_orders o WHERE h."TenantId"={{tenantId}} AND h."OrderId"=o."Id";
        DELETE FROM sales.shipment_document_attempts a USING sales.shipment_packages p, purge_orders o WHERE a."TenantId"={{tenantId}} AND a."PackageId"=p."Id" AND p."OrderId"=o."Id";
        DELETE FROM sales.shipment_documents d USING sales.shipment_packages p, purge_orders o WHERE d."TenantId"={{tenantId}} AND d."PackageId"=p."Id" AND p."OrderId"=o."Id";
        DELETE FROM sales.order_status_history h USING purge_orders o WHERE h."TenantId"={{tenantId}} AND h."OrderId"=o."Id";
        DELETE FROM sales.package_line_allocations a USING sales.shipment_packages p, purge_orders o WHERE a."TenantId"={{tenantId}} AND a."PackageId"=p."Id" AND p."OrderId"=o."Id";
        DELETE FROM sales.shipment_packages p USING purge_orders o WHERE p."TenantId"={{tenantId}} AND p."OrderId"=o."Id";
        DELETE FROM sales.order_financial_allocations a USING purge_orders o WHERE a."TenantId"={{tenantId}} AND a."OrderId"=o."Id";
        DELETE FROM sales.order_lines l USING purge_orders o WHERE l."TenantId"={{tenantId}} AND l."OrderId"=o."Id";
        DELETE FROM sales.orders o USING purge_orders p WHERE o."TenantId"={{tenantId}} AND o."Id"=p."Id";
        """, cancellationToken);

    // ImportedOnly (full reset): of the connection's products only those its own import created go. A product created
    // on the panel, or matched to a panel product by its codes, keeps its row and loses only the link (30 Oct 2026
    // owner decision). The link of a product an import created is written with the product, so both ids (version 7)
    // carry the same moment; a product older than its link was there before, and an id without a time keeps the product.
    // (The link's LastImportedAt does not tell them apart: a sync that matches a panel product by barcode sets it too.)
    private Task DeleteProductsAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken, bool importedOnly = false) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        CREATE TEMP TABLE IF NOT EXISTS purge_products ("Id" uuid PRIMARY KEY) ON COMMIT DROP;
        TRUNCATE purge_products;
        INSERT INTO purge_products SELECT p."Id" FROM catalog.products p WHERE p."TenantId"={{tenantId}} AND (CAST({{connectionId}} AS uuid) IS NULL OR (EXISTS (SELECT 1 FROM catalog.marketplace_product_links l WHERE l."TenantId"={{tenantId}} AND l."ProductId"=p."Id" AND l."ConnectionId"={{connectionId}}
                AND (NOT {{importedOnly}} OR abs(extract(epoch FROM uuid_extract_timestamp(p."Id") - uuid_extract_timestamp(l."Id"))) <= {{ImportedProductLinkSeconds}}))
            AND NOT EXISTS (SELECT 1 FROM catalog.marketplace_product_links other_link WHERE other_link."TenantId"={{tenantId}} AND other_link."ProductId"=p."Id" AND other_link."ConnectionId"<> {{connectionId}})
            AND NOT ({{importedOnly}} AND EXISTS (SELECT 1 FROM catalog.marketplace_variant_links other_variant JOIN catalog.product_variants v ON v."TenantId"=other_variant."TenantId" AND v."Id"=other_variant."VariantId" WHERE other_variant."TenantId"={{tenantId}} AND v."ProductId"=p."Id" AND other_variant."ConnectionId"<> {{connectionId}}))));
        UPDATE sales.order_lines SET "VariantId"=NULL WHERE "TenantId"={{tenantId}} AND "VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_products p ON p."Id"=v."ProductId" WHERE v."TenantId"={{tenantId}});
        DELETE FROM catalog.import_decisions WHERE "TenantId"={{tenantId}} AND "CandidateId" IN (SELECT c."Id" FROM catalog.import_match_candidates c WHERE c."TenantId"={{tenantId}} AND (c."ProductId" IN (SELECT "Id" FROM purge_products) OR c."VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_products p ON p."Id"=v."ProductId")));
        DELETE FROM catalog.import_match_candidates c WHERE c."TenantId"={{tenantId}} AND (c."ProductId" IN (SELECT "Id" FROM purge_products) OR c."VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_products p ON p."Id"=v."ProductId"));
        DELETE FROM catalog.field_provenance f WHERE f."TenantId"={{tenantId}} AND (f."ProductId" IN (SELECT "Id" FROM purge_products) OR f."VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_products p ON p."Id"=v."ProductId"));
        DELETE FROM inventory.channel_price_history h USING inventory.channel_offers o, catalog.product_variants v, purge_products p WHERE h."TenantId"={{tenantId}} AND h."OfferId"=o."Id" AND o."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM inventory.channel_offers o USING catalog.product_variants v, purge_products p WHERE o."TenantId"={{tenantId}} AND o."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM inventory.channel_inventory_observations o USING catalog.product_variants v, purge_products p WHERE o."TenantId"={{tenantId}} AND o."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM inventory.stock_reservations r USING inventory.inventory_items i, catalog.product_variants v, purge_products p WHERE r."TenantId"={{tenantId}} AND r."InventoryItemId"=i."Id" AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM inventory.stock_ledger_entries l USING inventory.inventory_items i, catalog.product_variants v, purge_products p WHERE l."TenantId"={{tenantId}} AND l."InventoryItemId"=i."Id" AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
        -- A return of another connection may have put stock back into one of these items (26 Oct 2026: the reset failed on it).
        DELETE FROM sales.return_stock_dispositions d USING inventory.inventory_items i, catalog.product_variants v, purge_products p WHERE d."TenantId"={{tenantId}} AND d."InventoryItemId"=i."Id" AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM inventory.inventory_items i USING catalog.product_variants v, purge_products p WHERE i."TenantId"={{tenantId}} AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM catalog.marketplace_listing_states s USING catalog.product_variants v, purge_products p WHERE s."TenantId"={{tenantId}} AND s."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM catalog.marketplace_variant_links l USING catalog.product_variants v, purge_products p WHERE l."TenantId"={{tenantId}} AND l."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM catalog.channel_listing_profiles p USING purge_products targets WHERE p."TenantId"={{tenantId}} AND p."ProductId"=targets."Id";
        DELETE FROM catalog.marketplace_product_links l USING purge_products p WHERE l."TenantId"={{tenantId}} AND l."ProductId"=p."Id";
        INSERT INTO purge_files SELECT m."FileAssetId" FROM catalog.product_media m JOIN purge_products p ON p."Id"=m."ProductId" WHERE m."TenantId"={{tenantId}} ON CONFLICT DO NOTHING;
        INSERT INTO purge_files SELECT c."LastGeneratedAssetId" FROM catalog.product_size_charts c JOIN purge_products p ON p."Id"=c."ProductId" WHERE c."TenantId"={{tenantId}} AND c."LastGeneratedAssetId" IS NOT NULL ON CONFLICT DO NOTHING;
        DELETE FROM catalog.product_media m USING purge_products p WHERE m."TenantId"={{tenantId}} AND m."ProductId"=p."Id";
        DELETE FROM catalog.product_attribute_assignments a USING purge_products p WHERE a."TenantId"={{tenantId}} AND a."ProductId"=p."Id";
        DELETE FROM catalog.variant_option_values x USING catalog.product_variants v, purge_products p WHERE x."TenantId"={{tenantId}} AND x."VariantId"=v."Id" AND v."ProductId"=p."Id";
        DELETE FROM catalog.product_option_values x USING catalog.product_options o, purge_products p WHERE x."TenantId"={{tenantId}} AND x."OptionId"=o."Id" AND o."ProductId"=p."Id";
        DELETE FROM catalog.product_options o USING purge_products p WHERE o."TenantId"={{tenantId}} AND o."ProductId"=p."Id";
        DELETE FROM catalog.product_variants v USING purge_products p WHERE v."TenantId"={{tenantId}} AND v."ProductId"=p."Id";
        DELETE FROM catalog.product_change_entries c USING purge_products p WHERE c."TenantId"={{tenantId}} AND c."ProductId"=p."Id";
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
        DELETE FROM inventory.channel_inventory_observations WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM inventory.connection_location_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM inventory.connection_inventory_policies WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.webhook_subscriptions WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM sales.marketplace_questions WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.marketplace_question_sync_states WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.sync_cursors WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.connection_sync_policies WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.platform_capabilities WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM integration.platform_credentials WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
        DELETE FROM billing.invoice_policies WHERE "TenantId"={{tenantId}} AND "ProviderConnectionId"={{connectionId}};
        """, cancellationToken);

    // Issued invoices are fiscal records the seller must keep: a reset or deletion that would remove one is
    // refused. "Issued" is anything that left the draft stages, or got a number, an ETTN or an issue date.
    private static readonly InvoiceStatus[] UnissuedInvoiceStatuses = [InvoiceStatus.Draft, InvoiceStatus.Validating, InvoiceStatus.ValidationFailed, InvoiceStatus.Ready, InvoiceStatus.Rejected, InvoiceStatus.CancelledLocal];

    private static readonly string[] TestProviderEnvironments = ["STAGE", "SANDBOX"];

    // FiscalOnly leaves out the invoices a test environment's provider issued.
    private Task<int> IssuedInvoicesAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken, bool fiscalOnly = false) =>
        db.Invoices.CountAsync(x => x.TenantId == tenantId
            && (connectionId == null || x.ProviderConnectionId == connectionId || db.Orders.Any(order => order.TenantId == tenantId && order.Id == x.OrderId && order.ConnectionId == connectionId))
            && (!UnissuedInvoiceStatuses.Contains(x.Status) || x.IssuedAt != null || x.EttnUuid != null || x.InvoiceNumber != null)
            && (!fiscalOnly || !db.PlatformConnections.Any(provider => provider.TenantId == tenantId && provider.Id == x.ProviderConnectionId && TestProviderEnvironments.Contains(provider.Environment))), cancellationToken);

    private static ServiceResult<OperationalDataResetView> IssuedInvoicesKept(int count) =>
        ServiceResult<OperationalDataResetView>.Fail("ISSUED_INVOICES_KEPT", $"Seçilen kayıtlar {count} kesilmiş fatura içeriyor; mali kayıtlar silinemez. Faturaları ve bağlı siparişleri seçimden çıkarın.", 409);

    // Stored files (invoice PDFs, labels, return evidence, product images) of the rows a reset removes are
    // collected while the rows go, then archived for removal (Status PURGE_PENDING) when nothing else still
    // points at them; PurgedFileRetention deletes the files from disk. A temporary table scoped to the
    // transaction carries the ids from statement to statement.
    public const string PurgePendingStatus = "PURGE_PENDING";

    private Task BeginFilePurgeAsync(CancellationToken cancellationToken) => db.Database.ExecuteSqlRawAsync("""
        CREATE TEMP TABLE IF NOT EXISTS purge_files ("Id" uuid PRIMARY KEY) ON COMMIT DROP;
        TRUNCATE purge_files;
        """, cancellationToken);

    private Task ArchivePurgedFilesAsync(Guid tenantId, CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync($$"""
        UPDATE ops.file_assets f SET "Status"={{PurgePendingStatus}}, "ArchivedAt"={{timeProvider.GetUtcNow()}}
        FROM purge_files p
        WHERE f."TenantId"={{tenantId}} AND f."Id"=p."Id" AND f."Status"<>{{PurgePendingStatus}}
          AND NOT EXISTS (SELECT 1 FROM catalog.product_media m WHERE m."TenantId"={{tenantId}} AND m."FileAssetId"=f."Id" AND m."Status"<>'ARCHIVED')
          AND NOT EXISTS (SELECT 1 FROM billing.invoice_documents d WHERE d."TenantId"={{tenantId}} AND d."FileAssetId"=f."Id")
          AND NOT EXISTS (SELECT 1 FROM billing.manual_order_invoice_documents d WHERE d."TenantId"={{tenantId}} AND d."FileAssetId"=f."Id")
          AND NOT EXISTS (SELECT 1 FROM sales.return_evidence e WHERE e."TenantId"={{tenantId}} AND e."FileAssetId"=f."Id")
          AND NOT EXISTS (SELECT 1 FROM sales.shipment_documents s WHERE s."TenantId"={{tenantId}} AND s."FileAssetId"=f."Id")
          AND NOT EXISTS (SELECT 1 FROM catalog.import_sessions i WHERE i."TenantId"={{tenantId}} AND i."SourceAssetId"=f."Id");
        """, cancellationToken);

    // The dashboard snapshot is rebuilt after the reset has committed, so a failure there must not turn a finished reset
    // into an error (P-6, 29 Oct 2026: the worker's refresher wrote the same snapshot row and the request answered 500
    // after the data was gone). The rebuild runs on a clean change tracker, is tried twice with its own short time limit
    // instead of the request's, and is otherwise left to the worker, which the reset's outbox event (or the hidden or
    // deleted connection's own row change) tells to rebuild.
    internal static readonly TimeSpan DashboardRefreshTimeout = TimeSpan.FromSeconds(10);

    private async Task RefreshDashboardBestEffortAsync(Guid tenantId)
    {
        db.ChangeTracker.Clear();
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = new CancellationTokenSource(DashboardRefreshTimeout);
            try
            {
                await dashboard.RebuildTenantAsync(tenantId, timeout.Token);
                return;
            }
            catch (Exception exception) when (exception is DbUpdateException || exception is OperationCanceledException && timeout.IsCancellationRequested)
            {
                db.ChangeTracker.Clear();
                if (attempt < 2 && exception is DbUpdateException) continue;
                logger.LogWarning(exception, "Sıfırlamadan sonra pano özeti yenilenemedi; worker yenileyecek. TenantId: {TenantId}", tenantId);
                return;
            }
        }
    }

    // A data change for the worker's dashboard refresher (DashboardSnapshotRefresher) and the open panels: the rows a
    // reset removes with SQL raise no change events of their own.
    private void AnnounceReset(Guid tenantId, string resourceType, string aggregateType, Guid aggregateId)
    {
        var now = timeProvider.GetUtcNow();
        db.IntegrationOutboxEvents.Add(new IntegrationOutboxEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ResourceType = resourceType,
            OperationType = "reset",
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            PayloadJson = JsonSerializer.Serialize(new { aggregateType, aggregateId }),
            CreatedAt = now,
            NextAttemptAt = now
        });
    }

    private AuditLog Audit(Guid tenantId, Guid actorUserId, string action, string targetType, string targetId, string reason, string correlationId) => new() { TenantId = tenantId, ActorUserId = actorUserId, Action = action, TargetType = targetType, TargetId = targetId, Reason = reason, CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow() };
    private static ServiceResult<OperationalDataResetView> Invalid(string message) => ServiceResult<OperationalDataResetView>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { ["confirmation"] = [message] });
}
