using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A product publish or approval check that ended blocked or in manual review (O4 of the 2 Nov 2026 catalog review: 65 of
// 85 such jobs live) is closed once a later publish or approval check of the same listing profile (one product on one
// connection) succeeded: the later send went out and the old failure no longer stands for anything, but it stayed in the
// job queue and in the "inceleme gerekli" counts for good, because a new send only retires jobs of its own dedup key.
// It becomes CANCELLED with the code SUPERSEDED_BY_LATER_SUCCESS, the way ScheduledReadSupersession closes old reads.
// Only BLOCKED and MANUAL_REVIEW jobs of the two job types with a strictly later SUCCEEDED job of the same tenant,
// connection and profile are touched, so the pass is idempotent: a closed job is not BLOCKED or MANUAL_REVIEW any more.
public static class PublicationJobSupersession
{
    public const string Code = "SUPERSEDED_BY_LATER_SUCCESS";

    internal static readonly string[] JobTypes = [MarketplaceJobTypes.ProductCreate, MarketplaceJobTypes.ProductApprovalReconcile];

    public static bool Supersedes(
        (Guid ConnectionId, string JobType, Guid ProfileId, JobStatus Status, DateTimeOffset CreatedAt) failed,
        (Guid ConnectionId, string JobType, Guid ProfileId, JobStatus Status, DateTimeOffset CreatedAt) later) =>
        failed.Status is JobStatus.Blocked or JobStatus.ManualReview
        && later.Status == JobStatus.Succeeded
        && failed.ConnectionId == later.ConnectionId
        && failed.ProfileId != Guid.Empty && failed.ProfileId == later.ProfileId
        && JobTypes.Contains(failed.JobType, StringComparer.Ordinal) && JobTypes.Contains(later.JobType, StringComparer.Ordinal)
        && later.CreatedAt > failed.CreatedAt;

    // The listing profile a publication job's payload names (both job types carry "ProfileId" at the top).
    public static Guid ProfileIdOf(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ProfileId", out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var profileId) ? profileId : Guid.Empty;
        }
        catch (JsonException) { return Guid.Empty; }
    }

    internal static string Summary(string? previousCode) =>
        $"Aynı ürünün bu bağlantıdaki sonraki yayın veya onay işi başarılı oldu; bu eski hata kaydı kapatıldı (önceki hata: {previousCode ?? "—"}).";

    // Closes the superseded failures; returns how many. Each pass looks at a bounded number of failures.
    internal static async Task<int> CloseAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var failed = (await db.IntegrationJobs.AsNoTracking()
                .Where(x => (x.Status == JobStatus.Blocked || x.Status == JobStatus.ManualReview) && x.ConnectionId != null && JobTypes.Contains(x.JobType))
                .OrderBy(x => x.CreatedAt)
                .Select(x => new { x.Id, x.TenantId, ConnectionId = x.ConnectionId!.Value, x.JobType, x.Status, x.CreatedAt, x.LastErrorCode, x.PayloadJson })
                .Take(500)
                .ToListAsync(cancellationToken))
            .Select(x => new { x.Id, x.TenantId, x.ConnectionId, x.JobType, x.Status, x.CreatedAt, x.LastErrorCode, ProfileId = ProfileIdOf(x.PayloadJson) })
            .Where(x => x.ProfileId != Guid.Empty)
            .ToList();
        var closed = 0;
        foreach (var profile in failed.GroupBy(x => (x.TenantId, x.ConnectionId, x.ProfileId)))
        {
            var profileText = profile.Key.ProfileId.ToString("D");
            var earliest = profile.Min(x => x.CreatedAt);
            // The payload names the profile; the text match narrows the rows, the parsed id decides.
            var successes = (await db.IntegrationJobs.AsNoTracking()
                    .Where(x => x.TenantId == profile.Key.TenantId && x.ConnectionId == profile.Key.ConnectionId && JobTypes.Contains(x.JobType)
                        && x.Status == JobStatus.Succeeded && x.CreatedAt > earliest && x.PayloadJson.Contains(profileText))
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => new { x.JobType, x.CreatedAt, x.PayloadJson })
                    .Take(20)
                    .ToListAsync(cancellationToken))
                .Where(x => ProfileIdOf(x.PayloadJson) == profile.Key.ProfileId)
                .ToList();
            var latest = successes.FirstOrDefault();
            if (latest is null) continue;
            foreach (var job in profile.Where(x => Supersedes(
                         (x.ConnectionId, x.JobType, x.ProfileId, x.Status, x.CreatedAt),
                         (profile.Key.ConnectionId, latest.JobType, profile.Key.ProfileId, JobStatus.Succeeded, latest.CreatedAt))))
            {
                var summary = Summary(job.LastErrorCode);
                closed += await db.IntegrationJobs
                    .Where(x => x.Id == job.Id && x.TenantId == job.TenantId && (x.Status == JobStatus.Blocked || x.Status == JobStatus.ManualReview))
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
