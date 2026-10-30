using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// G: what the automatic price/stock pipeline of each marketplace connection is doing, and the kill switch.
public sealed class PriceStockMonitorService(AppDbContext db, IConfiguration configuration, TimeProvider timeProvider) : IPriceStockMonitorService
{
    private static readonly string[] MarketplacePlatforms = ["TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY"];
    private static readonly string[] PipelineJobTypes = [MarketplaceJobTypes.PriceInventorySync, MarketplaceJobTypes.StockProjectionDispatch, MarketplaceJobTypes.StockReconciliation];
    private static readonly JobStatus[] QueuedStatuses = [JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled];
    private static readonly JobStatus[] FailedStatuses = [JobStatus.Blocked, JobStatus.Dead];

    public async Task<PriceStockMonitorView> MonitorAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var since = now.AddDays(-7).ToUniversalTime();
        var globalWrites = configuration.GetValue<bool>("FeatureFlags:ExternalWrites");
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status != "DELETED" && MarketplacePlatforms.Contains(x.PlatformCode))
            .OrderBy(x => x.PlatformCode).ThenBy(x => x.DisplayName)
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.Status, x.SettingsJson })
            .ToListAsync(cancellationToken);
        var ids = connections.Select(x => x.Id).ToArray();
        var policies = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && (x.ResourceType == MarketplaceExternalWritePolicies.Price || x.ResourceType == MarketplaceExternalWritePolicies.Stock))
            .Select(x => new { x.ConnectionId, x.ResourceType, x.Enabled })
            .ToListAsync(cancellationToken);
        var lastSuccess = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId != null && ids.Contains(x.ConnectionId.Value) && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.Status == JobStatus.Succeeded)
            .GroupBy(x => x.ConnectionId!.Value)
            .Select(group => new { ConnectionId = group.Key, At = group.Max(x => x.CompletedAt) })
            .ToDictionaryAsync(x => x.ConnectionId, x => x.At, cancellationToken);
        var queued = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId != null && ids.Contains(x.ConnectionId.Value) && PipelineJobTypes.Contains(x.JobType) && QueuedStatuses.Contains(x.Status))
            .GroupBy(x => x.ConnectionId!.Value)
            .Select(group => new { ConnectionId = group.Key, Count = group.Count(), Waiting = group.Count(x => x.LastErrorCode == OrderFreshnessGatePolicy.WaitCode) })
            .ToDictionaryAsync(x => x.ConnectionId, cancellationToken);
        var failed = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId != null && ids.Contains(x.ConnectionId.Value) && x.JobType == MarketplaceJobTypes.PriceInventorySync
                && (FailedStatuses.Contains(x.Status) && x.CompletedAt >= since || x.Status == JobStatus.ManualReview))
            .Select(x => new { ConnectionId = x.ConnectionId!.Value, x.Id, x.Status, x.LastErrorCode, x.LastErrorSummary, x.CompletedAt })
            .ToListAsync(cancellationToken);
        var issuePrefix = PriceInventoryOfferIssues.KeyPrefix;
        var offerIssues = await db.OperationalIssues.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(issuePrefix))
            .Select(x => new { x.DedupeKey, x.Code, x.Summary, x.LastSeenAt })
            .ToListAsync(cancellationToken);
        var linkHealth = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && (x.HealthStatus == MarketplaceVariantLinkHealth.NotFound || x.HealthStatus == MarketplaceVariantLinkHealth.Rejected))
            .GroupBy(x => new { x.ConnectionId, x.HealthStatus })
            .Select(group => new { group.Key.ConnectionId, group.Key.HealthStatus, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var rows = connections.Select(connection =>
        {
            var master = globalWrites && StockProjectionOutbox.StoredExternalWritesEnabled(connection.SettingsJson);
            var price = policies.Any(x => x.ConnectionId == connection.Id && x.ResourceType == MarketplaceExternalWritePolicies.Price && x.Enabled);
            var stock = policies.Any(x => x.ConnectionId == connection.Id && x.ResourceType == MarketplaceExternalWritePolicies.Stock && x.Enabled);
            var prefix = PriceInventoryOfferIssues.ConnectionPrefix(connection.Id);
            var issues = offerIssues.Where(x => x.DedupeKey.StartsWith(prefix, StringComparison.Ordinal)).OrderByDescending(x => x.LastSeenAt).ToList();
            var failures = failed.Where(x => x.ConnectionId == connection.Id).ToList();
            var latest = failures.OrderByDescending(x => x.CompletedAt ?? DateTimeOffset.MinValue).FirstOrDefault();
            queued.TryGetValue(connection.Id, out var queue);
            return new PriceStockConnectionMonitorView(
                connection.Id,
                connection.PlatformCode,
                connection.DisplayName,
                connection.Status,
                master,
                price,
                stock,
                AutomaticPriceStockWriteGate.IsOpen(master, price, stock),
                lastSuccess.GetValueOrDefault(connection.Id),
                queue?.Count ?? 0,
                queue?.Waiting ?? 0,
                failures.Count(x => x.Status != JobStatus.ManualReview),
                failures.Count(x => x.Status == JobStatus.ManualReview),
                issues.Count,
                linkHealth.Where(x => x.ConnectionId == connection.Id && x.HealthStatus == MarketplaceVariantLinkHealth.NotFound).Sum(x => x.Count),
                linkHealth.Where(x => x.ConnectionId == connection.Id && x.HealthStatus == MarketplaceVariantLinkHealth.Rejected).Sum(x => x.Count),
                latest is null ? null : new PriceStockPushFailureView(latest.Id, latest.LastErrorCode, latest.LastErrorSummary, latest.Status.ToString(), latest.CompletedAt),
                issues.Take(10).Select(x => new PriceStockOfferIssueView(x.Code, x.Summary, x.LastSeenAt)).ToList());
        }).ToList();
        return new PriceStockMonitorView(now, rows);
    }

    public async Task<PriceStockEmergencyStopView> StopAllAutomaticWritesAsync(Guid tenantId, Guid actorUserId, string correlationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var policies = await db.ConnectionSyncPolicies
            .Where(x => x.TenantId == tenantId && x.Enabled && (x.ResourceType == MarketplaceExternalWritePolicies.Price || x.ResourceType == MarketplaceExternalWritePolicies.Stock))
            .ToListAsync(cancellationToken);
        foreach (var policy in policies)
        {
            policy.Enabled = false;
            policy.Version++;
        }
        // A job already sending (leased) cannot be taken back; its submit checks the policies first and stops there.
        var jobs = await db.IntegrationJobs
            .Where(x => x.TenantId == tenantId && PipelineJobTypes.Contains(x.JobType) && (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled))
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
            job.LastErrorCode = "AUTOMATIC_WRITES_STOPPED";
            job.LastErrorSummary = "Tüm otomatik yazmalar acil durdurmayla kapatıldı; iş gönderilmeden iptal edildi.";
            job.Version++;
        }
        var connectionsStopped = policies.Select(x => x.ConnectionId).Distinct().Count();
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = actorUserId,
            Action = "AUTOMATIC_PRICE_STOCK_WRITES_STOPPED",
            TargetType = "PlatformConnection",
            TargetId = null,
            Reason = $"Tüm otomatik fiyat-stok yazmaları durduruldu: {connectionsStopped} bağlantı, {policies.Count} akış kapatıldı, {jobs.Count} bekleyen iş iptal edildi.",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new PriceStockEmergencyStopView(connectionsStopped, policies.Count, jobs.Count);
    }
}
