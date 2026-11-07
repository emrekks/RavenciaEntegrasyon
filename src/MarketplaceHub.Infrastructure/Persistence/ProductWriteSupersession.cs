using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A product write (update, publish, archive) that ended blocked, in manual review or dead is closed once a later write of
// the same type for the same product on the same connection succeeded (live 7 Nov 2026: a Trendyol PRODUCT_UPDATE blocked
// on PRODUCT_CONTENT_GROUP_MISMATCH on 6 Nov stayed "Hata" although the next update of that product went through six hours
// later). The later write sent the product's current card again, so the old failure no longer stands for anything. It
// becomes CANCELLED with the code SUPERSEDED_BY_LATER_WRITE and a summary naming the later job and its time.
// Only the three product write types qualify: never an invoice job, never a price or stock write. The pass is idempotent:
// a closed job is not BLOCKED, MANUAL_REVIEW or DEAD any more. It runs after PublicationJobSupersession, which closes a
// blocked or reviewed publish of the same listing profile with its own code first.
public static class ProductWriteSupersession
{
    public const string Code = "SUPERSEDED_BY_LATER_WRITE";

    internal static readonly string[] JobTypes = [MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.ProductCreate, MarketplaceJobTypes.ProductArchive];

    private static readonly TimeZoneInfo Istanbul = DashboardReadService.ResolveTimezone("Europe/Istanbul");

    public static bool IsClosableFailure(JobStatus status) => status is JobStatus.Blocked or JobStatus.ManualReview or JobStatus.Dead;

    public static bool Supersedes(
        (Guid ConnectionId, string JobType, Guid ProductId, JobStatus Status, DateTimeOffset CreatedAt) failed,
        (Guid ConnectionId, string JobType, Guid ProductId, JobStatus Status, DateTimeOffset CreatedAt) later) =>
        IsClosableFailure(failed.Status)
        && later.Status == JobStatus.Succeeded
        && failed.ConnectionId == later.ConnectionId
        && failed.ProductId != Guid.Empty && failed.ProductId == later.ProductId
        && JobTypes.Contains(failed.JobType, StringComparer.Ordinal)
        && string.Equals(failed.JobType, later.JobType, StringComparison.Ordinal)
        && later.CreatedAt > failed.CreatedAt;

    // The product a write job's payload names (all three job types carry "ProductId" at the top).
    public static Guid ProductIdOf(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ProductId", out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var productId) ? productId : Guid.Empty;
        }
        catch (JsonException) { return Guid.Empty; }
    }

    public static string Summary(Guid laterJobId, DateTimeOffset laterCreatedAt, string? previousCode)
    {
        var local = TimeZoneInfo.ConvertTime(laterCreatedAt, Istanbul);
        return $"Aynı ürünün bu bağlantıdaki sonraki yazma işi ({laterJobId:D}, {local.ToString("d MMMM yyyy HH:mm", CultureInfo.GetCultureInfo("tr-TR"))}) başarılı oldu; bu eski hata kaydı kapatıldı (önceki hata: {previousCode ?? "—"}).";
    }

    // Closes the superseded failures; returns how many. Each pass looks at a bounded number of failures.
    internal static async Task<int> CloseAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var failed = (await db.IntegrationJobs.AsNoTracking()
                .Where(x => (x.Status == JobStatus.Blocked || x.Status == JobStatus.ManualReview || x.Status == JobStatus.Dead)
                    && x.ConnectionId != null && JobTypes.Contains(x.JobType))
                .OrderBy(x => x.CreatedAt)
                .Select(x => new { x.Id, x.TenantId, ConnectionId = x.ConnectionId!.Value, x.JobType, x.Status, x.CreatedAt, x.LastErrorCode, x.PayloadJson })
                .Take(500)
                .ToListAsync(cancellationToken))
            .Select(x => new { x.Id, x.TenantId, x.ConnectionId, x.JobType, x.Status, x.CreatedAt, x.LastErrorCode, ProductId = ProductIdOf(x.PayloadJson) })
            .Where(x => x.ProductId != Guid.Empty)
            .ToList();
        var closed = 0;
        foreach (var product in failed.GroupBy(x => (x.TenantId, x.ConnectionId, x.JobType, x.ProductId)))
        {
            var productText = product.Key.ProductId.ToString("D");
            var earliest = product.Min(x => x.CreatedAt);
            // The payload names the product; the text match narrows the rows, the parsed id decides.
            var latest = (await db.IntegrationJobs.AsNoTracking()
                    .Where(x => x.TenantId == product.Key.TenantId && x.ConnectionId == product.Key.ConnectionId && x.JobType == product.Key.JobType
                        && x.Status == JobStatus.Succeeded && x.CreatedAt > earliest && x.PayloadJson.Contains(productText))
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => new { x.Id, x.CreatedAt, x.PayloadJson })
                    .Take(20)
                    .ToListAsync(cancellationToken))
                .FirstOrDefault(x => ProductIdOf(x.PayloadJson) == product.Key.ProductId);
            if (latest is null) continue;
            foreach (var job in product.Where(x => Supersedes(
                         (x.ConnectionId, x.JobType, x.ProductId, x.Status, x.CreatedAt),
                         (product.Key.ConnectionId, product.Key.JobType, product.Key.ProductId, JobStatus.Succeeded, latest.CreatedAt))))
            {
                var summary = Summary(latest.Id, latest.CreatedAt, job.LastErrorCode);
                closed += await db.IntegrationJobs
                    .Where(x => x.Id == job.Id && x.TenantId == job.TenantId && x.Status == job.Status)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, JobStatus.Cancelled)
                        .SetProperty(x => x.LastErrorCode, Code)
                        .SetProperty(x => x.LastErrorSummary, summary)
                        .SetProperty(x => x.CompletedAt, x => x.CompletedAt ?? now)
                        .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
            }
        }
        return closed;
    }
}
