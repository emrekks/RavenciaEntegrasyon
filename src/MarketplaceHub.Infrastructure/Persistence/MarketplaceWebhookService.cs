using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class MarketplaceWebhookService(AppDbContext db, TokenHasher tokenHasher, IWebhookVerifier verifier, TimeProvider timeProvider) : IMarketplaceWebhookService
{
    public async Task<ServiceResult<bool>> ReceiveAsync(Guid connectionPublicId, string routeToken, ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.PublicId == connectionPublicId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA") && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken); if (connection is null) return NotFound();
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.Id && x.Status == "ACTIVE").ToListAsync(cancellationToken); var subscription = subscriptions.SingleOrDefault(x => SafeVerify(routeToken, x.RouteTokenHash));
        if (subscription is null)
        {
            await NoteRejectedAsync(connection, "WEBHOOK_ROUTE_TOKEN_MISMATCH", "adres eski ya da hatalı (abonelik yenilendiyse pazaryerindeki adresi güncelleyin)", cancellationToken);
            return NotFound();
        }
        var verified = await verifier.VerifyAsync(rawBody, headers, connection.Id, subscription.Id, cancellationToken);
        // A stale Shopify delivery is answered 200 and dropped: a refusal would only make Shopify retry the same old delivery.
        if (!verified.IsSuccess && verified.Error!.Code == MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyWebhookVerifier.StaleCode) return ServiceResult<bool>.Ok(false);
        if (!verified.IsSuccess)
        {
            await NoteRejectedAsync(connection, verified.Error!.Code, verified.Error.SafeMessage, cancellationToken);
            return ServiceResult<bool>.Fail(verified.Error!.Code, verified.Error.SafeMessage, verified.Error.HttpStatus ?? 422);
        }
        var source = $"{connection.PlatformCode}_WEBHOOK";
        var jobType = MarketplaceJobTypes.ForPlatform(connection.PlatformCode, MarketplaceJobTypes.WebhookIngest);
        var envelope = verified.Value!;
        var externalMessageId = connection.PlatformCode == "HEPSIBURADA"
            ? $"{connection.Id:N}:{envelope.ExternalMessageId}"
            : envelope.ExternalMessageId;
        var dedup = $"webhook:{connection.Id}:{envelope.ExternalMessageId}";
        var inboxExists = await db.InboxMessages.AsNoTracking().AnyAsync(x => x.TenantId == connection.TenantId && x.Source == source && x.ExternalMessageId == externalMessageId, cancellationToken);
        var jobExists = await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == connection.TenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (!inboxExists)
            db.InboxMessages.Add(new InboxMessage { Id = Guid.CreateVersion7(), TenantId = connection.TenantId, Source = source, ExternalMessageId = externalMessageId, PayloadHash = envelope.PayloadHash, ReceivedAt = now });
        if (!jobExists)
        {
            var payload = JsonSerializer.Serialize(new { connectionId = connection.Id, externalMessageId, resourceType = envelope.ResourceType, rawJson = envelope.RawJson });
            db.IntegrationJobs.Add(new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = connection.TenantId, ConnectionId = connection.Id, JobType = jobType, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), JobDedupKey = dedup, EffectIdempotencyKey = dedup, TriggerType = "webhook", AvailableAt = now, CorrelationId = correlationId, LockGroup = MarketplaceSyncExecutionLock.WebhookIngestGroup(envelope.ResourceType), Version = 1 });
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsDuplicateCandidate(exception))
        {
            db.ChangeTracker.Clear();
            var committed = await db.InboxMessages.AsNoTracking().AnyAsync(x => x.TenantId == connection.TenantId && x.Source == source && x.ExternalMessageId == externalMessageId, cancellationToken)
                && await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == connection.TenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken);
            if (!committed) throw;
        }
        await db.WebhookSubscriptions.Where(x => x.TenantId == connection.TenantId && x.Id == subscription.Id && x.Status == "ACTIVE").ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastReceivedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }
    // A refused delivery for a known connection (old address, wrong password, a body that does not verify) leaves an
    // operational issue, so a marketplace that calls and is turned away is told apart from one that never calls; the
    // log alone is lost with every restart. One row per connection and code, touched at most once a minute.
    private async Task NoteRejectedAsync(PlatformConnection connection, string code, string reason, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var key = $"webhook-rejected:{connection.Id:N}:{code}";
        var summary = $"{connection.DisplayName} bildirimi reddedildi ({code}): {reason}";
        if (summary.Length > 1000) summary = summary[..1000];
        try
        {
            var touched = await db.OperationalIssues
                .Where(x => x.TenantId == connection.TenantId && x.DedupeKey == key && x.LastSeenAt < now.AddMinutes(-1))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, IssueStatus.Open)
                    .SetProperty(x => x.Summary, summary)
                    .SetProperty(x => x.LastSeenAt, now)
                    .SetProperty(x => x.OccurrenceCount, x => x.OccurrenceCount + 1), cancellationToken);
            if (touched > 0 || await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == connection.TenantId && x.DedupeKey == key, cancellationToken)) return;
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = connection.TenantId, DedupeKey = key, Code = "WEBHOOK_DELIVERY_REJECTED", Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateCandidate(exception)) { db.ChangeTracker.Clear(); }
    }

    private static bool IsDuplicateCandidate(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    private bool SafeVerify(string token, string hash) { try { return tokenHasher.Verify(token, hash); } catch (FormatException) { return false; } }
    private static ServiceResult<bool> NotFound() => ServiceResult<bool>.Fail("WEBHOOK_ROUTE_NOT_FOUND", "Webhook route bulunamadı.", 404);
}
