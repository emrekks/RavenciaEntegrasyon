using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs.Handlers;

// Reads a production Hepsiburada store's accounting records into the finance table, read only: the ones to be paid
// (WillBePaid) by their due day, from LookbackDays back to AheadDays ahead, and the paid ones (Paid) by their payment
// day over the last LookbackDays. A record is stored by its id; read again it is updated (its status turns Paid).
// Hepsiburada's own status is kept in TransactionSubType. All of a run's pages are read before anything is written.
public sealed class HepsiburadaFinanceSyncJobHandler(AppDbContext db, IHepsiburadaFinancePort finance, TimeProvider timeProvider, IConfiguration configuration) : IJobHandler
{
    internal const string Source = "HEPSIBURADA_TRANSACTIONS";
    internal const string FailureCode = "HEPSIBURADA_FINANCE_SYNC_FAILED";
    // A guard against a paging answer that never ends: 300 pages of 100.
    private const int MaximumPages = 300;
    private const int PageSize = Adapters.Hepsiburada.HepsiburadaHttpClient.FinancePageSize;
    private const int WindowDays = 30;

    public bool Handles(string jobType) => jobType == MarketplaceJobTypes.HepsiburadaFinanceSync;

    public async Task<JobOutcome> HandleAsync(JobRequest request, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == request.TenantId && x.Id == request.ConnectionId)
            .Select(x => new { x.PlatformCode, x.Environment })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null || !string.Equals(connection.PlatformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            return new(JobExecutionResult.Blocked("HEPSIBURADA_FINANCE_CONNECTION_INVALID", "Muhasebe okuması yalnız Hepsiburada bağlantısında çalışır."), new());
        if (!string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase))
            return new(JobExecutionResult.Blocked("HEPSIBURADA_FINANCE_PRODUCTION_ONLY", "Muhasebe okuması yalnız canlı (production) Hepsiburada mağazasında çalışır."), new());

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var back = now.AddDays(-Setting("LookbackDays", 60, 1, 180));
        var context = new AdapterContext(request.TenantId, request.ConnectionId, request.CorrelationId, $"hepsiburada-finance:{request.ConnectionId:N}", now.AddMinutes(10), Operation: IntegrationOperation.Automatic);
        var requests = 0;
        var records = new Dictionary<string, RemoteFinanceRecord>(StringComparer.Ordinal);
        foreach (var (status, byDueDate, start, end) in new[] { ("WillBePaid", true, back, now.AddDays(Setting("AheadDays", 90, 1, 365))), ("Paid", false, back, now.AddDays(1)) })
        {
            // A month at most per query, as the guide's example asks: consecutive windows of WindowDays calendar days.
            for (var from = start; from < end; from = from.AddDays(WindowDays))
            {
                var to = from.AddDays(WindowDays - 1) < end ? from.AddDays(WindowDays - 1) : end;
                for (var page = 0; page < MaximumPages; page++)
                {
                    requests++;
                    var result = await finance.GetFinanceRecordsAsync(context, status, byDueDate, from, to, page * PageSize, cancellationToken);
                    if (!result.IsSuccess) return await FailAsync(request, result.Error!, now, new(requests, records.Count, 0, 0, 0, 1), cancellationToken);
                    foreach (var item in result.Value!.Items) records[item.Id] = item;
                    if (result.Value.Items.Count < PageSize || (page + 1) * PageSize >= result.Value.Count) break;
                }
            }
        }

