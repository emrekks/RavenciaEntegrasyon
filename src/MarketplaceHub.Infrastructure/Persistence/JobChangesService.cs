using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// İşlem Takibi › İş detayı › Gidenler ve Değişenler. Read-only: every row comes from a record that already keeps it.
//   - PRICE_INVENTORY_SYNC: its own price/stock send log rows (one per line it carried, with the marketplace's answer).
//   - STOCK_RECONCILIATION and STOCK_PROJECTION_DISPATCH send nothing themselves: they queue a PRICE_INVENTORY_SYNC under
//     their own correlation id while they run, so the rows are those send jobs' send log rows, and the send jobs are
//     listed as links. A scan that queued nothing says why from what the job recorded (its note code), never a guess.
//   - A product import (PRODUCT_SYNC, Shopify, Hepsiburada): the price and stock changes Ürün Hareketleri lists for the
//     import's connection within the job's run (the ledger and the price history keep no job id).
public sealed class JobChangesService(AppDbContext db, IPriceStockSendLogService sendLog, IProductActivityService activity, TimeProvider timeProvider) : IJobChangesService
{
    // A send job is queued while the scan runs; a second either side covers the clocks of one save.
    private static readonly TimeSpan WindowSlack = TimeSpan.FromSeconds(1);

    private static readonly string[] ProductImportTypes = [MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ShopifyProductSync, MarketplaceJobTypes.HepsiburadaProductSync];
    private static readonly string[] ImportKinds = [ProductActivityKinds.Stock, ProductActivityKinds.Price];
    private static readonly string[] ImportMovements = [ProductActivityMovements.MarketplaceStock, ProductActivityMovements.OpeningStock, ProductActivityMovements.OfferPrice];
    private static readonly JobChangeCounts Unknown = new(null, null, null, null, null, null, null, null, null, null);

    private const string JobNotFoundCode = "JOB_NOT_FOUND";
    private const string JobNotFoundMessage = "İşlem kaydı bulunamadı; silinmiş ya da kapatılmış olabilir.";

    // What a job's list is read from: its mode, the send jobs (send log mode), the window and connection (import mode).
    private sealed record Scope(IntegrationJob Job, string Mode, IReadOnlyList<IntegrationJob> SpawnedJobs, IReadOnlyList<Guid> SendJobIds, DateTimeOffset From, DateTimeOffset To);

