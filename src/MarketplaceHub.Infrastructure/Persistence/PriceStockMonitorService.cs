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
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.Status, x.SettingsJson, x.Environment })
            .ToListAsync(cancellationToken);
        var ids = connections.Select(x => x.Id).ToArray();
        var policies = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && (x.ResourceType == MarketplaceExternalWritePolicies.Price || x.ResourceType == MarketplaceExternalWritePolicies.Stock || x.ResourceType == "ORDERS"))
            .Select(x => new { x.ConnectionId, x.ResourceType, x.Enabled, x.IntervalSeconds, x.IdleBackoffEnabled, x.IdleIntervalSeconds })
            .ToListAsync(cancellationToken);
        // Webhook first: whether each connection's new orders come by notification or, its notifications being unhealthy
        // or absent, by the scheduled read, judged by the scheduler's own rule.
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && x.Status == "ACTIVE" && x.ExternalSubscriptionId != null)
            .GroupBy(x => x.ConnectionId)
            .Select(group => new { ConnectionId = group.Key, LastReceivedAt = group.Max(x => x.LastReceivedAt) })
            .ToDictionaryAsync(x => x.ConnectionId, x => x.LastReceivedAt, cancellationToken);
        var webhookHealthy = new HashSet<Guid>();
        foreach (var connectionId in subscriptions.Keys)
            if (await ScheduledJobProducer.WebhookHealthyAsync(db, tenantId, connectionId, now, cancellationToken)) webhookHealthy.Add(connectionId);
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
        var failed = await FailedPushes(db, tenantId, ids, since)
            .Select(x => new { ConnectionId = x.ConnectionId!.Value, x.Id, x.Status, x.LastErrorCode, x.LastErrorSummary, x.CompletedAt })
            .ToListAsync(cancellationToken);
        var issuePrefix = PriceInventoryOfferIssues.KeyPrefix;
        var offerIssues = await PriceInventoryOfferIssues.Open(db, tenantId, issuePrefix)
            .Select(x => new { x.DedupeKey, x.Code, x.Summary, x.LastSeenAt })
            .ToListAsync(cancellationToken);
        var linkHealth = await BrokenLinks(db, tenantId, ids)
            .GroupBy(x => new { x.ConnectionId, x.HealthStatus })
            .Select(group => new { group.Key.ConnectionId, group.Key.HealthStatus, Count = group.Count() })
            .ToListAsync(cancellationToken);
        // Which panel products those broken links belong to (read only when there are any).
        var lostLinks = linkHealth.Count == 0 ? [] : await (
                from link in db.MarketplaceVariantLinks.AsNoTracking()
                join variant in db.ProductVariants.AsNoTracking() on link.VariantId equals variant.Id
                join product in EffectiveCatalog.Products(db) on variant.ProductId equals product.Id
                where link.TenantId == tenantId && variant.TenantId == tenantId && product.TenantId == tenantId && ids.Contains(link.ConnectionId)
                    && (link.HealthStatus == MarketplaceVariantLinkHealth.NotFound || link.HealthStatus == MarketplaceVariantLinkHealth.Rejected)
                select new LostLink(link.ConnectionId, product.Id, product.Title, link.HealthStatus!, variant.Sku, link.HealthChangedAt))
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
            var healthy = webhookHealthy.Contains(connection.Id);
            var orderRead = policies.FirstOrDefault(x => x.ConnectionId == connection.Id && x.ResourceType == "ORDERS" && x.Enabled);
            int? orderReadInterval = orderRead is null ? null : SyncCadencePolicy.EffectiveSeconds(connection.PlatformCode, "ORDERS", connection.Environment,
                Math.Clamp(orderRead.IntervalSeconds, 30, ScheduledOrderPollingCadencePolicy.MaximumIntervalSeconds), orderRead.IdleBackoffEnabled, orderRead.IdleIntervalSeconds, healthy, null, now);
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
                latest is null ? null : new PriceStockPushFailureView(latest.Id, latest.LastErrorCode, JobErrorMessages.Display(latest.LastErrorCode, latest.LastErrorSummary), latest.Status.ToString(), latest.CompletedAt),
                issues.Take(10).Select(x => new PriceStockOfferIssueView(x.Code, x.Summary, x.LastSeenAt)).ToList(),
                WebhookPollingPolicy.OrderSource(subscriptions.ContainsKey(connection.Id), healthy),
                subscriptions.GetValueOrDefault(connection.Id),
                orderReadInterval,
                LostListings(lostLinks.Where(x => x.ConnectionId == connection.Id)));
        }).ToList();
        return new PriceStockMonitorView(now, rows);
    }

    // The automatic sends that failed (blocked or dead since the given moment) or wait for a review: the monitor's
    // FailedPushes7d and ManualReviewPushes and the failed-pushes list read the same rows.
    private static IQueryable<IntegrationJob> FailedPushes(AppDbContext db, Guid tenantId, Guid[] ids, DateTimeOffset since) =>
        db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId != null && ids.Contains(x.ConnectionId.Value) && x.JobType == MarketplaceJobTypes.PriceInventorySync
                && (FailedStatuses.Contains(x.Status) && x.CompletedAt >= since || x.Status == JobStatus.ManualReview));

    // The links the marketplace no longer holds or refused: the monitor's NotFoundLinks/RejectedLinks and the lost-links list.
    private static IQueryable<MarketplaceVariantLink> BrokenLinks(AppDbContext db, Guid tenantId, Guid[] ids) =>
        db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && (x.HealthStatus == MarketplaceVariantLinkHealth.NotFound || x.HealthStatus == MarketplaceVariantLinkHealth.Rejected));

    private static ServiceResult<T> ConnectionNotFound<T>() => ServiceResult<T>.Fail("CONNECTION_NOT_FOUND", "Pazaryeri bağlantısı bulunamadı.", 404);

    // A connection the monitor shows: the tenant's, not deleted, a marketplace.
    private Task<bool> MonitoredAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED" && MarketplacePlatforms.Contains(x.PlatformCode), cancellationToken);

    private sealed record VariantFacts(Guid VariantId, Guid ProductId, string Sku, string? Barcode, string OptionSignature, decimal? ListPrice, decimal? SalePrice, string Title);

    // The variants and their cards (as the catalog shows them), in two queries for the whole set.
    private async Task<Dictionary<Guid, VariantFacts>> VariantFactsAsync(Guid tenantId, Guid[] variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Length == 0) return [];
        return await (from variant in db.ProductVariants.AsNoTracking()
                      join product in EffectiveCatalog.Products(db) on variant.ProductId equals product.Id
                      where variant.TenantId == tenantId && product.TenantId == tenantId && variantIds.Contains(variant.Id)
                      select new VariantFacts(variant.Id, product.Id, variant.Sku, variant.Barcode, variant.OptionSignature, variant.DefaultListPrice, variant.DefaultSalePrice, product.Title))
            .ToDictionaryAsync(x => x.VariantId, cancellationToken);
    }

    internal static string? VariantLabel(string? signature)
    {
        var values = VariantOptionSignature.Parse(signature).Select(option => option.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        return values.Count == 0 ? null : string.Join(" / ", values);
    }

    public async Task<ServiceResult<PriceStockInvalidOffersView>> InvalidOffersAsync(Guid tenantId, Guid connectionId, string? search, string? reason, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (!await MonitoredAsync(tenantId, connectionId, cancellationToken)) return ConnectionNotFound<PriceStockInvalidOffersView>();
        var issues = await PriceInventoryOfferIssues.Open(db, tenantId, PriceInventoryOfferIssues.ConnectionPrefix(connectionId))
            .Select(x => new { x.DedupeKey, x.Code, x.Summary, x.LastSeenAt })
            .ToListAsync(cancellationToken);
        var variantIds = issues.Select(x => PriceInventoryOfferIssues.VariantOf(x.DedupeKey)).OfType<Guid>().Distinct().ToArray();
        var variants = await VariantFactsAsync(tenantId, variantIds, cancellationToken);
        var offers = variantIds.Length == 0 ? [] : await db.ChannelOffers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId))
            .Select(x => new { x.Id, x.VariantId, x.ListPrice, x.SalePrice, x.Currency })
            .ToListAsync(cancellationToken);
        var offerByVariant = offers.GroupBy(x => x.VariantId).ToDictionary(group => group.Key, group => group.First());
        var rows = issues.Select(issue =>
        {
            var variantId = PriceInventoryOfferIssues.VariantOf(issue.DedupeKey) ?? Guid.Empty;
            variants.TryGetValue(variantId, out var variant);
            offerByVariant.TryGetValue(variantId, out var offer);
            return new PriceStockInvalidOfferView(offer?.Id, variantId, variant?.ProductId, variant?.Sku, variant?.Barcode, variant?.Title, VariantLabel(variant?.OptionSignature),
                issue.Code, issue.Summary, variant?.ListPrice, variant?.SalePrice, offer?.ListPrice, offer?.SalePrice, offer?.Currency, issue.LastSeenAt);
        }).ToList();
        var reasons = rows.GroupBy(x => x.ReasonCode, StringComparer.Ordinal).Select(group => new PriceStockReasonCountView(group.Key, group.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Code, StringComparer.Ordinal).ToList();
        var term = search?.Trim();
        var wantedReason = reason?.Trim();
        var filtered = rows
            .Where(x => string.IsNullOrEmpty(wantedReason) || string.Equals(x.ReasonCode, wantedReason, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrEmpty(term) || Contains(x.Sku, term) || Contains(x.Barcode, term) || Contains(x.ProductTitle, term))
            .OrderByDescending(x => x.LastSeenAt).ThenBy(x => x.Sku, StringComparer.Ordinal).ThenBy(x => x.VariantId)
            .ToList();
        return ServiceResult<PriceStockInvalidOffersView>.Ok(new(filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, rows.Count, filtered.Count, reasons, PriceStockOfferIssueCodes.All));

        static bool Contains(string? value, string term) => value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    // At most one list page's worth of offers a request (the window marks the rows it shows).
    public const int NoProblemLimit = 500;

    // "Sorun Yok" (owner, 24 Nov 2026): the open offer issues of the named variants are resolved with the mark and the
    // values the send judged (OfferIssueNoProblem), one audit record each. Panel records only: no offer, price or job is
    // touched and nothing is sent. A variant with no open issue (closed already, or never listed) counts as closed.
    public async Task<ServiceResult<PriceStockOfferNoProblemView>> MarkOffersNoProblemAsync(Guid tenantId, Guid connectionId, IReadOnlyList<Guid> variantIds, Guid actorUserId, string correlationId, CancellationToken cancellationToken)
    {
        var ids = (variantIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return ServiceResult<PriceStockOfferNoProblemView>.Fail("VARIANTS_REQUIRED", "İşaretlenecek en az bir teklif seçin.", 422);
        if (ids.Length > NoProblemLimit) return ServiceResult<PriceStockOfferNoProblemView>.Fail("TOO_MANY_VARIANTS", $"Bir seferde en çok {NoProblemLimit} teklif işaretlenebilir.", 422);
        if (!await MonitoredAsync(tenantId, connectionId, cancellationToken)) return ConnectionNotFound<PriceStockOfferNoProblemView>();
        var keys = ids.Select(id => PriceInventoryOfferIssues.Key(connectionId, id)).ToArray();
        var issues = await db.OperationalIssues
            .Where(x => x.TenantId == tenantId && keys.Contains(x.DedupeKey) && x.Status != IssueStatus.Resolved)
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var issue in issues)
        {
            var fingerprint = PriceInventoryOfferIssues.VariantOf(issue.DedupeKey) is { } variantId
                ? (await OfferIssueNoProblem.CurrentAsync(db, tenantId, connectionId, variantId, issue.Code, cancellationToken)).Current
                : OfferIssueNoProblem.CurrentShape + OfferIssueNoProblem.Fingerprint(issue.Code, null, null, null);
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
            issue.Summary += OfferIssueNoProblem.Mark(fingerprint);
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId,
                ActorUserId = actorUserId,
                Action = OfferIssueNoProblem.AuditAction,
                TargetType = nameof(OperationalIssue),
                TargetId = issue.Id.ToString("D"),
                Reason = $"Gönderilemeyen teklif 'Sorun Yok' olarak işaretlendi ({issue.Code}); pazaryerine bir şey gönderilmedi.",
                CorrelationId = correlationId,
                CreatedAt = now
            });
        }
        if (issues.Count > 0) await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<PriceStockOfferNoProblemView>.Ok(new(issues.Count, ids.Length - issues.Count));
    }

    public async Task<ServiceResult<PriceStockFailedPushesView>> FailedPushesAsync(Guid tenantId, Guid connectionId, string? kind, int page, int pageSize, CancellationToken cancellationToken)
    {
        var manualReview = (kind?.Trim().ToLowerInvariant()) switch
        {
            null or "" or "failed" => false,
            "manual-review" => true,
            _ => throw new ArgumentException("kind failed ya da manual-review olmalıdır.")
        };
        if (!await MonitoredAsync(tenantId, connectionId, cancellationToken)) return ConnectionNotFound<PriceStockFailedPushesView>();
        var since = timeProvider.GetUtcNow().AddDays(-7).ToUniversalTime();
        var query = FailedPushes(db, tenantId, [connectionId], since).Where(x => manualReview ? x.Status == JobStatus.ManualReview : x.Status != JobStatus.ManualReview);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.LastErrorCode, x.LastErrorSummary, x.Status, x.CompletedAt })
            .ToListAsync(cancellationToken);
        return ServiceResult<PriceStockFailedPushesView>.Ok(new(
            items.Select(x => new PriceStockPushFailureView(x.Id, x.LastErrorCode, JobErrorMessages.Display(x.LastErrorCode, x.LastErrorSummary), x.Status.ToString(), x.CompletedAt)).ToList(),
            page, pageSize, total));
    }

    public async Task<ServiceResult<PriceStockLostLinksView>> LostLinksAsync(Guid tenantId, Guid connectionId, string? health, int page, int pageSize, CancellationToken cancellationToken)
    {
        var wanted = health?.Trim().ToUpperInvariant() switch
        {
            null or "" => null,
            MarketplaceVariantLinkHealth.NotFound => MarketplaceVariantLinkHealth.NotFound,
            MarketplaceVariantLinkHealth.Rejected => MarketplaceVariantLinkHealth.Rejected,
            _ => throw new ArgumentException("health NOT_FOUND ya da REJECTED olmalıdır.")
        };
        if (!await MonitoredAsync(tenantId, connectionId, cancellationToken)) return ConnectionNotFound<PriceStockLostLinksView>();
        var query = BrokenLinks(db, tenantId, [connectionId]).Where(x => wanted == null || x.HealthStatus == wanted);
        var total = await query.CountAsync(cancellationToken);
        var links = await query.OrderBy(x => x.HealthChangedAt).ThenBy(x => x.VariantId)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.VariantId, x.HealthStatus, x.HealthChangedAt })
            .ToListAsync(cancellationToken);
        var variants = await VariantFactsAsync(tenantId, links.Select(x => x.VariantId).Distinct().ToArray(), cancellationToken);
        return ServiceResult<PriceStockLostLinksView>.Ok(new(links.Select(link =>
        {
            variants.TryGetValue(link.VariantId, out var variant);
            return new PriceStockLostLinkView(link.VariantId, variant?.ProductId, variant?.Sku, variant?.Barcode, variant?.Title, VariantLabel(variant?.OptionSignature), link.HealthStatus!, link.HealthChangedAt);
        }).ToList(), page, pageSize, total));
    }

    internal const int LostListingProductLimit = 20;
    private const int LostListingSkuLimit = 50;

    internal sealed record LostLink(Guid ConnectionId, Guid ProductId, string Title, string HealthStatus, string Sku, DateTimeOffset? HealthChangedAt);

    // One entry per product and health, the product with the most broken variants first; its stock codes in order.
    internal static IReadOnlyList<PriceStockLostListingView> LostListings(IEnumerable<LostLink> links) => links
        .GroupBy(x => (x.ProductId, x.HealthStatus))
        .Select(group => new PriceStockLostListingView(
            group.Key.ProductId,
            group.First().Title,
            group.Key.HealthStatus,
            group.Count(),
            group.Select(x => x.Sku).Order(StringComparer.Ordinal).Take(LostListingSkuLimit).ToList(),
            group.Min(x => x.HealthChangedAt)))
        .OrderByDescending(x => x.Variants).ThenBy(x => x.Title, StringComparer.Ordinal)
        .Take(LostListingProductLimit)
        .ToList();

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
        // Only the automatic pipeline stops: its dispatches, reconcile scans and automatic pushes not yet sent. A send
        // started from the panel keeps its own gate (checked again when it goes), and a batch already submitted is polled
        // to its end so its outcome and send log rows are recorded. A job already sending (leased) cannot be taken back;
        // its submit checks the policies first and stops there.
        var queued = await db.IntegrationJobs
            .Where(x => x.TenantId == tenantId && PipelineJobTypes.Contains(x.JobType) && (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled))
            .ToListAsync(cancellationToken);
        var jobs = queued.Where(job => PriceStockEmergencyStopPolicy.Cancels(job.JobType, job.PayloadJson)).ToList();
        foreach (var job in jobs)
            Jobs.JobStateMachine.Cancel(db, job, now, "AUTOMATIC_WRITES_STOPPED", "Tüm otomatik yazmalar acil durdurmayla kapatıldı; iş gönderilmeden iptal edildi.", Jobs.JobEvent.None);
        // K2: one "jobs" event for the whole stop (it may close thousands of waiting jobs), not one per job.
        Jobs.JobStateMachine.PublishBulk(db, tenantId, now, JobStatus.Cancelled, jobs.Count);
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

// Which queued price/stock jobs the emergency stop cancels: the automatic ones that have not reached the marketplace.
internal static class PriceStockEmergencyStopPolicy
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool Cancels(string jobType, string payloadJson)
    {
        // A dispatch and a reconcile scan only ever start automatic sends.
        if (jobType is MarketplaceJobTypes.StockProjectionDispatch or MarketplaceJobTypes.StockReconciliation) return true;
        if (jobType != MarketplaceJobTypes.PriceInventorySync) return false;
        PriceInventoryJobPayload? payload;
        try { payload = System.Text.Json.JsonSerializer.Deserialize<PriceInventoryJobPayload>(payloadJson, JsonOptions); }
        catch (System.Text.Json.JsonException) { return false; }
        // A job whose payload cannot be read is left to its own checks rather than guessed at.
        return payload is { Automatic: true } && string.Equals(payload.Phase?.Trim(), "SUBMIT", StringComparison.OrdinalIgnoreCase);
    }
}
