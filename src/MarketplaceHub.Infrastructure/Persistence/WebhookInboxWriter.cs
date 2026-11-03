using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Writes a received webhook's inbox row and its ingest job with INSERT ... ON CONFLICT DO NOTHING. Trendyol sends the
// same package webhook twice about 9 ms apart: the check-then-insert let both requests through the check, and the second
// insert hit IX_inbox_messages_TenantId_Source_ExternalMessageId. It was caught (both answered 200, nothing lost), but EF
// logged an Error for it first. Here the database decides which request created the rows, and no error is raised.
internal static class WebhookInboxWriter
{
    internal sealed record Outcome(bool InboxCreated, bool JobCreated)
    {
        public bool Created => InboxCreated || JobCreated;
    }

    public static async Task<Outcome> InsertAsync(AppDbContext db, InboxMessage inbox, IntegrationJob job, CancellationToken cancellationToken)
    {
        var receivedAt = inbox.ReceivedAt.ToUniversalTime();
        var availableAt = job.AvailableAt.ToUniversalTime();
        // Both rows or neither, as the single SaveChanges before.
        var ownTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var inboxRows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO integration.inbox_messages ("Id", "TenantId", "Source", "ExternalMessageId", "PayloadHash", "ReceivedAt")
                VALUES ({inbox.Id}, {inbox.TenantId}, {inbox.Source}, {inbox.ExternalMessageId}, {inbox.PayloadHash}, {receivedAt})
                ON CONFLICT ("TenantId", "Source", "ExternalMessageId") DO NOTHING
                """, cancellationToken);
            // The columns the EF model would write for a new job; the counters and timestamps take their defaults.
            var jobRows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO integration.jobs ("Id", "TenantId", "ConnectionId", "JobType", "ResourceType", "OperationType", "TriggerType",
                    "PayloadJson", "PayloadVersion", "PayloadHash", "JobDedupKey", "EffectIdempotencyKey", "Priority", "Status",
                    "AvailableAt", "AttemptCount", "MaxAttempts", "CorrelationId", "LockGroup", "Version")
                VALUES ({job.Id}, {job.TenantId}, {job.ConnectionId}, {job.JobType}, {job.ResourceType}, {job.OperationType}, {job.TriggerType},
                    {job.PayloadJson}, {job.PayloadVersion}, {job.PayloadHash}, {job.JobDedupKey}, {job.EffectIdempotencyKey}, {job.Priority}, {StatusText(job.Status)},
                    {availableAt}, {job.AttemptCount}, {job.MaxAttempts}, {job.CorrelationId}, {job.LockGroup}, {job.Version})
                ON CONFLICT ("TenantId", "JobType", "JobDedupKey") DO NOTHING
                """, cancellationToken);
            if (ownTransaction is not null) await ownTransaction.CommitAsync(cancellationToken);
            return new(inboxRows > 0, jobRows > 0);
        }
        finally
        {
            if (ownTransaction is not null) await ownTransaction.DisposeAsync();
        }
    }

    // A new webhook job is always queued; any other status is refused rather than written in a form the model cannot read.
    private static string StatusText(JobStatus status) => status == JobStatus.Pending
        ? "PENDING"
        : throw new ArgumentOutOfRangeException(nameof(status), status, "A received webhook queues a pending job.");
}