    public async Task<ServiceResult<JobChangesSummaryView>> SummaryAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, jobId, cancellationToken);
        if (scope is null) return ServiceResult<JobChangesSummaryView>.Fail(JobNotFoundCode, JobNotFoundMessage, 404);
        var job = scope.Job;
        var spawned = scope.SpawnedJobs.Select(x => new JobSpawnedJobView(x.Id, x.JobType, JobStatusText(x.Status), x.CreatedAt, x.CompletedAt, x.LastErrorCode)).ToList();
        if (scope.Mode == JobChangeModes.ProductImport)
        {
            var counts = await activity.CountsAsync(tenantId, ImportQuery(scope, null, null, null), cancellationToken);
            if (!counts.Succeeded) return ServiceResult<JobChangesSummaryView>.Fail(counts.Error!.Code, counts.Error.Message, counts.Error.Status);
            int KindCount(string kind) => counts.Value!.Where(x => x.Kind == kind).Sum(x => x.Count);
            var price = KindCount(ProductActivityKinds.Price);
            var stock = KindCount(ProductActivityKinds.Stock);
            var outcome = Unfinished(job) ? JobChangeOutcomes.Running : price + stock == 0 && job.Status == JobStatus.Succeeded ? JobChangeOutcomes.NoChanges : null;
            return ServiceResult<JobChangesSummaryView>.Ok(new(scope.Mode, outcome, Unknown with { PriceChanged = price, StockChanged = stock }, [], null, null));
        }
        if (scope.Mode == JobChangeModes.None)
            return ServiceResult<JobChangesSummaryView>.Ok(new(scope.Mode, null, Unknown, [], null, null));

        var trigger = job.JobType switch
        {
            MarketplaceJobTypes.StockReconciliation => PriceStockSendTriggers.Reconcile,
            MarketplaceJobTypes.StockProjectionDispatch => PriceStockSendTriggers.FromSourceEvent(PayloadString(job.PayloadJson, "sourceEventId")),
            _ => PayloadTrigger(job.PayloadJson)
        };
        var orderNumber = job.JobType == MarketplaceJobTypes.StockProjectionDispatch ? await SourceOrderNumberAsync(tenantId, PayloadString(job.PayloadJson, "sourceEventId"), cancellationToken) : null;
        if (scope.SendJobIds.Count == 0)
            return ServiceResult<JobChangesSummaryView>.Ok(new(scope.Mode, await ScanOutcomeAsync(tenantId, scope, cancellationToken), Unknown with { Sent = 0 }, spawned, trigger, orderNumber));
        var results = await sendLog.CountsAsync(tenantId, new PriceStockSendLogQuery(JobIds: scope.SendJobIds), cancellationToken);
        if (!results.Succeeded) return ServiceResult<JobChangesSummaryView>.Fail(results.Error!.Code, results.Error.Message, results.Error.Status);
        int Count(params string[] codes) => results.Value!.Where(x => codes.Contains(x.Result, StringComparer.Ordinal)).Sum(x => x.Count);
        var total = results.Value!.Sum(x => x.Count);
        var summary = Unknown with
        {
            Sent = total,
            Applied = Count(PriceStockSendResults.Applied),
            Unchanged = Count(PriceStockSendResults.Unchanged),
            Pending = Count(PriceStockSendResults.Submitted),
            Rejected = Count(PriceStockSendResults.Rejected, PriceStockSendResults.Failed),
            Uncertain = Count(PriceStockSendResults.Unknown),
            Superseded = Count(PriceStockSendResults.Superseded)
        };
        // No line yet: what the send job (the job itself, or the last one a scan queued) recorded about it.
        var lineOutcome = total > 0 ? null : SendOutcome(job.JobType == MarketplaceJobTypes.PriceInventorySync ? job : scope.SpawnedJobs[^1]);
        return ServiceResult<JobChangesSummaryView>.Ok(new(scope.Mode, lineOutcome, summary, spawned, trigger, orderNumber));
    }

    public async Task<ServiceResult<PageResult<JobChangeLineView>>> PageAsync(Guid tenantId, Guid jobId, JobChangesQuery query, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, jobId, cancellationToken);
        if (scope is null) return ServiceResult<PageResult<JobChangeLineView>>.Fail(JobNotFoundCode, JobNotFoundMessage, 404);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var empty = ServiceResult<PageResult<JobChangeLineView>>.Ok(new([], null, false));
        if (scope.Mode == JobChangeModes.ProductImport)
        {
            var page = await activity.PageAsync(tenantId, ImportQuery(scope, query.EffectiveLimit, query.After, search), cancellationToken);
            if (!page.Succeeded) return ServiceResult<PageResult<JobChangeLineView>>.Fail(page.Error!.Code, page.Error.Message, page.Error.Status);
            return ServiceResult<PageResult<JobChangeLineView>>.Ok(new(page.Value!.Items.Select(FromActivity).ToList(), page.Value.NextCursor, page.Value.HasMore));
        }
        if (scope.Mode != JobChangeModes.SendLog || scope.SendJobIds.Count == 0) return empty;
        var lines = await sendLog.PageAsync(tenantId, new PriceStockSendLogQuery(query.EffectiveLimit, query.After, Search: search, JobIds: scope.SendJobIds), cancellationToken);
        if (!lines.Succeeded) return ServiceResult<PageResult<JobChangeLineView>>.Fail(lines.Error!.Code, lines.Error.Message, lines.Error.Status);
        return ServiceResult<PageResult<JobChangeLineView>>.Ok(new(lines.Value!.Items.Select(FromSendLog).ToList(), lines.Value.NextCursor, lines.Value.HasMore));
    }

    private async Task<Scope?> ScopeAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == jobId, cancellationToken);
        if (job is null) return null;
        var (from, to) = await RunWindowAsync(job, cancellationToken);
        if (job.JobType == MarketplaceJobTypes.PriceInventorySync) return new(job, JobChangeModes.SendLog, [], [job.Id], from, to);
        if (job.JobType is MarketplaceJobTypes.StockReconciliation or MarketplaceJobTypes.StockProjectionDispatch)
        {
            // The send jobs it queued: same connection and correlation id, created while it ran (a dispatch's correlation
            // id names its variant, so the window keeps another dispatch's sends of the same variant out).
            var spawned = job.ConnectionId is { } connectionId
                ? await db.IntegrationJobs.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.CorrelationId == job.CorrelationId && x.JobType == MarketplaceJobTypes.PriceInventorySync
                        && x.Id != job.Id && x.CreatedAt >= from - WindowSlack && x.CreatedAt <= to + WindowSlack)
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                    .Take(JobChangesQuery.MaximumSpawnedJobs)
                    .ToListAsync(cancellationToken)
                : [];
            return new(job, JobChangeModes.SendLog, spawned, spawned.Select(x => x.Id).ToList(), from, to);
        }
        if (ProductImportTypes.Contains(job.JobType, StringComparer.Ordinal) && job.ConnectionId is not null)
            return new(job, JobChangeModes.ProductImport, [], [], from, to);
        return new(job, JobChangeModes.None, [], [], from, to);
    }

    private static ProductActivityQuery ImportQuery(Scope scope, int? limit, string? after, string? search) =>
        new(limit, after, ImportKinds, null, scope.From - WindowSlack, scope.To + WindowSlack, search, ImportMovements, ConnectionId: scope.Job.ConnectionId);

    private static JobChangeLineView FromSendLog(PriceStockSendLogView row) => new(
        row.Id, row.CreatedAt,
        row.NewQuantity is not null && row.NewSalePrice is null && row.NewListPrice is null ? JobChangeLineKinds.Stock
            : row.NewQuantity is null && (row.NewSalePrice is not null || row.NewListPrice is not null) ? JobChangeLineKinds.Price
            : JobChangeLineKinds.StockAndPrice,
        row.ProductId, row.ProductTitle, row.ImageUrl, row.ModelCode, row.Barcode, row.StockCode, row.OptionText,
        row.PreviousQuantity, row.NewQuantity, row.PreviousSalePrice, row.NewSalePrice, row.PreviousListPrice, row.NewListPrice, row.Currency,
        row.Trigger, row.Result, row.ErrorCode, row.ErrorMessage, row.ConnectionId, row.PlatformCode, row.StoreName, row.JobId);

    private static JobChangeLineView FromActivity(ProductActivityView row)
    {
        var stock = row.Kind == ProductActivityKinds.Stock;
        return new(row.Id, row.OccurredAt, stock ? JobChangeLineKinds.Stock : JobChangeLineKinds.Price,
            row.ProductId, row.ProductTitle, row.ImageUrl, row.ModelCode, row.Barcode, row.Sku, row.OptionText,
            stock ? row.StockBefore : null, stock ? row.StockAfter : null,
            stock ? null : row.PreviousSalePrice, stock ? null : row.NewSalePrice, stock ? null : row.PreviousListPrice, stock ? null : row.NewListPrice,
            row.Currency, row.Source, JobChangeResults.Changed, null, null, row.ConnectionId, row.PlatformCode, row.StoreName, null);
    }

    private static bool Unfinished(IntegrationJob job) => job.Status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled;

    // Why a scan or a dispatch queued no send, from its own note code and status.
    private async Task<string?> ScanOutcomeAsync(Guid tenantId, Scope scope, CancellationToken cancellationToken)
    {
        var job = scope.Job;
        if (job.LastErrorCode == OrderFreshnessGatePolicy.WaitCode) return JobChangeOutcomes.OrderReadPending;
        if (Unfinished(job)) return JobChangeOutcomes.Running;
        if (job.LastErrorCode == "AUTOMATIC_WRITES_OFF") return JobChangeOutcomes.AutomaticWritesOff;
        if (job.LastErrorCode == "PRICE_INVENTORY_PUSH_IN_FLIGHT") return JobChangeOutcomes.PushInFlight;
        if (job.Status != JobStatus.Succeeded) return null;
        // The price-policy hold finishes the job as a success and records its issue in the same run.
        if (job.ConnectionId is { } connectionId)
        {
            var key = $"stock-projection-price-policy:{connectionId:N}";
            if (await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.DedupeKey == key && x.LastSeenAt >= scope.From - WindowSlack && x.LastSeenAt <= scope.To + WindowSlack, cancellationToken))
                return JobChangeOutcomes.PricePolicyHeld;
        }
        return JobChangeOutcomes.NoChanges;
    }

    // A send job without a send log row: why, from what it recorded.
    internal static string? SendOutcome(IntegrationJob job)
    {
        if (job.LastErrorCode is "EXTERNAL_WRITES_DISABLED" or "EXTERNAL_WRITE_POLICY_DISABLED") return JobChangeOutcomes.AutomaticWritesOff;
        if (job.LastErrorCode == "PRICE_INVENTORY_SUPERSEDED") return JobChangeOutcomes.Superseded;
        if (Unfinished(job)) return JobChangeOutcomes.Running;
        return job.Status == JobStatus.Succeeded ? JobChangeOutcomes.NoChanges : null;
    }

    // From the job's first run to its end (now while it runs). A job that never ran has an empty window at its creation.
    private async Task<(DateTimeOffset From, DateTimeOffset To)> RunWindowAsync(IntegrationJob job, CancellationToken cancellationToken)
    {
        var firstAttempt = await db.JobAttempts.AsNoTracking().Where(x => x.TenantId == job.TenantId && x.JobId == job.Id)
            .OrderBy(x => x.AttemptNumber).Select(x => (DateTimeOffset?)x.StartedAt).FirstOrDefaultAsync(cancellationToken);
        var from = (firstAttempt ?? job.StartedAt ?? job.CreatedAt).ToUniversalTime();
        var to = (job.CompletedAt ?? timeProvider.GetUtcNow()).ToUniversalTime();
        return (from, to < from ? from : to);
    }

    // The order whose line queued the dispatch ("{line id}:{unix ms}"): its number only.
    private async Task<string?> SourceOrderNumberAsync(Guid tenantId, string? sourceEventId, CancellationToken cancellationToken)
    {
        if (PriceStockSendTriggers.FromSourceEvent(sourceEventId) != PriceStockSendTriggers.Order || !Guid.TryParseExact(sourceEventId![..32], "N", out var lineId)) return null;
        return await (from line in db.OrderLines.AsNoTracking()
                      join order in db.Orders.AsNoTracking() on new { line.TenantId, Id = line.OrderId } equals new { order.TenantId, order.Id }
                      where line.TenantId == tenantId && line.Id == lineId
                      select order.OrderNumber).FirstOrDefaultAsync(cancellationToken);
    }

    // The send job's own trigger (its payload names it; a job queued before it did names none).
    private static string? PayloadTrigger(string payloadJson)
    {
        var trigger = PayloadString(payloadJson, "Trigger") ?? PayloadString(payloadJson, "trigger");
        return trigger is not null && PriceStockSendTriggers.All.Contains(trigger, StringComparer.Ordinal) ? trigger : null;
    }

    private static string? PayloadString(string payloadJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string JobStatusText(JobStatus status) => status switch
    {
        JobStatus.RetryScheduled => "RETRY_SCHEDULED",
        JobStatus.ManualReview => "MANUAL_REVIEW",
        _ => status.ToString().ToUpperInvariant()
    };
}
