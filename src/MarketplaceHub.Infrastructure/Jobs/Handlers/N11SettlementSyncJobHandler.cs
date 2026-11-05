using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs.Handlers;

// Reads a production n11 store's settlements (hakediş) into the finance table, read only: the settlement days of the
// last LookbackDays and the coming AheadDays, then each day's order item lines. A day is read again while it is recent
// (its lines and status still change) or until it was read once; DaysPerRun days a slice, the rest continue.
public sealed class N11SettlementSyncJobHandler(AppDbContext db, IN11SettlementPort settlements, TimeProvider timeProvider, IConfiguration configuration) : IJobHandler
{
    internal const string Source = "N11_SETTLEMENT";
    internal const string RequestedType = "SettlementDetail";
    internal const string FailureCode = "N11_SETTLEMENT_SYNC_FAILED";
    // A guard against a paging answer that never ends.
    private const int MaximumPages = 100;

    public bool Handles(string jobType) => jobType == MarketplaceJobTypes.N11SettlementSync;

    public async Task<JobOutcome> HandleAsync(JobRequest request, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == request.TenantId && x.Id == request.ConnectionId)
            .Select(x => new { x.PlatformCode, x.Environment })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null || !string.Equals(connection.PlatformCode, "N11", StringComparison.OrdinalIgnoreCase))
            return new(JobExecutionResult.Blocked("N11_SETTLEMENT_CONNECTION_INVALID", "Hakediş okuması yalnız N11 bağlantısında çalışır."), new());
        if (!string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase))
            return new(JobExecutionResult.Blocked("N11_SETTLEMENT_PRODUCTION_ONLY", "Hakediş okuması yalnız canlı (production) N11 mağazasında çalışır."), new());

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var today = PayoutBuilder.Day(now);
        var context = new AdapterContext(request.TenantId, request.ConnectionId, request.CorrelationId, $"n11-settlement:{request.ConnectionId:N}", now.AddMinutes(10), Operation: IntegrationOperation.Automatic);
        int requests = 0, received = 0, inserted = 0, updated = 0;

        var days = new List<RemoteSettlementDay>();
        for (var page = 0; page < MaximumPages; page++)
        {
            requests++;
            var listed = await settlements.GetSettlementDaysAsync(context, today.AddDays(-Setting("LookbackDays", 45, 1, 180)), today.AddDays(Setting("AheadDays", 30, 0, 90)), page, cancellationToken);
            if (!listed.IsSuccess) return await FailAsync(request, listed.Error!, now, new(requests, received, inserted, updated, inserted + updated, 1), cancellationToken);
            days.AddRange(listed.Value!.Items);
            if (listed.Value.Items.Count == 0 || page + 1 >= listed.Value.PageCount) break;
        }

        // The days read before, by the day their lines are due; a recent one is read again.
        var read = (await db.MarketplaceFinanceTransactions.AsNoTracking()
                .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && x.Source == Source)
                .Select(x => x.ExternalId)
                .ToListAsync(cancellationToken))
            .Select(id => id[(id.LastIndexOf(':') + 1)..]).ToHashSet(StringComparer.Ordinal);
        var recent = today.AddDays(-Setting("RereadDays", 7, 0, 30));
        var due = days.Where(x => x.SettlementDate is { } day && (day >= recent || !read.Contains(Key(day))))
            .Select(x => x.SettlementDate!.Value).Distinct().OrderByDescending(x => x).ToList();
        var perRun = Setting("DaysPerRun", 10, 1, 60);
        foreach (var day in due.Take(perRun))
        {
            var lines = new List<RemoteSettlementItem>();
            var skipped = new List<RemoteSkippedFinanceRecord>();
            string? status = days.FirstOrDefault(x => x.SettlementDate == day)?.Status;
            for (var page = 0; page < MaximumPages; page++)
            {
                requests++;
                var detail = await settlements.GetSettlementItemsAsync(context, day, page, cancellationToken);
                if (!detail.IsSuccess) return await FailAsync(request, detail.Error!, now, new(requests, received, inserted, updated, inserted + updated, 1), cancellationToken);
                lines.AddRange(detail.Value!.Items);
                // A line is stored by its id and day; a skipped one is named the same way.
                skipped.AddRange(detail.Value.Skipped.Select(x => x.Key.StartsWith('#') ? x : x with { Key = ExternalId(x.Key, day) }));
                status = detail.Value.Status ?? status;
                if (detail.Value.Items.Count + detail.Value.Skipped.Count == 0 || page + 1 >= detail.Value.PageCount) break;
            }
            received += lines.Count + skipped.Count;
            var ids = lines.Select(x => ExternalId(x.OrderItemId, day)).ToList();
            await FinanceRecordIssues.RecordAsync(db, request.TenantId, request.ConnectionId, "n11", skipped, ids, now, cancellationToken);
            var stored = await db.MarketplaceFinanceTransactions
                .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && x.Source == Source && ids.Contains(x.ExternalId))
                .ToDictionaryAsync(x => x.ExternalId, StringComparer.Ordinal, cancellationToken);
            foreach (var line in lines.DistinctBy(x => x.OrderItemId))
            {
                var id = ExternalId(line.OrderItemId, day);
                if (!stored.TryGetValue(id, out var row))
                {
                    row = new MarketplaceFinanceTransaction
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = request.TenantId,
                        ConnectionId = request.ConnectionId,
                        PlatformCode = "N11",
                        Source = Source,
                        ExternalId = id,
                        RequestedType = RequestedType,
                        Category = MarketplaceFinanceCategories.Sale,
                        RawJson = line.RawJson,
                        FirstSeenAt = now
                    };
                    db.MarketplaceFinanceTransactions.Add(row);
                    inserted++;
                }
                // A row stored before its rate had a field of its own (CommissionRate null) is written again.
                else if (row.RawJson == line.RawJson && row.TransactionType == Short(status, 200) && row.CommissionRate is not null) continue;
                else updated++;
                // What the line pays: n11's settlement amount; the debt is what n11 kept back from the line's payment.
                row.TransactionType = Short(status, 200);
                row.Category = line.SettlementAmount < 0 ? MarketplaceFinanceCategories.Return : MarketplaceFinanceCategories.Sale;
                row.TransactionDate = (line.CompletedAt ?? PayoutBuilder.DayStart(day)).ToUniversalTime();
                row.OrderNumber = Short(line.OrderNumber, 128);
                row.Credit = line.PaymentAmount;
                row.Debt = line.DeductionAmount;
                row.CommissionRate = line.Commission;
                row.CommissionAmount = Commission(line.Commission, null, line.PaymentAmount, row.Category);
                row.SellerRevenue = line.SettlementAmount;
                row.PaymentDate = PayoutBuilder.DayStart(line.RemittanceDate ?? day);
                row.RawJson = line.RawJson;
                row.UpdatedAt = now;
            }
            // One save per day: its lines together.
            await db.SaveChangesAsync(cancellationToken);
        }
        var telemetry = new JobTelemetry(requests, received, inserted, updated, inserted + updated, 0);
        if (due.Count > perRun)
            return new(JobExecutionResult.Continue("N11 hakediş günleri okunuyor; sonraki günler birkaç saniye içinde okunacak."), telemetry);
        await ResolveIssueAsync(request.TenantId, FailureKey(request.ConnectionId), now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new(JobExecutionResult.Success(), telemetry);
    }

    // n11's settlement "commission" is a rate in per cent, not an amount (live 5 Nov 2026: "20.34" on every line, the
    // order line's commissionRate too; deductionAmount 185.76 = 799 × 20.34 % (162.52) + ad fee 7.99 + its VAT 1.60 +
    // the service fee). The line's commission is its paid amount at that rate, as a cost to the seller: charged on a sale
    // line, given back on a return line. Rows stored before 5 Nov 2026 hold the rate in CommissionAmount and no
    // CommissionRate; legacyAmount takes that rate. Null when no rate is known.
    internal static decimal? Commission(decimal? rate, decimal? legacyAmount, decimal paymentAmount, string category)
    {
        if ((rate ?? legacyAmount) is not { } percent) return null;
        var amount = Math.Round(Math.Abs(paymentAmount) * Math.Abs(percent) / 100m, 2, MidpointRounding.AwayFromZero);
        return category == MarketplaceFinanceCategories.Return ? -amount : amount;
    }

    internal static string ExternalId(string orderItemId, DateOnly day) => $"{Short(orderItemId, 100)}:{Key(day)}";

    private static string Key(DateOnly day) => day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    internal static string FailureKey(Guid connectionId) => $"n11-settlement:{connectionId:N}";

    private async Task<JobOutcome> FailAsync(JobRequest request, AdapterError error, DateTimeOffset now, JobTelemetry telemetry, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var key = FailureKey(request.ConnectionId);
        var summary = Short($"N11 hakediş kayıtları okunamadı ({error.Code}: {error.SafeMessage}); okunan günler kaydedildi, kalanlar sonraki çalışmada yeniden denenecek.", 1000)!;
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
        Math.Clamp(configuration.GetValue($"MarketplaceSync:N11Settlement:{name}", fallback), minimum, maximum);

    private static string? Short(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
