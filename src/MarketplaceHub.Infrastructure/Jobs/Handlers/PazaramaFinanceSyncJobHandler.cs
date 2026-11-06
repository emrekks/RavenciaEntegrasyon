using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs.Handlers;

// Reads the money movements of a production Pazarama store's recent orders (order/paymentAgreement: sale, refund,
// commission, cargo debt, withholding) into the finance table, read only, so the reports take Pazarama's commission
// from it: the order service sends none. Pazarama answers one order at a time, so a run asks for the orders of the
// last days that have no movement stored yet first, then the stored ones again (a refund is written later).
public sealed class PazaramaFinanceSyncJobHandler(AppDbContext db, IOrderFinancePort finance, TimeProvider timeProvider, IConfiguration configuration) : IJobHandler
{
    internal const string Source = "PAZARAMA_AGREEMENT";
    internal const string RequestedType = "PaymentAgreement";
    internal const string FailureCode = "PAZARAMA_FINANCE_SYNC_FAILED";

    public bool Handles(string jobType) => jobType == MarketplaceJobTypes.PazaramaFinanceSync;

    public async Task<JobOutcome> HandleAsync(JobRequest request, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == request.TenantId && x.Id == request.ConnectionId)
            .Select(x => new { x.PlatformCode, x.Environment })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null || !string.Equals(connection.PlatformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase))
            return new(JobExecutionResult.Blocked("PAZARAMA_FINANCE_CONNECTION_INVALID", "Finans okuması yalnız Pazarama bağlantısında çalışır."), new());
        if (!string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase))
            return new(JobExecutionResult.Blocked("PAZARAMA_FINANCE_PRODUCTION_ONLY", "Finans okuması yalnız canlı (production) Pazarama mağazasında çalışır."), new());

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var since = now.AddDays(-Setting("LookbackDays", 60, 1, 180));
        var orders = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && x.OrderedAt >= since && x.DerivedStatus != "CANCELLED")
            .Select(x => new { x.OrderNumber, x.OrderedAt })
            .ToListAsync(cancellationToken);
        var numbers = orders.Select(x => x.OrderNumber).ToList();
        var stored = await db.MarketplaceFinanceTransactions
            .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && x.Source == Source && x.OrderNumber != null && numbers.Contains(x.OrderNumber))
            .ToListAsync(cancellationToken);
        var refreshed = stored.GroupBy(x => x.OrderNumber!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Min(row => row.UpdatedAt), StringComparer.Ordinal);
        var due = Due(orders.Select(x => (x.OrderNumber, x.OrderedAt)), refreshed, Setting("OrdersPerRun", 100, 1, 500));

        var context = new AdapterContext(request.TenantId, request.ConnectionId, request.CorrelationId, $"pazarama-finance:{request.ConnectionId:N}", now.AddMinutes(10), Operation: IntegrationOperation.Automatic);
        int requests = 0, received = 0, inserted = 0, updated = 0;
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<RemoteSkippedFinanceRecord>();
        var storedCodes = new List<string>();
        foreach (var order in due)
        {
            requests++;
            var result = await finance.GetOrderFinanceAsync(context, order.OrderNumber, order.OrderedAt.AddDays(-1), now.AddDays(1), cancellationToken);
            if (!result.IsSuccess && OrderOnly(result.Error!))
            {
                // Pazarama refused this one order (not the connection, not a quota): it is named and the others are read.
                await UpsertIssueAsync(request.TenantId, $"{FailureKey(request.ConnectionId)}:{Short(order.OrderNumber, 64)}",
                    $"Pazarama finans hareketleri {order.OrderNumber} numaralı siparişte okunamadı ({result.Error!.Code}: {result.Error.SafeMessage}); diğer siparişler okundu, bu sipariş sonraki çalışmada yeniden denenecek.", now, cancellationToken);
                failed.Add(order.OrderNumber);
                continue;
            }
            if (!result.IsSuccess)
            {
                await FinanceRecordIssues.RecordAsync(db, request.TenantId, request.ConnectionId, "Pazarama", skipped, storedCodes, now, cancellationToken);
                // What was read so far is kept; the rest waits for the next run.
                await UpsertIssueAsync(request.TenantId, FailureKey(request.ConnectionId),
                    $"Pazarama finans hareketleri {order.OrderNumber} numaralı siparişte okunamadı ({result.Error!.Code}: {result.Error.SafeMessage}); sonraki çalışmada yeniden denenecek.", now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return new(JobExecutionResult.FromAdapterError(result.Error), new(requests, received, inserted, updated, inserted + updated, 1));
            }
            // Movements without a code, an amount, an allowance or a date are not stored; they are named in issues.
            skipped.AddRange(result.Value!.Skipped);
            received += result.Value.Skipped.Count;
            foreach (var movement in result.Value.Transactions)
            {
                received++;
                storedCodes.Add(movement.TransactionCode);
                var row = stored.FirstOrDefault(x => x.ExternalId == movement.TransactionCode);
                if (row is null)
                {
                    row = new MarketplaceFinanceTransaction
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = request.TenantId,
                        ConnectionId = request.ConnectionId,
                        PlatformCode = "PAZARAMA",
                        Source = Source,
                        ExternalId = Short(movement.TransactionCode, 128)!,
                        RequestedType = RequestedType,
                        Category = MarketplaceFinanceCategories.Sale,
                        RawJson = "{}",
                        FirstSeenAt = now
                    };
                    db.MarketplaceFinanceTransactions.Add(row);
                    stored.Add(row);
                    inserted++;
                }
                else updated++;
                Fill(row, movement, order.OrderNumber, now);
            }
        }
        await FinanceRecordIssues.RecordAsync(db, request.TenantId, request.ConnectionId, "Pazarama", skipped, storedCodes, now, cancellationToken);
        await ResolveIssueAsync(request.TenantId, FailureKey(request.ConnectionId), now, cancellationToken);
        // An order refused before and read now closes its own issue.
        var readKeys = due.Where(x => !failed.Contains(x.OrderNumber)).Select(x => $"{FailureKey(request.ConnectionId)}:{Short(x.OrderNumber, 64)}").ToList();
        foreach (var issue in await db.OperationalIssues.Where(x => x.TenantId == request.TenantId && x.Status != IssueStatus.Resolved && readKeys.Contains(x.DedupeKey)).ToListAsync(cancellationToken))
        {
            issue.Status = IssueStatus.Resolved;
            issue.LastSeenAt = now;
            issue.ResolvedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(JobExecutionResult.Success(), new(requests, received, inserted, updated, inserted + updated, failed.Count));
    }

    // Orders without a stored movement first, the newest of them first; then the read ones, the one refreshed longest
    // ago first. Before 6 Nov 2026 the read ones went newest first, so past OrdersPerRun orders in the lookback the
    // older ones were never read again (late refunds, the payout day `transferredDate`).
    internal static List<(string OrderNumber, DateTimeOffset OrderedAt)> Due(IEnumerable<(string OrderNumber, DateTimeOffset OrderedAt)> orders, IReadOnlyDictionary<string, DateTimeOffset> refreshed, int perRun) =>
        orders.OrderBy(x => refreshed.ContainsKey(x.OrderNumber))
            .ThenBy(x => refreshed.TryGetValue(x.OrderNumber, out var at) ? at : DateTimeOffset.MinValue)
            .ThenByDescending(x => x.OrderedAt)
            .Take(perRun).ToList();

    // A refusal of one order (not found, refused, an answer that cannot be read) does not stop the others; a quota,
    // network, server or credential failure does, as the next order would meet it too.
    internal static bool OrderOnly(AdapterError error) =>
        error.Class is AdapterErrorClass.NotFound or AdapterErrorClass.Validation or AdapterErrorClass.ContractViolation or AdapterErrorClass.BusinessConflict;

    // A sale credits the seller and a refund debits it; the commission is kept as the cost to the seller (Pazarama
    // sends it negative on a sale, positive when a refund gives it back), as the payout takes it off; null when the
    // movement did not send it (the reports then estimate the order's commission).
    internal static void Fill(MarketplaceFinanceTransaction row, RemoteFinanceTransaction movement, string orderNumber, DateTimeOffset now)
    {
        row.TransactionType = Short(movement.Status, 200);
        row.Category = movement.Amount < 0 ? MarketplaceFinanceCategories.Return : MarketplaceFinanceCategories.Sale;
        row.TransactionDate = movement.TransactionDate.ToUniversalTime();
        row.OrderNumber = orderNumber;
        row.Credit = Math.Max(0m, movement.Amount);
        row.Debt = Math.Max(0m, -movement.Amount);
        row.CommissionAmount = -movement.Commission;
        row.SellerRevenue = movement.Allowance;
        row.PaymentDate = movement.TransferredDate?.ToUniversalTime();
        row.RawJson = JsonSerializer.Serialize(movement);
        row.UpdatedAt = now;
    }

    internal static string FailureKey(Guid connectionId) => $"pazarama-finance:{connectionId:N}";

    private async Task UpsertIssueAsync(Guid tenantId, string key, string summary, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var issue = await db.OperationalIssues.SingleOrDefaultAsync(item => item.TenantId == tenantId && item.DedupeKey == key, cancellationToken);
        if (issue is null)
        {
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = FailureCode, Summary = Short(summary, 1000)!, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            return;
        }
        issue.Code = FailureCode;
        issue.Summary = Short(summary, 1000)!;
        issue.Status = IssueStatus.Open;
        issue.LastSeenAt = now;
        issue.OccurrenceCount++;
    }

    private async Task ResolveIssueAsync(Guid tenantId, string key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var issue = await db.OperationalIssues.SingleOrDefaultAsync(item => item.TenantId == tenantId && item.DedupeKey == key && item.Status != IssueStatus.Resolved, cancellationToken);
        if (issue is null) return;
        issue.Status = IssueStatus.Resolved;
        issue.LastSeenAt = now;
        issue.ResolvedAt = now;
    }

    private int Setting(string name, int fallback, int minimum, int maximum) =>
        Math.Clamp(configuration.GetValue($"MarketplaceSync:PazaramaFinance:{name}", fallback), minimum, maximum);

    private static string? Short(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
