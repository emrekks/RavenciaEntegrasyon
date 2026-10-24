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
        var read = stored.Select(x => x.OrderNumber!).ToHashSet(StringComparer.Ordinal);
        // Orders without a stored movement first, the newest of them first.
        var due = orders.OrderBy(x => read.Contains(x.OrderNumber)).ThenByDescending(x => x.OrderedAt).Take(Setting("OrdersPerRun", 100, 1, 500)).ToList();

        var context = new AdapterContext(request.TenantId, request.ConnectionId, request.CorrelationId, $"pazarama-finance:{request.ConnectionId:N}", now.AddMinutes(10), Operation: IntegrationOperation.Automatic);
        int requests = 0, received = 0, inserted = 0, updated = 0;
        foreach (var order in due)
        {
            requests++;
            var result = await finance.GetOrderFinanceAsync(context, order.OrderNumber, order.OrderedAt.AddDays(-1), now.AddDays(1), cancellationToken);
            if (!result.IsSuccess)
            {
                // What was read so far is kept; the rest waits for the next run.
                await UpsertIssueAsync(request.TenantId, FailureKey(request.ConnectionId),
                    $"Pazarama finans hareketleri {order.OrderNumber} numaralı siparişte okunamadı ({result.Error!.Code}: {result.Error.SafeMessage}); sonraki çalışmada yeniden denenecek.", now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return new(JobExecutionResult.FromAdapterError(result.Error), new(requests, received, inserted, updated, inserted + updated, 1));
            }
            foreach (var movement in result.Value!.Transactions)
            {
                received++;
                if (string.IsNullOrWhiteSpace(movement.TransactionCode)) continue;
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
        await ResolveIssueAsync(request.TenantId, FailureKey(request.ConnectionId), now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new(JobExecutionResult.Success(), new(requests, received, inserted, updated, inserted + updated, 0));
    }

    // A sale credits the seller and a refund debits it; the commission is kept as the cost to the seller (Pazarama
    // sends it negative on a sale, positive when a refund gives it back), as the payout takes it off.
    internal static void Fill(MarketplaceFinanceTransaction row, RemoteFinanceTransaction movement, string orderNumber, DateTimeOffset now)
    {
        row.TransactionType = Short(movement.Status, 200);
        row.Category = movement.Amount < 0 ? MarketplaceFinanceCategories.Return : MarketplaceFinanceCategories.Sale;
        row.TransactionDate = (movement.TransactionDate ?? now).ToUniversalTime();
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