        int inserted = 0, updated = 0;
        var ids = records.Keys.ToList();
        var stored = new Dictionary<string, MarketplaceFinanceTransaction>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(1000))
            foreach (var row in await db.MarketplaceFinanceTransactions
                         .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && x.Source == Source && chunk.Contains(x.ExternalId))
                         .ToListAsync(cancellationToken))
                stored[row.ExternalId] = row;
        foreach (var record in records.Values)
        {
            if (!stored.TryGetValue(record.Id, out var row))
            {
                row = new MarketplaceFinanceTransaction
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = request.TenantId,
                    ConnectionId = request.ConnectionId,
                    PlatformCode = "HEPSIBURADA",
                    Source = Source,
                    ExternalId = Short(record.Id, 128)!,
                    RequestedType = Short(record.Category ?? record.TransactionType ?? "Record", 64)!,
                    Category = MarketplaceFinanceCategories.Other,
                    RawJson = record.RawJson,
                    FirstSeenAt = now
                };
                db.MarketplaceFinanceTransactions.Add(row);
                inserted++;
            }
            else if (row.RawJson == record.RawJson) continue;
            else updated++;
            Apply(row, record, now);
        }
        await db.SaveChangesAsync(cancellationToken);
        await ResolveIssueAsync(request.TenantId, FailureKey(request.ConnectionId), now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new(JobExecutionResult.Success(), new(requests, records.Count, inserted, updated, inserted + updated, 0));
    }

    // Income pays the seller (credit), an expense charges them (debt); a record that does not say keeps its sign.
    internal static void Apply(MarketplaceFinanceTransaction row, RemoteFinanceRecord record, DateTimeOffset now)
    {
        var amount = record.Amount ?? 0m;
        var credit = record.IsIncome switch { true => Math.Abs(amount), false => 0m, null => Math.Max(amount, 0m) };
        var debt = record.IsIncome switch { true => 0m, false => Math.Abs(amount), null => Math.Max(-amount, 0m) };
        row.TransactionType = Short(record.TransactionType, 200);
        row.TransactionSubType = Short(record.Status, 64);
        row.Category = Category(record.TransactionType);
        row.TransactionDate = (record.InvoiceDate ?? record.OrderDate ?? record.DueDate ?? now).ToUniversalTime();
        row.OrderNumber = Short(record.OrderNumber, 128);
        row.ShipmentPackageId = Short(record.PackageNumber, 128);
        row.Barcode = Short(record.Sku, 160);
        row.InvoiceNumber = Short(record.InvoiceNumber, 64);
        row.Currency = Short(record.Currency, 8);
        row.Credit = credit;
        row.Debt = debt;
        row.PaymentDate = (record.PaymentDate ?? record.DueDate)?.ToUniversalTime();
        row.RawJson = record.RawJson;
        row.UpdatedAt = now;
    }

    // The kind of a record by Hepsiburada's documented types (Payment, Return, Commission...); OTHER otherwise.
    internal static string Category(string? type) => type switch
    {
        "Payment" => MarketplaceFinanceCategories.Sale,
        "Return" => MarketplaceFinanceCategories.Return,
        "Commission" or "CommissionRefund" or "CommissionInvoiceRefund" or "CommissionCorrection" => MarketplaceFinanceCategories.Commission,
        "ShipmentCostSharingExpense" or "ShipmentCostSharingIncome" or "ReturnShipmentCostSharingExpense" or "DropShipmentCostSharingExpense" or "DropShipmentCostSharingIncome" or "CargoCostRefund"
            => MarketplaceFinanceCategories.Cargo,
        "ProcessingFeeExpense" or "ProcessingFeeExpenseRefund" or "DeliveryProcessingFee" or "DeliveryProcessingFeeRefund" or "ReturnDeliveryProcessingFee" => MarketplaceFinanceCategories.ServiceFee,
        "AdSharingExpense" or "AdSharingExpenseRefund" or "MarketingExpense" or "MarketingExpenseRefund" => MarketplaceFinanceCategories.Advertising,
        "Stoppage" => MarketplaceFinanceCategories.Stoppage,
        "CampaignDiscount" or "CampaignDiscountRefund" => MarketplaceFinanceCategories.Discount,
        _ => MarketplaceFinanceCategories.Other
    };

    internal static string FailureKey(Guid connectionId) => $"hepsiburada-finance:{connectionId:N}";

    private async Task<JobOutcome> FailAsync(JobRequest request, AdapterError error, DateTimeOffset now, JobTelemetry telemetry, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var key = FailureKey(request.ConnectionId);
        var summary = Short($"Hepsiburada muhasebe kayıtları okunamadı ({error.Code}: {error.SafeMessage}); hiçbir kayıt yazılmadı, sonraki çalışmada yeniden denenecek.", 1000)!;
        var issue = await db.OperationalIssues.SingleOrDefaultAsync(item => item.TenantId == request.TenantId && item.DedupeKey == key, cancellationToken);
        if (issue is null)
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = request.TenantId, DedupeKey = key, Code = FailureCode, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
        else
        {
            issue.Code = FailureCode;
            issue.Summary = summary;
            issue.Status = IssueStatus.Open;
            issue.LastSeenAt = now;
            issue.OccurrenceCount++;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(JobExecutionResult.FromAdapterError(error), telemetry);
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
        Math.Clamp(configuration.GetValue($"MarketplaceSync:HepsiburadaFinance:{name}", fallback), minimum, maximum);

    private static string? Short(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
