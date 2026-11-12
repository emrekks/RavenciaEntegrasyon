using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Files;

// Removes from disk the stored files a delete, a data reset or the orphan sweep left without an owner row (Status
// PURGE_PENDING), then the file_assets row itself once no row points at it (owner, 12 Nov 2026: a row whose file is
// gone keeps nothing worth having); remote image links (no file) only lose their row. Before a file goes, the owner
// rows are checked again (FileAssetReferences.NotReferenced): an upload that reused the file meanwhile takes it back.
// A row an archived picture row still points at stays as DELETED. The files a Paneli Tamamen Sıfırla left
// (PanelFactoryResetService.FileDropStatus) leave no record either.
public static class PurgedFileRetention
{
    public const string DeletedStatus = "DELETED";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    // Short grace so a reset still being committed or read is not raced.
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);
    private const int BatchSize = 200;
    private const int RowBatchSize = 5000;

    public static async Task<int> SweepAsync(AppDbContext db, IPrivateFileStorage storage, DateTimeOffset now, CancellationToken cancellationToken, ProductMediaRenditions? renditions = null)
    {
        var cutoff = now - Grace;
        var assets = await db.FileAssets
            .Where(x => (x.Status == OperationalDataMaintenanceService.PurgePendingStatus || x.Status == PanelFactoryResetService.FileDropStatus) && x.ArchivedAt != null && x.ArchivedAt <= cutoff)
            .OrderBy(x => x.ArchivedAt).Take(BatchSize).ToListAsync(cancellationToken);
        var reclaimed = await ReclaimReferencedAsync(db, assets.Where(x => x.Status == OperationalDataMaintenanceService.PurgePendingStatus).Select(x => x.Id).ToArray(), cancellationToken);
        var purged = 0;
        foreach (var asset in assets)
        {
            if (reclaimed.Contains(asset.Id)) continue;
            if (HasFile(asset.Classification, asset.MimeType))
            {
                try { await storage.DeleteAsync(asset.TenantId, asset.RelativePath, cancellationToken); }
                // A path outside the tenant's folder is never deleted; the row is closed so it is not retried.
                catch (UnauthorizedAccessException) { }
                // A product photo takes its thumbnails with it.
                if (renditions is not null && asset.Classification == "PRODUCT_MEDIA") renditions.Delete(asset.TenantId, asset.Id);
            }
            if (asset.Status == PanelFactoryResetService.FileDropStatus) db.FileAssets.Remove(asset);
            else asset.Status = DeletedStatus;
            purged++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await RemoveDeletedRowsAsync(db, cancellationToken);
        return purged;
    }

    // The rows whose file is gone and that no row points at any more: the ones this sweep closed and those earlier sweeps
    // kept as DELETED.
    public static Task<int> RemoveDeletedRowsAsync(AppDbContext db, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync($"""
            DELETE FROM ops.file_assets f
            WHERE f."Id" IN (
                SELECT f."Id" FROM ops.file_assets f
                WHERE f."Status"='{DeletedStatus}' AND {FileAssetReferences.NoRowPointsAt}
                LIMIT {RowBatchSize})
            """, cancellationToken);

    // A file marked for purge that an owner row points at again (an upload reused it after it was marked) goes back to
    // ACTIVE. A product photo whose content an unarchived twin holds keeps its ArchivedAt (one active row per content).
    private static async Task<HashSet<Guid>> ReclaimReferencedAsync(AppDbContext db, Guid[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0) return [];
        var referenced = await db.Database.SqlQueryRaw<Guid>($"""
            SELECT f."Id" AS "Value" FROM ops.file_assets f
            WHERE f."Id"=ANY(@ids) AND NOT ({FileAssetReferences.NotReferenced})
            """, new NpgsqlParameter("ids", ids)).ToListAsync(cancellationToken);
        if (referenced.Count == 0) return [];
        await db.Database.ExecuteSqlRawAsync($"""
            UPDATE ops.file_assets f SET "Status"='{FileAssetReferences.ActiveStatus}',
                "ArchivedAt"=CASE WHEN f."Classification"='PRODUCT_MEDIA' AND EXISTS (
                    SELECT 1 FROM ops.file_assets t WHERE t."TenantId"=f."TenantId" AND t."Sha256"=f."Sha256"
                      AND t."Classification"='PRODUCT_MEDIA' AND t."ArchivedAt" IS NULL AND t."Id"<>f."Id") THEN f."ArchivedAt" ELSE NULL END
            WHERE f."Id"=ANY(@ids)
            """, [new NpgsqlParameter("ids", referenced.ToArray())], cancellationToken);
        foreach (var asset in db.FileAssets.Local.Where(x => referenced.Contains(x.Id)).ToList()) db.Entry(asset).State = EntityState.Detached;
        return referenced.ToHashSet();
    }

    private static bool HasFile(string classification, string mimeType) =>
        !string.Equals(classification, "PRODUCT_MEDIA_URL", StringComparison.Ordinal) && !string.Equals(mimeType, "image/remote", StringComparison.Ordinal);
}
