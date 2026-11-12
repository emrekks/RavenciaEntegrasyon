using MarketplaceHub.Infrastructure.Files;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// What a deleted connection or a replaced key leaves behind, removed for good (owner, 12 Nov 2026: "kökten çözüm").
//
// Keys: a replaced key (RevokedAt set) is read by nothing (every reader filters RevokedAt IS NULL), so it goes. Rotation
// deletes it at once (MarketplaceConnectionService.RotateCredentialAsync); the sweep removes any left from before.
//
// Connections: Entegrasyonlar › Sil (OperationalDataMaintenanceService.DeleteConnectionAsync) removes the connection's
// data and leaves the row as Status DELETED. Afterwards every setting and derived row of the connection goes (keys,
// capabilities, sync settings, webhooks, mappings and templates, attribute defaults, stock and location settings,
// cursors, backoffs, effect records, send logs, reference lists, imports, tariffs and report files) and then the row
// itself, unless a record a seller keeps (orders, packages, labels, returns, invoices and their deliveries, finance
// and payout rows, questions) still names it: then the row stays as a tombstone the restricting keys need, with no
// settings or keys.
public static class ConnectionLeftovers
{
    public const string DeletedStatus = "DELETED";
    private const int ConnectionBatchSize = 20;

    public static Task<int> RemoveRevokedCredentialsAsync(AppDbContext db, CancellationToken cancellationToken) =>
        db.PlatformCredentials.Where(x => x.RevokedAt != null).ExecuteDeleteAsync(cancellationToken);

    // Returns how many tombstones were removed outright.
    public static async Task<int> SweepDeletedConnectionsAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken, Guid? tenantId = null)
    {
        var deleted = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.Status == DeletedStatus && (tenantId == null || x.TenantId == tenantId))
            .OrderBy(x => x.Id).Take(ConnectionBatchSize)
            .Select(x => new { x.TenantId, x.Id }).ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var connection in deleted)
            if (await PurgeAsync(db, connection.TenantId, connection.Id, now, cancellationToken)) removed++;
        return removed;
    }

    // One deleted connection; true when its row went too. A connection that is not DELETED is left untouched.
    public static async Task<bool> PurgeAsync(AppDbContext db, Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (!await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status == DeletedStatus, cancellationToken)) return false;
        var importFiles = await db.ImportSessions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.SourceAssetId != null)
            .Select(x => x.SourceAssetId!.Value).ToListAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            DELETE FROM integration.platform_credentials WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.platform_capabilities WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.connection_sync_policies WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.webhook_subscriptions WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.sync_cursors WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.connection_backoffs WHERE "ConnectionId"={{connectionId}};
            DELETE FROM integration.external_effect_records WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.price_stock_send_log WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.marketplace_question_sync_states WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.marketplace_finance_sync_states WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.reconciliation_differences d USING integration.reconciliation_runs r WHERE d."TenantId"={{tenantId}} AND d."RunId"=r."Id" AND r."TenantId"={{tenantId}} AND r."ConnectionId"={{connectionId}};
            DELETE FROM integration.reconciliation_runs WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.reference_items i USING integration.reference_snapshots s WHERE i."TenantId"={{tenantId}} AND i."SnapshotId"=s."Id" AND s."TenantId"={{tenantId}} AND s."ConnectionId"={{connectionId}};
            DELETE FROM integration.reference_snapshots WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.job_attempts a USING integration.jobs j WHERE a."TenantId"={{tenantId}} AND a."JobId"=j."Id" AND j."TenantId"={{tenantId}} AND j."ConnectionId"={{connectionId}} AND j."Status"<>'LEASED';
            DELETE FROM integration.jobs WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}} AND "Status"<>'LEASED';
            DELETE FROM inventory.connection_location_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM inventory.connection_inventory_policies WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM inventory.channel_price_history h USING inventory.channel_offers o WHERE h."TenantId"={{tenantId}} AND h."OfferId"=o."Id" AND o."TenantId"={{tenantId}} AND o."ConnectionId"={{connectionId}};
            DELETE FROM inventory.channel_offers WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM inventory.channel_inventory_observations WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM sales.cargo_provider_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.attribute_value_mapping_templates WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.attribute_mapping_templates WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.mapping_template_dismissals WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.connection_attribute_defaults WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.category_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.brand_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.attribute_value_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.attribute_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.channel_listing_profiles WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.marketplace_listing_states WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.marketplace_variant_links WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.marketplace_product_links WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.external_identifier_aliases WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.product_change_entries WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.commission_tariffs WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.promotion_offer_files WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.ad_report_files WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.import_decisions d USING catalog.import_match_candidates m, catalog.import_sessions s WHERE d."TenantId"={{tenantId}} AND d."CandidateId"=m."Id" AND m."SessionId"=s."Id" AND s."TenantId"={{tenantId}} AND s."ConnectionId"={{connectionId}};
            DELETE FROM catalog.import_match_candidates m USING catalog.import_sessions s WHERE m."TenantId"={{tenantId}} AND m."SessionId"=s."Id" AND s."TenantId"={{tenantId}} AND s."ConnectionId"={{connectionId}};
            DELETE FROM catalog.field_provenance f USING catalog.import_sessions s WHERE f."TenantId"={{tenantId}} AND f."SessionId"=s."Id" AND s."TenantId"={{tenantId}} AND s."ConnectionId"={{connectionId}};
            DELETE FROM catalog.import_sessions WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.product_import_staging_records WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.product_import_sessions WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.catalog_structure_runs WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM billing.invoice_policies p WHERE p."TenantId"={{tenantId}} AND p."ProviderConnectionId"={{connectionId}}
              AND NOT EXISTS (SELECT 1 FROM billing.invoices i WHERE i."TenantId"={{tenantId}} AND i."InvoicePolicyId"=p."Id");
            """, cancellationToken);
        await FileAssetReferences.MarkForPurgeAsync(db, tenantId, importFiles, now, cancellationToken);
        var rowRemoved = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            DELETE FROM integration.platform_connections c
            WHERE c."TenantId"={{tenantId}} AND c."Id"={{connectionId}} AND c."Status"={{DeletedStatus}}
              AND NOT EXISTS (SELECT 1 FROM sales.orders x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM sales.shipment_packages x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM sales.shipment_documents x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM sales.return_claims x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM sales.marketplace_invoice_reference_history x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM sales.marketplace_questions x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM sales.marketplace_finance_transactions x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM sales.marketplace_cargo_invoice_items x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM sales.marketplace_payout_orders x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM billing.invoices x WHERE x."TenantId"={{tenantId}} AND x."ProviderConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM billing.invoice_policies x WHERE x."TenantId"={{tenantId}} AND x."ProviderConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM billing.marketplace_deliveries x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM billing.marketplace_delivery_states x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM billing.manual_order_invoice_documents x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM billing.manual_order_invoice_delivery_states x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
              AND NOT EXISTS (SELECT 1 FROM integration.jobs x WHERE x."TenantId"={{tenantId}} AND x."ConnectionId"=c."Id")
            """, cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return rowRemoved > 0;
    }
}
