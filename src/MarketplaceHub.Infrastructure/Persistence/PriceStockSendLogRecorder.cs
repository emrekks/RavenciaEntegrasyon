using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Writes the price/stock send log where the job already knows the outcome, in the job's own save: the submit adds one
// row per line of the request (one read of the offers' confirmed values for the whole request), the poll settles them
// (one read of the job's open rows, or one UPDATE when every line gets the same outcome). No marketplace call of its own.
internal static class PriceStockSendLogRecorder
{
    public static string Trigger(IntegrationJob job, PriceInventoryJobPayload payload) =>
        PriceStockSendTriggers.ForJob(payload.Trigger, payload.Manual, payload.Automatic, string.Equals(job.TriggerType, "user", StringComparison.Ordinal), payload.VariantId is not null);

    // Rows for every line of a request the marketplace was sent; added to the caller's unit of work.
    public static async Task AddAsync(AppDbContext db, Guid tenantId, Guid connectionId, string platformCode, IntegrationJob job, PriceInventoryJobPayload payload,
        string? externalRequestId, DateTimeOffset now, string result, string? errorCode, string? errorMessage, CancellationToken cancellationToken)
    {
        if (payload.Lines.Count == 0) return;
        var offerIds = payload.Lines.Select(line => line.OfferId).Distinct().ToArray();
        var confirmed = await db.ChannelOffers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && offerIds.Contains(x.Id))
            .Select(x => new { x.Id, Values = new PriceStockConfirmedValues(x.LastSentCode, x.LastSentQuantity, x.LastSentSalePrice, x.LastSentListPrice) })
            .ToDictionaryAsync(x => x.Id, x => x.Values, cancellationToken);
        var trigger = Trigger(job, payload);
        db.PriceStockSendLogs.AddRange(payload.Lines.Select(line => PriceStockSendLogPolicy.Row(tenantId, connectionId, platformCode, job.Id, line,
            confirmed.GetValueOrDefault(line.OfferId), trigger, externalRequestId, now, result, errorCode, errorMessage)));
    }

    // The open rows of a polled request, tracked, so the poll's own save writes their outcomes with the offers.
    public static async Task<PendingSendLog> PendingAsync(AppDbContext db, Guid tenantId, Guid jobId, string externalRequestId, DateTimeOffset now, CancellationToken cancellationToken) =>
        new(await db.PriceStockSendLogs
            .Where(x => x.TenantId == tenantId && x.JobId == jobId && x.ExternalRequestId == externalRequestId && x.Result == PriceStockSendResults.Submitted)
            .ToListAsync(cancellationToken), now);

    // One outcome for every open row of a polled request, written at once (the poll ends without saving anything else).
    public static Task<int> SettleAllAsync(AppDbContext db, Guid tenantId, Guid jobId, string? externalRequestId, string result, string? errorCode, string? errorMessage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalRequestId)) return Task.FromResult(0);
        var settled = new PriceStockSendLog { PlatformCode = "", Barcode = "", Currency = "", Trigger = "", Result = PriceStockSendResults.Submitted };
        PriceStockSendLogPolicy.Settle(settled, result, errorCode, errorMessage, now);
        return db.PriceStockSendLogs
            .Where(x => x.TenantId == tenantId && x.JobId == jobId && x.ExternalRequestId == externalRequestId && x.Result == PriceStockSendResults.Submitted)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Result, settled.Result)
                .SetProperty(x => x.ErrorCode, settled.ErrorCode)
                .SetProperty(x => x.ErrorMessage, settled.ErrorMessage)
                .SetProperty(x => x.CompletedAt, settled.CompletedAt), cancellationToken);
    }
}

// The open rows of one polled request by offer; a line without a row (sent before the log existed) is skipped.
internal sealed class PendingSendLog(IReadOnlyList<PriceStockSendLog> rows, DateTimeOffset now)
{
    private readonly Dictionary<Guid, PriceStockSendLog> byOffer = rows.GroupBy(row => row.OfferId).ToDictionary(group => group.Key, group => group.First());

    public void Settle(PriceInventoryPushLine line, string result, string? errorCode = null, string? errorMessage = null)
    {
        if (byOffer.TryGetValue(line.OfferId, out var row)) PriceStockSendLogPolicy.Settle(row, result, errorCode, errorMessage, now);
    }
}
