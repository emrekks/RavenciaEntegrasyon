using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Files;

// Removes from disk the stored files a data reset or connection deletion left without a row
// (OperationalDataMaintenanceService marks them PURGE_PENDING). The file_assets row stays as DELETED, a
// record that the file existed and when it went; remote image links (no file) only change state.
public static class PurgedFileRetention
{
    public const string DeletedStatus = "DELETED";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    // Short grace so a reset still being committed or read is not raced.
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);
    private const int BatchSize = 200;

    public static async Task<int> SweepAsync(AppDbContext db, IPrivateFileStorage storage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - Grace;
        var assets = await db.FileAssets
            .Where(x => x.Status == OperationalDataMaintenanceService.PurgePendingStatus && x.ArchivedAt != null && x.ArchivedAt <= cutoff)
            .OrderBy(x => x.ArchivedAt).Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var asset in assets)
        {
            if (HasFile(asset.Classification, asset.MimeType))
            {
                try { await storage.DeleteAsync(asset.TenantId, asset.RelativePath, cancellationToken); }
                // A path outside the tenant's folder is never deleted; the row is closed so it is not retried.
                catch (UnauthorizedAccessException) { }
            }
            asset.Status = DeletedStatus;
        }
        await db.SaveChangesAsync(cancellationToken);
        return assets.Count;
    }

    private static bool HasFile(string classification, string mimeType) =>
        !string.Equals(classification, "PRODUCT_MEDIA_URL", StringComparison.Ordinal) && !string.Equals(mimeType, "image/remote", StringComparison.Ordinal);
}
