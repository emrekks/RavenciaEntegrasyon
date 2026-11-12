using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MarketplaceHub.Infrastructure.Files;

// Every place that points at an ops.file_assets row, in one SQL predicate over the alias f. A file whose owner rows are
// gone is handed to the purge sweep (PURGE_PENDING), and the sweep checks the same predicate again before it deletes
// anything. A table that gains a file column must be added here (FileAssetLeftoverPostgreSqlTests checks the foreign keys).
//
//   catalog.product_media.FileAssetId                   product and variant pictures (an ARCHIVED row no longer shows it)
//   catalog.product_size_charts.FileAssetId             the card's size chart picture
//   catalog.product_size_charts.LastGeneratedAssetId    the newest generated chart, attachable before it has a media row
//   billing.invoice_documents.FileAssetId               invoice PDFs/XMLs
//   billing.manual_order_invoice_documents.FileAssetId  manually uploaded invoices
//   sales.return_evidence.FileAssetId                   return rejection evidence
//   sales.shipment_documents.FileAssetId                shipment labels
//   catalog.import_sessions.SourceAssetId               uploaded import spreadsheets (no foreign key)
//   integration.reference_snapshots.AssetId             a reference list's stored copy (no foreign key)
//   integration.jobs.PayloadJson                        an unfinished job that names the file in its payload (JSON)
public static class FileAssetReferences
{
    public const string ActiveStatus = "ACTIVE";
    // An unreferenced file younger than this is left alone: an upload is answered before its owner row is saved
    // (return evidence waits for the decision, a generated chart for "Kullan").
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);
    private const int OrphanBatchSize = 5000;

    // Only the classifications whose owners are listed above; a file of another kind is never swept as an orphan.
    public static readonly string[] SweptClassifications =
        ["PRODUCT_MEDIA", "PRODUCT_MEDIA_URL", "INVOICE_DOCUMENT", "INVOICE_DOCUMENT_MANUAL", "SHIPMENT_LABEL", "RETURN_EVIDENCE", "IMPORT_SOURCE"];

    public const string NotReferenced = """
        NOT EXISTS (SELECT 1 FROM catalog.product_media m WHERE m."FileAssetId"=f."Id" AND m."Status"<>'ARCHIVED')
        AND NOT EXISTS (SELECT 1 FROM catalog.product_size_charts c WHERE c."FileAssetId"=f."Id")
        AND NOT EXISTS (SELECT 1 FROM catalog.product_size_charts c WHERE c."LastGeneratedAssetId"=f."Id")
        AND NOT EXISTS (SELECT 1 FROM billing.invoice_documents d WHERE d."TenantId"=f."TenantId" AND d."FileAssetId"=f."Id")
        AND NOT EXISTS (SELECT 1 FROM billing.manual_order_invoice_documents d WHERE d."TenantId"=f."TenantId" AND d."FileAssetId"=f."Id")
        AND NOT EXISTS (SELECT 1 FROM sales.return_evidence e WHERE e."FileAssetId"=f."Id")
        AND NOT EXISTS (SELECT 1 FROM sales.shipment_documents s WHERE s."FileAssetId"=f."Id")
        AND NOT EXISTS (SELECT 1 FROM catalog.import_sessions i WHERE i."TenantId"=f."TenantId" AND i."SourceAssetId"=f."Id")
        AND NOT EXISTS (SELECT 1 FROM integration.reference_snapshots r WHERE r."TenantId"=f."TenantId" AND r."AssetId"=f."Id")
        AND NOT EXISTS (SELECT 1 FROM integration.jobs j WHERE j."TenantId"=f."TenantId"
            AND j."Status" IN ('PENDING','LEASED','RETRY_SCHEDULED','BLOCKED','MANUAL_REVIEW')
            AND (strpos(j."PayloadJson"::text, f."Id"::text) > 0 OR strpos(j."PayloadJson"::text, replace(f."Id"::text, '-', '')) > 0))
        """;

    // No row at all keeps a foreign key on the file (an ARCHIVED picture row included), so the row itself can go.
    public const string NoRowPointsAt = NotReferenced + """

        AND NOT EXISTS (SELECT 1 FROM catalog.product_media m WHERE m."FileAssetId"=f."Id")
        """;

    // The files of rows a delete just removed: each one nothing else points at is handed to the purge sweep.
    public static Task<int> MarkForPurgeAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> assetIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0) return Task.FromResult(0);
        return db.Database.ExecuteSqlRawAsync($"""
            UPDATE ops.file_assets f SET "Status"='{OperationalDataMaintenanceService.PurgePendingStatus}', "ArchivedAt"=@now
            WHERE f."TenantId"=@tenant AND f."Id"=ANY(@ids) AND f."Status"='{ActiveStatus}'
              AND {NotReferenced}
            """, [new NpgsqlParameter("now", now.ToUniversalTime()), new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("ids", assetIds.Distinct().ToArray())], cancellationToken);
    }

    // The safety net of the hourly sweep: a file of a known kind older than OrphanAge that nothing points at is handed
    // to the purge sweep, whatever path forgot it. The tenant filter is for tests.
    public static Task<int> MarkOrphansAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken, Guid? tenantId = null) =>
        db.Database.ExecuteSqlRawAsync($"""
            UPDATE ops.file_assets f SET "Status"='{OperationalDataMaintenanceService.PurgePendingStatus}', "ArchivedAt"=@now
            WHERE f."Id" IN (
                SELECT f."Id" FROM ops.file_assets f
                WHERE f."Status"='{ActiveStatus}' AND f."CreatedAt"<@cutoff AND f."Classification"=ANY(@classes)
                  AND (@tenant IS NULL OR f."TenantId"=@tenant)
                  AND {NotReferenced}
                LIMIT {OrphanBatchSize})
            """, [
                new NpgsqlParameter("now", now.ToUniversalTime()),
                new NpgsqlParameter("cutoff", (now - OrphanAge).ToUniversalTime()),
                new NpgsqlParameter("classes", SweptClassifications),
                new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = (object?)tenantId ?? DBNull.Value }], cancellationToken);
}
