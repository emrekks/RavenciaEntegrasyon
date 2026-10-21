using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class ScheduledJobProducer(AppDbContext db, TimeProvider timeProvider, IConfiguration configuration) : IScheduledJobProducer
{
    // The policy passes run on their own slower timer (the worker calls this every five minutes and at
    // startup); the default upgrades only when asked, which the worker does at startup and hourly.
    public async Task MaintainPoliciesAsync(bool includeDefaults, CancellationToken cancellationToken)
    {
        await DisableProductAutomationAsync(cancellationToken);
        if (includeDefaults) await EnsureDefaultPoliciesAsync(cancellationToken);
        await DisableExternalWriteAutomationAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    public async Task<int> EnqueueDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var added = 0;
        // A connection that has just become operational gets its policies at once instead of waiting
        // for the hourly defaults pass; the check is a single EXISTS query.
        if (await ConnectionsWithoutPoliciesExistAsync(cancellationToken)) await EnsureDefaultPoliciesAsync(cancellationToken);
        added += await RetestTransientDraftsAsync(now, cancellationToken);
        // Jobs and policy timestamps of this pass are saved together below; nothing tracked by the
        // passes above may ride along.
        db.ChangeTracker.Clear();
        var policies = (await (from policy in db.ConnectionSyncPolicies.AsNoTracking()
                               join connection in db.PlatformConnections.AsNoTracking()
                                   on new { policy.TenantId, Id = policy.ConnectionId } equals new { connection.TenantId, connection.Id }
                               where policy.Enabled && policy.ResourceType != "PRODUCTS" && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                                   && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "SHOPIFY" || connection.PlatformCode == "HEPSIBURADA" || connection.PlatformCode == "N11" || connection.PlatformCode == "PAZARAMA" || connection.PlatformCode == "PTTAVM")
                               select new { Policy = policy, Connection = connection }).ToListAsync(cancellationToken))
            .Where(row => row.Connection.PlatformCode == "TRENDYOL" || row.Connection.PlatformCode == "SHOPIFY" && IsShopifyReadPolicy(row.Policy.ResourceType) || row.Connection.PlatformCode == "HEPSIBURADA" && SupportsHepsiburadaScheduledPolicy(row.Policy.ResourceType) || row.Connection.PlatformCode == "N11" && SupportsN11ScheduledPolicy(row.Policy.ResourceType) || row.Connection.PlatformCode == "PAZARAMA" && SupportsPazaramaScheduledPolicy(row.Policy.ResourceType) || row.Connection.PlatformCode == "PTTAVM" && SupportsPttAvmScheduledPolicy(row.Policy.ResourceType))
            // Keep the hot order stream ahead of lifecycle and reconciliation
            // scans. The query has no guaranteed row order, so an incidental
            // database order could otherwise enqueue a long lifecycle scan
            // first and block fresh orders behind the shared orders lane.
            .OrderBy(row => ScheduledPolicyPriority(row.Policy.ResourceType))
            .ThenBy(row => row.Connection.Id)
            .ToList();
        var bootstrapConnections = (await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.ConnectionId != null
                && x.JobDedupKey.StartsWith(MarketplaceJobTypes.ActivationBootstrapPrefix)
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled))
            .Select(x => x.ConnectionId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();
        HashSet<Guid>? webhookHealthy = null;
        var reservedExecutionGroups = new HashSet<string>(StringComparer.Ordinal);
        var backgroundOrderReservations = new HashSet<Guid>();
        var activeJobTypesByConnection = new Dictionary<Guid, List<string>>();
        var scheduledPolicyIds = new List<Guid>();

        foreach (var row in policies)
        {
            if (bootstrapConnections.Contains(row.Connection.Id)) continue;
            var definition = Definition(row.Policy.ResourceType, row.Connection.Id, row.Connection.PlatformCode);
            if (definition is null) continue;

            var interval = Math.Clamp(row.Policy.IntervalSeconds, 30, ScheduledOrderPollingCadencePolicy.MaximumIntervalSeconds);
            var isHotOrder = definition.Value.JobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync;
            var jitterSeconds = StableJitterSeconds(row.Connection.Id, row.Policy.ResourceType, row.Policy.JitterSeconds);
            // The cheap due check first: a policy queued less than its (shortest possible) interval ago is skipped
            // before any of the lookups below. Daily policies run only inside their afternoon window.
            if (!MayBeDue(row.Policy.LastScheduledAt, ScheduledOrderPollingCadencePolicy.ForEnvironment(interval, row.Connection.Environment), jitterSeconds, now, isHotOrder)) continue;
            // Questions are rare; a connection without a new question for a while is asked less often.
            if (row.Policy.ResourceType == "QUESTIONS")
            {
                var lastQuestionAt = await db.MarketplaceQuestions.AsNoTracking()
                    .Where(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id)
                    .MaxAsync(x => (DateTimeOffset?)x.CreatedAt, cancellationToken);
                interval = QuestionCadencePolicy.IntervalSeconds(interval, lastQuestionAt, now);
            }
            // Healthy order notifications make the order polls a slower safety net.
            if (WebhookPollingPolicy.StretchedIntervalSeconds(row.Connection.PlatformCode, row.Policy.ResourceType) is { } stretched
                && (webhookHealthy ??= await HealthyWebhookConnectionsAsync(now, cancellationToken)).Contains(row.Connection.Id))
                interval = Math.Max(interval, stretched);
            interval = ScheduledOrderPollingCadencePolicy.ForEnvironment(interval, row.Connection.Environment);
            // Different scheduled job types can share one provider execution
            // lane (for example order sync and order reconciliation). Checking
            // the execution group keeps the queue coalesced before it reaches
            // the worker's advisory lock. The queued job types are read once per connection: jobs this pass
            // adds are not saved yet, the reservations below cover them.
            if (!activeJobTypesByConnection.TryGetValue(row.Connection.Id, out var activeJobTypes))
            {
                activeJobTypes = await db.IntegrationJobs.AsNoTracking()
                    .Where(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id
                        && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled))
                    .Select(x => x.JobType)
                    .Distinct()
                    .ToListAsync(cancellationToken);
                activeJobTypesByConnection[row.Connection.Id] = activeJobTypes;
            }
            var executionGroup = MarketplaceSyncExecutionLock.GroupFor(definition.Value.JobType);
            var active = activeJobTypes.Any(jobType => MarketplaceSyncExecutionLock.GroupFor(jobType) == executionGroup);
            var hotOrderAlreadyQueued = activeJobTypes.Contains(MarketplaceJobTypes.OrderSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.ShopifyOrderSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.HepsiburadaOrderSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.N11OrderSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.PazaramaOrderSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.PttAvmOrderSync, StringComparer.Ordinal);
            var isOrderLifecycle = definition.Value.JobType is MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync or MarketplaceJobTypes.PttAvmOrderStatusSync;
            var lifecycleAlreadyQueued = activeJobTypes.Contains(MarketplaceJobTypes.OrderStatusSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.ShopifyOrderStatusSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.HepsiburadaOrderStatusSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.PazaramaOrderStatusSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.PttAvmOrderStatusSync, StringComparer.Ordinal);
            var activeOrderLane = activeJobTypes.Any(jobType => MarketplaceSyncExecutionLock.GroupFor(jobType) == "orders");
            var canQueueHotOrderBehindOrderLane = isHotOrder && activeOrderLane && !hotOrderAlreadyQueued;
            var canQueueLifecycleBehindOrderLane = isOrderLifecycle && activeOrderLane && !lifecycleAlreadyQueued;
            var isOrderBackground = IsOrderBackgroundJob(definition.Value.JobType);
            var backgroundOrderAlreadyQueued = activeJobTypes.Any(IsOrderBackgroundJob);
            var canQueueOrderBackgroundBehindOrderLane = isOrderBackground
                && activeOrderLane
                && !backgroundOrderAlreadyQueued
                && !backgroundOrderReservations.Contains(row.Connection.Id);
            // The jobs added during this pass are not visible to the AsNoTracking
            // queries until SaveChangesAsync. Reserve the provider lane in memory
            // as well, otherwise multiple order policies can be queued together.
            // A lifecycle scan is the exception: one pending lifecycle job may
            // wait behind the hot order stream, otherwise a continuously busy
            // stream can starve status refreshes forever.
            var reservationKey = $"{row.Policy.TenantId:N}:{row.Connection.Id:N}:{executionGroup}";
            if ((active && !canQueueHotOrderBehindOrderLane && !canQueueLifecycleBehindOrderLane && !canQueueOrderBackgroundBehindOrderLane)
                || (reservedExecutionGroups.Contains(reservationKey) && !canQueueHotOrderBehindOrderLane && !canQueueLifecycleBehindOrderLane && !canQueueOrderBackgroundBehindOrderLane)) continue;
            // The policy remembers when its job was last queued; only a policy that has not queued since the
            // column was added looks the time up in the job table.
            var latest = row.Policy.LastScheduledAt ?? await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id && x.JobType == definition.Value.JobType && x.JobDedupKey.StartsWith(definition.Value.DedupPrefix))
                .OrderByDescending(x => x.CreatedAt).Select(x => (DateTimeOffset?)x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (!isHotOrder && !IsDue(latest, interval, jitterSeconds, now)) continue;
            if (row.Policy.ResourceType == "REFERENCE_DATA"
                && await db.ReferenceSnapshots.AsNoTracking().AnyAsync(x => x.TenantId == row.Policy.TenantId
                    && x.ConnectionId == row.Connection.Id && x.ResourceType == "CATEGORIES" && x.ScopeExternalId == ""
                    && x.IsCurrent && x.FetchedAt > now.AddSeconds(-(interval + jitterSeconds)), cancellationToken)) continue;
            // A hot order read of a connection without any order yet runs at once, whatever its interval.
            if (isHotOrder && !IsDue(latest, interval, jitterSeconds, now)
                && await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id, cancellationToken)) continue;

            var bucket = now.ToUnixTimeSeconds() / interval;
            var dedup = $"{definition.Value.DedupPrefix}:{bucket}";
            if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == row.Policy.TenantId && x.JobType == definition.Value.JobType && x.JobDedupKey == dedup, cancellationToken)) continue;
            reservedExecutionGroups.Add(reservationKey);
            if (canQueueOrderBackgroundBehindOrderLane) backgroundOrderReservations.Add(row.Connection.Id);
            db.IntegrationJobs.Add(NewJob(row.Policy.TenantId, row.Connection.Id, definition.Value.JobType, dedup, definition.Value.PayloadJson, now, $"scheduler-{Guid.NewGuid():N}"));
            scheduledPolicyIds.Add(row.Policy.Id);
            added++;
        }

        // Tenants that have invoices (an EXISTS per tenant instead of a DISTINCT over every invoice).
        var dueTenants = await db.Tenants.AsNoTracking()
            .Where(tenant => db.Invoices.Any(invoice => invoice.TenantId == tenant.Id))
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);
        const int invoiceScanInterval = 300;
        var invoiceBucket = now.ToUnixTimeSeconds() / invoiceScanInterval;
        foreach (var tenantId in dueTenants)
        {
            var dedup = $"scheduled:invoice-due:{tenantId:N}:{invoiceBucket}";
            if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobType == InvoicingJobTypes.InvoiceDueScan && x.JobDedupKey == dedup, cancellationToken)) continue;
            db.IntegrationJobs.Add(NewJob(tenantId, null, InvoicingJobTypes.InvoiceDueScan, dedup, "{}", now, $"scheduler-{Guid.NewGuid():N}"));
            added++;
        }

        if (added == 0) return 0;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsExpectedDedupRace(exception))
        {
            // Another worker won the unique dedup race. Clear the pending rows; the next scheduler pass
            // observes the committed state.
            db.ChangeTracker.Clear();
            return 0;
        }
        // When each policy last queued its job is written on its own, without the policy's version check: a
        // user saving a policy meanwhile no longer throws the whole pass away, and the save does not conflict
        // with this write either.
        if (scheduledPolicyIds.Count > 0)
            await db.ConnectionSyncPolicies
                .Where(policy => scheduledPolicyIds.Contains(policy.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(policy => policy.LastScheduledAt, now), cancellationToken);
        return added;
    }

    // The cheap part of the due check, made before any lookup: the policy's own LastScheduledAt against the
    // shortest interval it can have this pass (question and webhook cadences only stretch it). A hot order
    // read is never skipped here because a connection without orders reads at once.
    internal static bool MayBeDue(DateTimeOffset? lastScheduledAt, int minimumIntervalSeconds, int jitterSeconds, DateTimeOffset now, bool isHotOrder) =>
        isHotOrder || IsDue(lastScheduledAt, minimumIntervalSeconds, jitterSeconds, now);

    // A daily (or longer) policy runs inside its Istanbul afternoon window (DailyScheduleWindowPolicy); every
    // other policy once its interval and jitter have passed since its last job.
    internal static bool IsDue(DateTimeOffset? latest, int intervalSeconds, int jitterSeconds, DateTimeOffset now) =>
        DailyScheduleWindowPolicy.Applies(intervalSeconds)
            ? DailyScheduleWindowPolicy.IsDue(now, latest, intervalSeconds, jitterSeconds)
            : latest is not { } last || last.AddSeconds(intervalSeconds + jitterSeconds) <= now;

    internal Task<bool> ConnectionsWithoutPoliciesExistAsync(CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking().AnyAsync(connection => (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
            && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "SHOPIFY" || connection.PlatformCode == "HEPSIBURADA" || connection.PlatformCode == "N11" || connection.PlatformCode == "PAZARAMA" || connection.PlatformCode == "PTTAVM")
            && !db.ConnectionSyncPolicies.Any(policy => policy.TenantId == connection.TenantId && policy.ConnectionId == connection.Id && policy.ResourceType == "ORDERS"), cancellationToken);

    internal async Task<HashSet<Guid>> HealthyWebhookConnectionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking()
            .Where(x => x.Status == "ACTIVE" && x.ExternalSubscriptionId != null)
            .Select(x => new { x.TenantId, x.ConnectionId, x.LastReceivedAt, x.VerifiedAt })
            .ToListAsync(cancellationToken);
        var healthy = new HashSet<Guid>();
        foreach (var subscription in subscriptions)
        {
            var health = await db.SyncCursors.AsNoTracking()
                .Where(x => x.TenantId == subscription.TenantId && x.ConnectionId == subscription.ConnectionId && x.ResourceType == WebhookPollingPolicy.HealthCursorResource)
                .Select(x => x.OpaqueCursor)
                .FirstOrDefaultAsync(cancellationToken);
            if (WebhookPollingPolicy.IsHealthy(subscription.LastReceivedAt, subscription.VerifiedAt, WebhookGaps(health), now,
                    await UnnotifiedOrderAsync(db, subscription.TenantId, subscription.ConnectionId, subscription.LastReceivedAt ?? subscription.VerifiedAt, now, cancellationToken)))
                healthy.Add(subscription.ConnectionId);
        }
        return healthy;
    }

    // The scheduler's notification health rule for one connection, so the panel judges a stretched poll by
    // the cadence it actually runs at.
    internal static async Task<bool> WebhookHealthyAsync(AppDbContext db, Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE" && x.ExternalSubscriptionId != null)
            .Select(x => new { x.LastReceivedAt, x.VerifiedAt })
            .ToListAsync(cancellationToken);
        if (subscriptions.Count == 0) return false;
        var health = await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == WebhookPollingPolicy.HealthCursorResource)
            .Select(x => x.OpaqueCursor)
            .FirstOrDefaultAsync(cancellationToken);
        foreach (var subscription in subscriptions)
            if (WebhookPollingPolicy.IsHealthy(subscription.LastReceivedAt, subscription.VerifiedAt, WebhookGaps(health), now,
                    await UnnotifiedOrderAsync(db, tenantId, connectionId, subscription.LastReceivedAt ?? subscription.VerifiedAt, now, cancellationToken)))
                return true;
        return false;
    }

    // Whether the panel holds an order of the connection that no notification followed (UnnotifiedOrderWindow).
    internal static Task<bool> UnnotifiedOrderAsync(AppDbContext db, Guid tenantId, Guid connectionId, DateTimeOffset? lastSign, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (lastSign is null) return Task.FromResult(false);
        var (after, before) = WebhookPollingPolicy.UnnotifiedOrderWindow(lastSign.Value, now);
        if (after >= before) return Task.FromResult(false);
        return db.Orders.AsNoTracking().AnyAsync(order => order.TenantId == tenantId && order.ConnectionId == connectionId && order.CreatedAt > after && order.CreatedAt <= before, cancellationToken);
    }

    // Missed orders are kept as {"missed":[...]}. The earlier bare list also counted orders the poll merely read
    // before their notification arrived, so it is not trusted and reads as no misses.
    internal static IReadOnlyList<DateTimeOffset> WebhookGaps(string? opaqueCursor)
    {
        if (string.IsNullOrWhiteSpace(opaqueCursor)) return [];
        try
        {
            using var document = JsonDocument.Parse(opaqueCursor);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("missed", out var missed)
                ? missed.Deserialize<DateTimeOffset[]>() ?? []
                : [];
        }
        catch (JsonException) { return []; }
    }

    internal static string WebhookGapsCursor(IEnumerable<DateTimeOffset> missed) => JsonSerializer.Serialize(new { missed });

    private async Task DisableProductAutomationAsync(CancellationToken cancellationToken)
    {
        var productPolicies = await (from policy in db.ConnectionSyncPolicies
                                     join connection in db.PlatformConnections
                                         on new { policy.TenantId, Id = policy.ConnectionId } equals new { connection.TenantId, connection.Id }
                                     where policy.ResourceType == "PRODUCTS" && policy.Enabled
                                         && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "SHOPIFY")
                                     select policy)
            .ToListAsync(cancellationToken);
        foreach (var policy in productPolicies)
        {
            policy.Enabled = false;
            policy.Version++;
        }

        var scheduledProductJobs = await db.IntegrationJobs
            .Where(x => (x.JobType == MarketplaceJobTypes.ProductSync || x.JobType == MarketplaceJobTypes.ShopifyProductSync)
                && (x.JobDedupKey.StartsWith("scheduled:products:")
                    || x.JobDedupKey.StartsWith(MarketplaceJobTypes.ActivationBootstrapPrefix) && x.JobDedupKey.EndsWith(":products"))
                && x.ConnectionId != null
                && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == x.ConnectionId
                    && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "SHOPIFY"))
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled))
            .ToListAsync(cancellationToken);
        foreach (var job in scheduledProductJobs)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = timeProvider.GetUtcNow();
            job.Version++;
        }

        if (productPolicies.Count > 0 || scheduledProductJobs.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    private async Task DisableExternalWriteAutomationAsync(CancellationToken cancellationToken)
    {
        var connections = await db.PlatformConnections
            .Where(x => x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM")
            .ToListAsync(cancellationToken);
        var blockedConnectionIds = connections
            .Where(x => !WritesEnabled(x.SettingsJson))
            .Select(x => new { x.TenantId, ConnectionId = x.Id })
            .ToList();
        if (blockedConnectionIds.Count == 0) return;

        var connectionSettingsChanged = false;
        foreach (var connection in connections.Where(x => !WritesEnabled(x.SettingsJson) && StoredWritesEnabled(x.SettingsJson)))
        {
            connection.SettingsJson = DisableStoredWrites(connection.SettingsJson);
            connection.Version++;
            connectionSettingsChanged = true;
        }

        var connectionIds = blockedConnectionIds.Select(x => x.ConnectionId).ToArray();
        // A STAGE (SIT) Hepsiburada connection runs its certification writes as manual Stage operations with
        // the write switches off: its product create/update jobs and the price/stock uploads started from
        // the panel are not cancelled here. Its automatic jobs, and every PRODUCTION connection, still are.
        var stageHepsiburadaIds = connections
            .Where(x => !WritesEnabled(x.SettingsJson) && HepsiburadaStageCertificationPolicy.IsStageHepsiburada(x))
            .Select(x => x.Id)
            .ToArray();
        var blockedHepsiburadaIds = connections
            .Where(x => x.PlatformCode == "HEPSIBURADA" && !WritesEnabled(x.SettingsJson) && !HepsiburadaStageCertificationPolicy.IsStageHepsiburada(x))
            .Select(x => x.Id)
            .ToArray();
        var policies = await db.ConnectionSyncPolicies
            .Where(x => connectionIds.Contains(x.ConnectionId) && x.Enabled)
            .ToListAsync(cancellationToken);
        foreach (var policy in policies.Where(x => x.ResourceType is "STOCK_RECONCILE_SHORT" or "STOCK_RECONCILE_MEDIUM" or "STOCK_RECONCILE_DAILY"
            || MarketplaceExternalWritePolicies.IsPolicy(x.ResourceType)))
        {
            policy.Enabled = false;
            policy.Version++;
        }

        var jobs = await db.IntegrationJobs
            .Where(x => connectionIds.Contains(x.ConnectionId!.Value)
                && (x.JobType == MarketplaceJobTypes.StockReconciliation
                    || x.JobType == MarketplaceJobTypes.PriceInventorySync && !(x.TriggerType == "user" && stageHepsiburadaIds.Contains(x.ConnectionId!.Value))
                    || x.JobType == MarketplaceJobTypes.StockProjectionDispatch
                    || blockedHepsiburadaIds.Contains(x.ConnectionId!.Value) && (x.JobType == MarketplaceJobTypes.ProductCreate || x.JobType == MarketplaceJobTypes.ProductUpdate))
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled))
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var job in jobs)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
            job.Version++;
        }

        if (connectionSettingsChanged || policies.Any(x => x.ResourceType is "STOCK_RECONCILE_SHORT" or "STOCK_RECONCILE_MEDIUM" or "STOCK_RECONCILE_DAILY"
            || MarketplaceExternalWritePolicies.IsPolicy(x.ResourceType)) || jobs.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    // A draft connection whose last test failed on a passing fault (gateway block, quota, marketplace fault, timeout)
    // is tested again every quarter hour, so it verifies itself once the marketplace recovers. One whose credentials
    // were refused is tested again with a doubling pause (1, 2, 4, 8, 16 hours) for a day while its credential stays
    // unchanged; see ConnectionRetestPolicy.
    internal async Task<int> RetestTransientDraftsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var drafts = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.Status == "DRAFT" && x.LastTestedAt != null && x.LastErrorCode != null
                && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM")
                && db.PlatformCredentials.Any(credential => credential.ConnectionId == x.Id && credential.RevokedAt == null))
            .Select(x => new
            {
                x.TenantId,
                x.Id,
                x.PlatformCode,
                x.Status,
                x.LastTestedAt,
                x.LastErrorCode,
                CredentialSavedAt = db.PlatformCredentials.Where(credential => credential.ConnectionId == x.Id && credential.RevokedAt == null).Max(credential => (DateTimeOffset?)credential.CreatedAt)
            })
            .ToListAsync(cancellationToken);
        var added = 0;
        foreach (var draft in drafts)
        {
            if (!ConnectionRetestPolicy.ShouldRetest(draft.Status, draft.LastTestedAt, draft.LastErrorCode, now, draft.CredentialSavedAt)) continue;
            var jobType = MarketplaceJobTypes.ForPlatform(draft.PlatformCode, MarketplaceJobTypes.ConnectionTest);
            var dedupPrefix = $"scheduled:connection-retest:{draft.Id:N}:";
            var pending = await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == draft.TenantId && x.ConnectionId == draft.Id
                && x.JobDedupKey.StartsWith(dedupPrefix) && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);
            if (pending) continue;
            db.IntegrationJobs.Add(NewJob(draft.TenantId, draft.Id, jobType, $"{dedupPrefix}{now.ToUnixTimeSeconds()}", "{}", now, $"scheduler-retest-{Guid.NewGuid():N}"));
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    private async Task EnsureDefaultPoliciesAsync(CancellationToken cancellationToken)
    {
        var operationalConnections = await db.PlatformConnections.AsNoTracking()
            .Where(x => (x.Status == "ACTIVE" || x.Status == "VERIFIED") && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM"))
            .Select(x => new { x.TenantId, ConnectionId = x.Id, x.PlatformCode })
            .ToListAsync(cancellationToken);
        if (operationalConnections.Count == 0) return;

        var connectionIds = operationalConnections.Select(x => x.ConnectionId).ToArray();
        var existing = await db.ConnectionSyncPolicies
            .Where(x => connectionIds.Contains(x.ConnectionId) && (x.ResourceType == "ORDERS" || x.ResourceType == "ORDER_RECOVERY" || x.ResourceType == "ORDER_LIFECYCLE" || x.ResourceType == "ORDER_CARGO_INFO" || x.ResourceType == "ORDER_RECONCILE_SHORT" || x.ResourceType == "ORDER_RECONCILE_MEDIUM" || x.ResourceType == "ORDER_RECONCILE_DAILY" || x.ResourceType == "ORDER_INVOICE_RECONCILIATION" || x.ResourceType == "RETURNS" || x.ResourceType == "RETURN_LIFECYCLE" || x.ResourceType == "RETURN_RECONCILE_SHORT" || x.ResourceType == "RETURN_RECONCILE_MEDIUM" || x.ResourceType == "RETURN_RECONCILE_DAILY" || x.ResourceType == "REFERENCE_DATA" || x.ResourceType == "QUESTIONS" || x.ResourceType == "STOCK_RECONCILE_SHORT" || x.ResourceType == "STOCK_RECONCILE_MEDIUM" || x.ResourceType == "STOCK_RECONCILE_DAILY" || x.ResourceType == MarketplaceExternalWritePolicies.Price || x.ResourceType == MarketplaceExternalWritePolicies.Stock || x.ResourceType == MarketplaceExternalWritePolicies.Shipment || x.ResourceType == MarketplaceExternalWritePolicies.Return))
            .ToListAsync(cancellationToken);
        var obsoleteProductPolicies = await (from policy in db.ConnectionSyncPolicies
                                             join connection in db.PlatformConnections
                                                 on new { policy.TenantId, Id = policy.ConnectionId } equals new { connection.TenantId, connection.Id }
                                             where connectionIds.Contains(policy.ConnectionId)
                                                 && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "SHOPIFY")
                                                 && policy.ResourceType == "PRODUCTS" && policy.Enabled
                                             select policy)
            .ToListAsync(cancellationToken);
        foreach (var policy in obsoleteProductPolicies)
        {
            policy.Enabled = false;
            policy.Version++;
        }
        foreach (var connection in operationalConnections)
        {
            var defaultsForConnection = DefaultPolicies(configuration)
                .Where(defaults => connection.PlatformCode switch
                {
                    "SHOPIFY" => SupportsShopifyPolicy(defaults.ResourceType),
                    "HEPSIBURADA" => SupportsHepsiburadaScheduledPolicy(defaults.ResourceType),
                    "N11" => SupportsN11ScheduledPolicy(defaults.ResourceType),
                    "PAZARAMA" => SupportsPazaramaScheduledPolicy(defaults.ResourceType),
                    "PTTAVM" => SupportsPttAvmScheduledPolicy(defaults.ResourceType),
                    _ => true
                })
                .Select(defaults =>
                {
                    var cadence = ScheduledOrderPollingCadencePolicy.ForPlatform(
                        connection.PlatformCode,
                        defaults.ResourceType,
                        defaults.IntervalSeconds,
                        defaults.JitterSeconds);
                    return defaults with { IntervalSeconds = cadence.IntervalSeconds, JitterSeconds = cadence.JitterSeconds };
                });
            foreach (var defaults in defaultsForConnection)
            {
                var current = existing.SingleOrDefault(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.ConnectionId && x.ResourceType == defaults.ResourceType);
                if (current is not null)
                {
                    // Upgrade only exact application defaults. A policy a user saved is never touched, even
                    // when its values equal a historical default.
                    if (ShouldUpgradeToDefault(current, connection.PlatformCode)
                        && (current.IntervalSeconds != defaults.IntervalSeconds
                            || current.OverlapSeconds != defaults.OverlapSeconds
                            || current.JitterSeconds != defaults.JitterSeconds))
                    {
                        current.IntervalSeconds = defaults.IntervalSeconds;
                        current.OverlapSeconds = defaults.OverlapSeconds;
                        current.JitterSeconds = defaults.JitterSeconds;
                        current.Version++;
                    }
                    continue;
                }
                db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = connection.TenantId,
                    ConnectionId = connection.ConnectionId,
                    ResourceType = defaults.ResourceType,
                    IntervalSeconds = defaults.IntervalSeconds,
                    OverlapSeconds = defaults.OverlapSeconds,
                    JitterSeconds = defaults.JitterSeconds,
                    Enabled = DefaultPolicyEnabled(connection.PlatformCode, defaults.ResourceType),
                    Version = 1
                });
            }
        }
        if (db.ChangeTracker.Entries<ConnectionSyncPolicy>().Any(x => x.State is EntityState.Added or EntityState.Modified))
            await db.SaveChangesAsync(cancellationToken);
    }

    internal static IReadOnlyList<PolicyDefaults> DefaultPolicies(IConfiguration configuration) =>
    [
        new("ORDERS", configuration.GetValue("MarketplaceSync:Orders:IntervalSeconds", 180), configuration.GetValue("MarketplaceSync:Orders:SafetyWindowSeconds", 600), configuration.GetValue("MarketplaceSync:Orders:JitterSeconds", 5)),
        new("ORDER_RECOVERY", configuration.GetValue("MarketplaceSync:OrderRecovery:IntervalSeconds", 900), configuration.GetValue("MarketplaceSync:OrderRecovery:SafetyWindowSeconds", 600), configuration.GetValue("MarketplaceSync:OrderRecovery:JitterSeconds", 30)),
        new("ORDER_LIFECYCLE", configuration.GetValue("MarketplaceSync:OrderLifecycle:IntervalSeconds", 180), 0, configuration.GetValue("MarketplaceSync:OrderLifecycle:JitterSeconds", 10)),
        new("ORDER_CARGO_INFO", configuration.GetValue("MarketplaceSync:TrendyolCargoInfo:IntervalSeconds", 300), 0, configuration.GetValue("MarketplaceSync:TrendyolCargoInfo:JitterSeconds", 15)),
        new("ORDER_RECONCILE_SHORT", configuration.GetValue("MarketplaceSync:OrderReconciliation:ShortIntervalSeconds", 900), 0, configuration.GetValue("MarketplaceSync:OrderReconciliation:ShortJitterSeconds", 30)),
        new("ORDER_RECONCILE_MEDIUM", configuration.GetValue("MarketplaceSync:OrderReconciliation:MediumIntervalSeconds", 3600), 0, configuration.GetValue("MarketplaceSync:OrderReconciliation:MediumJitterSeconds", 120)),
        new("ORDER_RECONCILE_DAILY", configuration.GetValue("MarketplaceSync:OrderReconciliation:DailyIntervalSeconds", 86_400), 0, configuration.GetValue("MarketplaceSync:OrderReconciliation:DailyJitterSeconds", 900)),
        new("ORDER_INVOICE_RECONCILIATION", configuration.GetValue("MarketplaceSync:OrderInvoiceReconciliation:IntervalSeconds", 900), 0, configuration.GetValue("MarketplaceSync:OrderInvoiceReconciliation:JitterSeconds", 30)),
        new("RETURNS", configuration.GetValue("MarketplaceSync:Returns:IntervalSeconds", 180), configuration.GetValue("MarketplaceSync:Returns:SafetyWindowSeconds", 900), configuration.GetValue("MarketplaceSync:Returns:JitterSeconds", 10)),
        new("RETURN_LIFECYCLE", configuration.GetValue("MarketplaceSync:ReturnLifecycle:IntervalSeconds", 180), 0, configuration.GetValue("MarketplaceSync:ReturnLifecycle:JitterSeconds", 10)),
        new("RETURN_RECONCILE_SHORT", configuration.GetValue("MarketplaceSync:ReturnReconciliation:ShortIntervalSeconds", 900), 0, configuration.GetValue("MarketplaceSync:ReturnReconciliation:ShortJitterSeconds", 30)),
        new("RETURN_RECONCILE_MEDIUM", configuration.GetValue("MarketplaceSync:ReturnReconciliation:MediumIntervalSeconds", 3600), 0, configuration.GetValue("MarketplaceSync:ReturnReconciliation:MediumJitterSeconds", 120)),
        new("RETURN_RECONCILE_DAILY", configuration.GetValue("MarketplaceSync:ReturnReconciliation:DailyIntervalSeconds", 86_400), 0, configuration.GetValue("MarketplaceSync:ReturnReconciliation:DailyJitterSeconds", 900)),
        new("REFERENCE_DATA", configuration.GetValue("MarketplaceSync:ReferenceData:IntervalSeconds", 86_400), 0, configuration.GetValue("MarketplaceSync:ReferenceData:JitterSeconds", 60)),
        new("QUESTIONS", configuration.GetValue("MarketplaceSync:Questions:IntervalSeconds", 120), 0, 0),
        new("STOCK_RECONCILE_SHORT", configuration.GetValue("MarketplaceSync:StockReconciliation:ShortIntervalSeconds", 900), 0, configuration.GetValue("MarketplaceSync:StockReconciliation:ShortJitterSeconds", 30)),
        new("STOCK_RECONCILE_MEDIUM", configuration.GetValue("MarketplaceSync:StockReconciliation:MediumIntervalSeconds", 3600), 0, configuration.GetValue("MarketplaceSync:StockReconciliation:MediumJitterSeconds", 120)),
        new("STOCK_RECONCILE_DAILY", configuration.GetValue("MarketplaceSync:StockReconciliation:DailyIntervalSeconds", 86_400), 0, configuration.GetValue("MarketplaceSync:StockReconciliation:DailyJitterSeconds", 900)),
        new(MarketplaceExternalWritePolicies.Price, 0, 0, 0),
        new(MarketplaceExternalWritePolicies.Stock, 0, 0, 0),
        new(MarketplaceExternalWritePolicies.Shipment, 0, 0, 0),
        new(MarketplaceExternalWritePolicies.Return, 0, 0, 0)
    ];

    // The application default of one policy of a platform (after the platform's cadence), or null when the
    // resource has no default. A user resetting a policy to its default gets these values.
    public static (int IntervalSeconds, int OverlapSeconds, int JitterSeconds)? DefaultPolicyValues(IConfiguration configuration, string platformCode, string resourceType)
    {
        var defaults = DefaultPolicies(configuration).FirstOrDefault(policy => policy.ResourceType == resourceType);
        if (defaults is null) return null;
        var cadence = ScheduledOrderPollingCadencePolicy.ForPlatform(platformCode, resourceType, defaults.IntervalSeconds, defaults.JitterSeconds);
        return (cadence.IntervalSeconds, defaults.OverlapSeconds, cadence.JitterSeconds);
    }

    // A save marks a policy as the user's own only when it changes the cadence: saving the values it already
    // had (for example only switching it on or off) keeps it following the application defaults.
    public static bool MarksUserDefined(bool alreadyUserDefined, (int IntervalSeconds, int OverlapSeconds, int JitterSeconds)? current, (int IntervalSeconds, int OverlapSeconds, int JitterSeconds) saved) =>
        alreadyUserDefined || current is null || current.Value != saved;

    internal static bool ShouldUpgradeToDefault(ConnectionSyncPolicy current, string platformCode) =>
        !current.UserDefined && IsKnownDefault(current, platformCode);

    internal static bool IsKnownDefault(ConnectionSyncPolicy current, string platformCode)
    {
        // The first generic read default; write flows never had it (theirs is "immediately", 0/0/0).
        if (current.IntervalSeconds == 300 && (current.OverlapSeconds == 60 || current.OverlapSeconds == 120) && current.JitterSeconds == 15)
            return !MarketplaceExternalWritePolicies.IsPolicy(current.ResourceType);
        return current.ResourceType switch
        {
            "ORDERS" => current.IntervalSeconds == 30 && current.OverlapSeconds == 600 && current.JitterSeconds == 2
                || current.IntervalSeconds == 60 && current.OverlapSeconds == 600 && (current.JitterSeconds == 0 || current.JitterSeconds == 5)
                || current.IntervalSeconds == 180 && current.OverlapSeconds == 600 && (current.JitterSeconds == 0 || current.JitterSeconds == 5)
                || current.IntervalSeconds == 480 && current.OverlapSeconds == 600 && (current.JitterSeconds == 0 || current.JitterSeconds == 30)
                || current.IntervalSeconds == 540 && current.OverlapSeconds == 600 && (current.JitterSeconds == 0 || current.JitterSeconds == 30)
                || current.IntervalSeconds == 900 && current.OverlapSeconds == 600 && (current.JitterSeconds == 0 || current.JitterSeconds == 30),
            "ORDER_RECOVERY" => (current.IntervalSeconds == 900 || current.IntervalSeconds == 2700 || current.IntervalSeconds == 3600) && current.OverlapSeconds == 600 && (current.JitterSeconds == 0 || current.JitterSeconds == 30),
            "QUESTIONS" => (current.IntervalSeconds == 60 || current.IntervalSeconds == 120) && current.OverlapSeconds == 0 && current.JitterSeconds == 0,
            "RETURNS" => current.IntervalSeconds == 60 && current.OverlapSeconds == 900 && current.JitterSeconds == 5
                || current.IntervalSeconds == 180 && current.OverlapSeconds == 900 && (current.JitterSeconds == 0 || current.JitterSeconds == 10),
            "ORDER_LIFECYCLE" => current.IntervalSeconds == 180 && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 10)
                || (current.IntervalSeconds == 480 || current.IntervalSeconds == 540 || current.IntervalSeconds == 1800) && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 10),
            "ORDER_CARGO_INFO" => (current.IntervalSeconds == 300 || current.IntervalSeconds == 1800) && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 15),
            "RETURN_LIFECYCLE" => (current.IntervalSeconds == 180 || current.IntervalSeconds == 1800) && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 10),
            "ORDER_RECONCILE_SHORT" => (current.IntervalSeconds == 900 || current.IntervalSeconds == 2700 || current.IntervalSeconds == 3600) && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 30),
            "RETURN_RECONCILE_SHORT" => (current.IntervalSeconds == 900 || current.IntervalSeconds == 3600) && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 30),
            "STOCK_RECONCILE_SHORT" => current.IntervalSeconds == 900 && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 30),
            "ORDER_RECONCILE_MEDIUM" => (current.IntervalSeconds == 3600 || current.IntervalSeconds == 10_800 || current.IntervalSeconds == 21_600) && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 120),
            "RETURN_RECONCILE_MEDIUM" => (current.IntervalSeconds == 3600 || current.IntervalSeconds == 21_600) && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 120),
            "STOCK_RECONCILE_MEDIUM" => current.IntervalSeconds == 3600 && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 120),
            "ORDER_RECONCILE_DAILY" => (current.IntervalSeconds == 86_400 || current.IntervalSeconds == 259_200) && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 900),
            "RETURN_RECONCILE_DAILY" or "STOCK_RECONCILE_DAILY" => current.IntervalSeconds == 86_400 && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 900),
            "ORDER_INVOICE_RECONCILIATION" => current.OverlapSeconds == 0 && (platformCode == "HEPSIBURADA"
                ? current.IntervalSeconds == 900 && (current.JitterSeconds == 0 || current.JitterSeconds == 30)
                    || current.IntervalSeconds == 300 && (current.JitterSeconds == 0 || current.JitterSeconds == 10)
                : current.IntervalSeconds == 900 && (current.JitterSeconds == 0 || current.JitterSeconds == 30)),
            "REFERENCE_DATA" => current.IntervalSeconds == 86_400 && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 60),
            _ => false
        };
    }

    private static int StableJitterSeconds(Guid connectionId, string resourceType, int maximum)
    {
        if (maximum <= 0) return 0;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{connectionId:N}:{resourceType}"));
        return (int)(BitConverter.ToUInt32(digest, 0) % (uint)(maximum + 1));
    }

    private static int ScheduledPolicyPriority(string resourceType) => resourceType switch
    {
        "ORDERS" => 0,
        "ORDER_RECOVERY" => 10,
        "ORDER_LIFECYCLE" => 20,
        "ORDER_CARGO_INFO" => 34,
        "ORDER_RECONCILE_SHORT" => 30,
        "ORDER_RECONCILE_MEDIUM" => 31,
        "ORDER_RECONCILE_DAILY" => 32,
        "ORDER_INVOICE_RECONCILIATION" => 33,
        "RETURNS" => 40,
        "RETURN_LIFECYCLE" => 50,
        "RETURN_RECONCILE_SHORT" => 60,
        "RETURN_RECONCILE_MEDIUM" => 61,
        "RETURN_RECONCILE_DAILY" => 62,
        "REFERENCE_DATA" => 65,
        "STOCK_RECONCILE_SHORT" => 70,
        "STOCK_RECONCILE_MEDIUM" => 71,
        "STOCK_RECONCILE_DAILY" => 72,
        _ => 100
    };

    private static bool IsOrderBackgroundJob(string jobType) =>
        jobType is MarketplaceJobTypes.OrderRecoverySync
            or MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation
            or MarketplaceJobTypes.ShopifyOrderRecoverySync
            or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync
            or MarketplaceJobTypes.OrderReconciliation
            or MarketplaceJobTypes.ShopifyOrderReconciliation
            or MarketplaceJobTypes.OrderInvoiceReconciliation
            or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation
            or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation;

    internal static bool IsShopifyReadPolicy(string resourceType) => resourceType is
        "ORDERS"
        or "ORDER_RECOVERY"
        or "ORDER_LIFECYCLE"
        or "ORDER_RECONCILE_SHORT"
        or "ORDER_RECONCILE_MEDIUM"
        or "ORDER_RECONCILE_DAILY"
        or "ORDER_INVOICE_RECONCILIATION";

    internal static bool SupportsShopifyPolicy(string resourceType) =>
        IsShopifyReadPolicy(resourceType) || MarketplaceExternalWritePolicies.IsPolicy(resourceType);

    internal static bool SupportsHepsiburadaScheduledPolicy(string resourceType) => resourceType is "ORDERS" or "ORDER_RECOVERY" or "ORDER_LIFECYCLE" or "ORDER_INVOICE_RECONCILIATION" or "RETURNS" or "RETURN_LIFECYCLE" or "REFERENCE_DATA" or "QUESTIONS"
        || MarketplaceExternalWritePolicies.IsPolicy(resourceType);

    // n11 flows join as their integration phases go live. Stock reconciliation carries
    // sales from other channels and panel edits to n11; it only writes while the
    // connection's external writes and its STOCK_WRITE policy are enabled.
    // RETURNS reads claims through the SOAP ReturnService; RETURN_WRITE sends approvals only.
    internal static bool SupportsN11ScheduledPolicy(string resourceType) => resourceType is "ORDERS" or "ORDER_RECOVERY" or "REFERENCE_DATA"
            or "ORDER_RECONCILE_SHORT" or "ORDER_RECONCILE_MEDIUM" or "ORDER_RECONCILE_DAILY" or "RETURNS" or "QUESTIONS"
            or "STOCK_RECONCILE_SHORT" or "STOCK_RECONCILE_MEDIUM" or "STOCK_RECONCILE_DAILY"
        || resourceType is MarketplaceExternalWritePolicies.Price or MarketplaceExternalWritePolicies.Stock or MarketplaceExternalWritePolicies.Shipment or MarketplaceExternalWritePolicies.Return;

    // Pazarama runs the order reads: new orders (ORDERS), the history scan (ORDER_RECOVERY) and the
    // open-order status scan (ORDER_LIFECYCLE), because the order list has no change filter, plus the
    // category/attribute/brand references. The write flows are listed so the policies exist (and stay
    // off) for the later phases.
    internal static bool SupportsPazaramaScheduledPolicy(string resourceType) => resourceType is "ORDERS" or "ORDER_RECOVERY" or "ORDER_LIFECYCLE" or "REFERENCE_DATA" or "RETURNS" or "QUESTIONS"
        || MarketplaceExternalWritePolicies.IsPolicy(resourceType);

    // PTT AVM runs the order reads: new orders (ORDERS), the history scan (ORDER_RECOVERY) and the open-order
    // status scan (ORDER_LIFECYCLE), because the order list has no change filter, plus the daily category tree
    // (REFERENCE_DATA). The write flows are listed so the
    // policies exist (and stay off) for the later phases.
    internal static bool SupportsPttAvmScheduledPolicy(string resourceType) => resourceType is "ORDERS" or "ORDER_RECOVERY" or "ORDER_LIFECYCLE" or "REFERENCE_DATA"
        || MarketplaceExternalWritePolicies.IsPolicy(resourceType);

    internal static bool DefaultPolicyEnabled(string platformCode, string resourceType) =>
        resourceType == "ORDER_CARGO_INFO"
            ? platformCode == "TRENDYOL"
            : !((platformCode is "HEPSIBURADA" or "SHOPIFY" or "N11" or "PAZARAMA" or "PTTAVM") && MarketplaceExternalWritePolicies.IsPolicy(resourceType));

    private (string JobType, string DedupPrefix, string PayloadJson)? Definition(string resourceType, Guid connectionId, string platformCode) => resourceType switch
    {
        "ORDERS" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderSync), $"scheduled:orders:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, externalOrderId = (string?)null })),
        "ORDER_RECOVERY" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderRecoverySync), $"scheduled:order-recovery:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, externalOrderId = (string?)null })),
        "ORDER_LIFECYCLE" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderStatusSync), $"scheduled:order-lifecycle:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "ORDER_CARGO_INFO" => (MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation, $"scheduled:order-cargo-info:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "ORDER_RECONCILE_SHORT" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderReconciliation), $"scheduled:order-reconcile-short:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:OrderReconciliation:ShortLookbackDays", 1, 1, 14), batchSize = ConfigInt("MarketplaceSync:OrderReconciliation:ShortBatchSize", 25, 1, 100) })),
        "ORDER_RECONCILE_MEDIUM" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderReconciliation), $"scheduled:order-reconcile-medium:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:OrderReconciliation:MediumLookbackDays", 3, 1, 30), batchSize = ConfigInt("MarketplaceSync:OrderReconciliation:MediumBatchSize", 50, 1, 100) })),
        "ORDER_RECONCILE_DAILY" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderReconciliation), $"scheduled:order-reconcile-daily:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:OrderReconciliation:DailyLookbackDays", 90, 1, 90), batchSize = ConfigInt("MarketplaceSync:OrderReconciliation:DailyBatchSize", 50, 1, 100) })),
        "ORDER_INVOICE_RECONCILIATION" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderInvoiceReconciliation), $"scheduled:order-invoice-reconciliation:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, batchSize = InvoiceReconciliationBatchSize(platformCode, configuration) })),
        "RETURNS" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.ReturnSync), $"scheduled:returns:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "RETURN_LIFECYCLE" => (MarketplaceJobTypes.ReturnStatusSync, $"scheduled:return-lifecycle:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "RETURN_RECONCILE_SHORT" => (MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-short:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:ReturnReconciliation:ShortLookbackDays", 1, 1, 14) })),
        "RETURN_RECONCILE_MEDIUM" => (MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-medium:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:ReturnReconciliation:MediumLookbackDays", 3, 1, 30) })),
        "RETURN_RECONCILE_DAILY" => (MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-daily:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:ReturnReconciliation:DailyLookbackDays", 90, 1, 90) })),
        "STOCK_RECONCILE_SHORT" => (MarketplaceJobTypes.StockReconciliation, $"scheduled:stock-reconcile-short:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackHours = ConfigInt("MarketplaceSync:StockReconciliation:ShortLookbackHours", 1, 1, 24 * 7) })),
        "STOCK_RECONCILE_MEDIUM" => (MarketplaceJobTypes.StockReconciliation, $"scheduled:stock-reconcile-medium:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackHours = ConfigInt("MarketplaceSync:StockReconciliation:MediumLookbackHours", 6, 1, 24 * 30) })),
        "STOCK_RECONCILE_DAILY" => (MarketplaceJobTypes.StockReconciliation, $"scheduled:stock-reconcile-daily:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackHours = ConfigInt("MarketplaceSync:StockReconciliation:DailyLookbackHours", 720, 1, 24 * 90) })),
        "REFERENCE_DATA" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.ReferenceSync), $"scheduled:reference:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, resourceType = "CATEGORIES", parentExternalId = (string?)null })),
        "QUESTIONS" => (MarketplaceJobTypes.QuestionSync, $"scheduled:questions:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, kind = (string?)null })),
        _ => null
    };

    // The flow's own job, started now and ahead of scheduled work. A run of the same flow that is
    // already running or due is returned instead of a second one. Flows that write to the
    // marketplace start only while writes are enabled for the connection.
    public async Task<ServiceResult<Guid>> RunNowAsync(Guid tenantId, Guid connectionId, string resourceType, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return ServiceResult<Guid>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        if (connection.Status is not ("ACTIVE" or "VERIFIED"))
            return ServiceResult<Guid>.Fail("CONNECTION_INACTIVE", "Bağlantı aktif olmadığı için akış başlatılamaz.", 409);
        var type = resourceType.Trim().ToUpperInvariant();
        if (Definition(type, connectionId, connection.PlatformCode) is not { } definition)
            return ServiceResult<Guid>.Fail("SYNC_FLOW_NOT_RUNNABLE", "Bu akış elle başlatılamaz.", 422);
        if (type.StartsWith("STOCK_RECONCILE", StringComparison.Ordinal) && !WritesEnabled(connection.SettingsJson))
            return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Pazaryerine yazma kapalı olduğu için stok eşitlemesi başlatılamaz.", 409);

        var now = timeProvider.GetUtcNow();
        var manualPrefix = $"manual:{definition.DedupPrefix}";
        var waiting = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == definition.JobType
                && (x.JobDedupKey.StartsWith(definition.DedupPrefix) || x.JobDedupKey.StartsWith(manualPrefix))
                && (x.Status == JobStatus.Leased || (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled) && x.AvailableAt <= now))
            .OrderBy(x => x.CreatedAt)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (waiting is { } existing) return ServiceResult<Guid>.Ok(existing);

        var job = NewJob(tenantId, connectionId, definition.JobType, $"{manualPrefix}:{Guid.CreateVersion7():N}", definition.PayloadJson, now, correlationId);
        job.TriggerType = "user";
        job.Priority = JobPriorityPolicy.For(definition.JobType, userInitiated: true);
        db.IntegrationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(job.Id);
    }

    private int ConfigInt(string key, int fallback, int minimum, int maximum) => Math.Clamp(configuration.GetValue(key, fallback), minimum, maximum);

    internal static int InvoiceReconciliationBatchSize(string platformCode, IConfiguration configuration) =>
        string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(configuration.GetValue("MarketplaceSync:OrderInvoiceReconciliation:HepsiburadaBatchSize", 100), 1, 250)
            : string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
                ? Math.Clamp(configuration.GetValue("MarketplaceSync:OrderInvoiceReconciliation:TrendyolBatchSize", 50), 1, 100)
            : Math.Clamp(configuration.GetValue("MarketplaceSync:OrderInvoiceReconciliation:BatchSize", 20), 1, 250);

    private bool WritesEnabled(string settingsJson)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false;
        return StoredWritesEnabled(settingsJson);
    }

    private static bool StoredWritesEnabled(string settingsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static string DisableStoredWrites(string settingsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            var userAgent = document.RootElement.TryGetProperty("UserAgentIdentity", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
            return JsonSerializer.Serialize(new { UserAgentIdentity = userAgent, ExternalWritesEnabled = false });
        }
        catch (JsonException) { return settingsJson; }
    }

    private static bool IsExpectedDedupRace(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_jobs_TenantId_JobType_JobDedupKey" }) return true;
        }

        return false;
    }

    private static IntegrationJob NewJob(Guid tenantId, Guid? connectionId, string type, string dedup, string payload, DateTimeOffset availableAt, string correlationId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        JobType = type,
        PayloadJson = payload,
        PayloadVersion = 1,
        PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
        JobDedupKey = dedup,
        EffectIdempotencyKey = dedup,
        Priority = Priority(type),
        AvailableAt = availableAt,
        CorrelationId = correlationId,
        Version = 1
    };

    private static int Priority(string type) => JobPriorityPolicy.For(type, userInitiated: false);

    internal sealed record PolicyDefaults(string ResourceType, int IntervalSeconds, int OverlapSeconds, int JitterSeconds);

}
