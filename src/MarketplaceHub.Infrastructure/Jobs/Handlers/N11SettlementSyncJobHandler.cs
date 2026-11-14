using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs.Handlers;

// Reads a production n11 store's settlements (hakediş) into the finance table, read only: the settlement days of the
// last LookbackDays and the coming AheadDays, then each day's order item lines. A day is read again while it is recent
// (its lines and status still change) or until it was read once; DaysPerRun older days a slice, the rest continue
// (PlanDays). Days older than LookbackDays are read once, back to the store's oldest order (BackfillAsync).
public sealed class N11SettlementSyncJobHandler(AppDbContext db, IN11SettlementPort settlements, TimeProvider timeProvider, IConfiguration configuration) : IJobHandler
{
    internal const string Source = "N11_SETTLEMENT";
    internal const string RequestedType = "SettlementDetail";
    internal const string FailureCode = "N11_SETTLEMENT_SYNC_FAILED";
    // The cursor of the one-off read of older settlement days (BackfillAsync).
    internal const string BackfillResource = "N11_SETTLEMENT_BACKFILL";
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
        var tally = new Tally();
        var lookback = today.AddDays(-Setting("LookbackDays", 45, 1, 180));

        var days = await ListDaysAsync(context, lookback, today.AddDays(Setting("AheadDays", 30, 0, 90)), tally, cancellationToken);
        if (days.Error is not null) return await FailAsync(request, days.Error, now, tally.Telemetry(1), cancellationToken);

        // The days read before, by the day their lines are due; a recent one is read again.
        var read = await ReadDaysAsync(request, cancellationToken);
        var recent = today.AddDays(-Setting("RereadDays", 7, 0, 30));
        var plan = PlanDays(days.Days, read, recent, Setting("DaysPerRun", 10, 1, 60));
        var olderStored = 0;
        foreach (var day in plan.Recent.Concat(plan.Older))
        {
            var stored = await ReadDayAsync(request, context, day, days.Days.FirstOrDefault(x => x.SettlementDate == day)?.Status, now, tally, cancellationToken);
            if (stored.Error is not null) return await FailAsync(request, stored.Error, now, tally.Telemetry(1), cancellationToken);
            if (day < recent && stored.Lines > 0) olderStored++;
        }
        // Only unread older days carry a run on, and only while a slice stores one of them: a day that answers without
        // lines stays unread, and continuing on it would read the same days forever.
        if (plan.MoreOlder && olderStored > 0)
            return new(JobExecutionResult.Continue("N11 hakediş günleri okunuyor; sonraki günler birkaç saniye içinde okunacak."), tally.Telemetry(0));
        if (await BackfillAsync(request, context, lookback, read, now, tally, cancellationToken))
            return new(JobExecutionResult.Continue("N11 geçmiş hakediş günleri okunuyor; sonraki dilim birkaç saniye içinde okunacak."), tally.Telemetry(0));
        await ResolveIssueAsync(request.TenantId, FailureKey(request.ConnectionId), now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new(JobExecutionResult.Success(), tally.Telemetry(0));
    }

    // The one-off read of the settlement days older than the regular list (a new store's history, or what a reset
    // deleted: 15 Nov 2026 the August orders' September settlements never came back), back to the store's oldest order,
    // since a settlement day always comes after its order. BackfillDays a slice, newest first. The oldest day reached is
    // kept in a cursor of its own (sync_cursors, BackfillResource), not told by the stored lines; both resets delete the
    // cursor with the lines, so the history is read again. A failure here never fails the run: the regular read stands,
    // the cursor names the error (three in a row raise the failing-read notice) and the slice is tried again next run.
    // Returns whether older days are left for another slice.
    private async Task<bool> BackfillAsync(JobRequest request, AdapterContext context, DateOnly lookback, IReadOnlySet<string> read, DateTimeOffset now, Tally tally, CancellationToken cancellationToken)
    {
        var oldestOrder = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId)
            .MinAsync(x => (DateTimeOffset?)x.OrderedAt, cancellationToken);
        var cursor = await BackfillCursorAsync(request, cancellationToken);
        var reached = cursor?.LastModifiedWatermark is { } watermark ? PayoutBuilder.Day(watermark) : (DateOnly?)null;
        if (NextBackfillWindow(reached, lookback, oldestOrder is { } oldest ? PayoutBuilder.Day(oldest) : null, Setting("BackfillDays", 45, 1, 75)) is not { } window)
            return false;

        var days = await ListDaysAsync(context, window.Start, window.End, tally, cancellationToken);
        var error = days.Error;
        if (error is null)
        {
            var listed = days.Days.Where(x => x.SettlementDate is { } day && day >= window.Start && day <= window.End)
                .GroupBy(x => x.SettlementDate!.Value).OrderByDescending(x => x.Key);
            foreach (var day in listed)
            {
                if (read.Contains(Key(day.Key))) continue;
                var stored = await ReadDayAsync(request, context, day.Key, day.First().Status, now, tally, cancellationToken);
                if (stored.Error is null) continue;
                error = stored.Error;
                break;
            }
        }
        // The days stored before a failure stay; the window is read again next run, its stored days skipped.
        if (error is not null) db.ChangeTracker.Clear();
        cursor = await BackfillCursorAsync(request, cancellationToken)
            ?? db.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = request.TenantId, ConnectionId = request.ConnectionId, ResourceType = BackfillResource, Version = 1 }).Entity;
        cursor.LastAttemptAt = now;
        if (error is not null)
        {
            cursor.LastError = Short($"{error.Code}: {error.SafeMessage}", 1000);
            cursor.LastErrorAt = now;
            cursor.ConsecutiveFailureCount++;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        cursor.LastModifiedWatermark = PayoutBuilder.DayStart(window.Start).ToUniversalTime();
        cursor.LastSuccessAt = now;
        cursor.LastSliceAt = now;
        cursor.LastError = null;
        cursor.LastErrorAt = null;
        cursor.ConsecutiveFailureCount = 0;
        cursor.LastRequestCount = tally.Requests;
        cursor.LastReceivedCount = tally.Received;
        await db.SaveChangesAsync(cancellationToken);
        return !window.Last;
    }

    private Task<SyncCursor?> BackfillCursorAsync(JobRequest request, CancellationToken cancellationToken) =>
        db.SyncCursors.SingleOrDefaultAsync(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && x.ResourceType == BackfillResource, cancellationToken);

    internal sealed record BackfillWindow(DateOnly Start, DateOnly End, bool Last);

    // The next slice of older settlement days: the windowDays before the oldest day reached (at first the regular list's
    // first day), not before the store's oldest order. Null when nothing older is left. windowDays stays within the
    // 75-day range the regular list (45 back + 30 ahead) has asked n11 for since 4 Nov 2026 without a refusal; n11's
    // SettlementService.wsdl names no longest range.
    internal static BackfillWindow? NextBackfillWindow(DateOnly? reached, DateOnly lookback, DateOnly? oldestOrder, int windowDays)
    {
        if (oldestOrder is not { } oldest) return null;
        var end = (reached ?? lookback).AddDays(-1);
        if (end < oldest) return null;
        var start = end.AddDays(-(Math.Max(1, windowDays) - 1));
        return start <= oldest ? new(oldest, end, true) : new(start, end, false);
    }

    private async Task<(List<RemoteSettlementDay> Days, AdapterError? Error)> ListDaysAsync(AdapterContext context, DateOnly start, DateOnly end, Tally tally, CancellationToken cancellationToken)
    {
        var days = new List<RemoteSettlementDay>();
        for (var page = 0; page < MaximumPages; page++)
        {
            tally.Requests++;
            var listed = await settlements.GetSettlementDaysAsync(context, start, end, page, cancellationToken);
            if (!listed.IsSuccess) return (days, listed.Error!);
            days.AddRange(listed.Value!.Items);
            if (listed.Value.Items.Count == 0 || page + 1 >= listed.Value.PageCount) break;
        }
        return (days, null);
    }

    private async Task<HashSet<string>> ReadDaysAsync(JobRequest request, CancellationToken cancellationToken) =>
        (await db.MarketplaceFinanceTransactions.AsNoTracking()
            .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && x.Source == Source)
            .Select(x => x.ExternalId)
            .ToListAsync(cancellationToken))
        .Select(id => id[(id.LastIndexOf(':') + 1)..]).ToHashSet(StringComparer.Ordinal);

    // Reads one settlement day's lines and stores them in one save. Lines: how many lines n11 gave for the day.
    private async Task<(int Lines, AdapterError? Error)> ReadDayAsync(JobRequest request, AdapterContext context, DateOnly day, string? status, DateTimeOffset now, Tally tally, CancellationToken cancellationToken)
    {
        var lines = new List<RemoteSettlementItem>();
        var skipped = new List<RemoteSkippedFinanceRecord>();
        for (var page = 0; page < MaximumPages; page++)
        {
            tally.Requests++;
            var detail = await settlements.GetSettlementItemsAsync(context, day, page, cancellationToken);
            if (!detail.IsSuccess) return (0, detail.Error!);
            lines.AddRange(detail.Value!.Items);
            // A line is stored by its id and day; a skipped one is named the same way.
            skipped.AddRange(detail.Value.Skipped.Select(x => x.Key.StartsWith('#') ? x : x with { Key = ExternalId(x.Key, day) }));
            status = detail.Value.Status ?? status;
            if (detail.Value.Items.Count + detail.Value.Skipped.Count == 0 || page + 1 >= detail.Value.PageCount) break;
        }
        tally.Received += lines.Count + skipped.Count;
        var records = Records(lines, day);
        var ids = records.Select(x => x.Id).ToList();
        await FinanceRecordIssues.RecordAsync(db, request.TenantId, request.ConnectionId, "N11", skipped, ids, now, cancellationToken);
        var stored = await db.MarketplaceFinanceTransactions
            .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && x.Source == Source && ids.Contains(x.ExternalId))
            .ToDictionaryAsync(x => x.ExternalId, StringComparer.Ordinal, cancellationToken);
        foreach (var (id, line) in records)
        {
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
                tally.Inserted++;
                request.Reads?.Add("FINANCE", new JobReadRecord(id, null, status, JobReadOutcomes.New));
            }
            // A row stored before its rate had a field of its own (CommissionRate null) is written again.
            else if (row.RawJson == line.RawJson && row.TransactionType == Short(status, 200) && row.CommissionRate is not null)
            {
                request.Reads?.Add("FINANCE", new JobReadRecord(id, row.OrderNumber, status, JobReadOutcomes.Unchanged));
                continue;
            }
            else
            {
                tally.Updated++;
                request.Reads?.Add("FINANCE", new JobReadRecord(id, row.OrderNumber, status, JobReadOutcomes.Updated));
            }
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
        return (lines.Count, null);
    }

    private sealed class Tally
    {
        public int Requests { get; set; }
        public int Received { get; set; }
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public JobTelemetry Telemetry(int failed) => new(Requests, Received, Inserted, Updated, Inserted + Updated, failed);
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

    internal sealed record SettlementDayPlan(IReadOnlyList<DateOnly> Recent, IReadOnlyList<DateOnly> Older, bool MoreOlder);

    // Recent and coming days (lines and status still change) are read on every run, all of them; older days only until
    // they were read once, perRun of them a slice, newest first. Before 6 Nov 2026 both shared one perRun list sorted
    // newest first, so ten or more recent and coming days filled every slice, the run continued on the same days
    // forever and no older day was ever read.
    internal static SettlementDayPlan PlanDays(IEnumerable<RemoteSettlementDay> days, IReadOnlySet<string> read, DateOnly recent, int perRun)
    {
        var listed = days.Where(x => x.SettlementDate is not null).Select(x => x.SettlementDate!.Value).Distinct().OrderByDescending(x => x).ToList();
        var older = listed.Where(day => day < recent && !read.Contains(Key(day))).ToList();
        return new(listed.Where(day => day >= recent).ToList(), older.Take(perRun).ToList(), older.Count > perRun);
    }

    // A day's lines by the id each is stored under. A sale and a return of the same order item on one day are two records
    // (a return line pays a negative settlement amount): both are kept, so the day nets to what n11 pays. Only a line
    // listed twice under the same id is one record.
    internal static IReadOnlyList<(string Id, RemoteSettlementItem Line)> Records(IEnumerable<RemoteSettlementItem> lines, DateOnly day) =>
        lines.Select(line => (Id: ExternalId(line.OrderItemId, day, line.SettlementAmount < 0), Line: line)).DistinctBy(x => x.Id, StringComparer.Ordinal).ToList();

    // The day stays after the last ':' (the days read before are told by it); a return line carries its kind before it.
    internal static string ExternalId(string orderItemId, DateOnly day, bool isReturn = false) =>
        isReturn ? $"{Short(orderItemId, 100)}:RETURN:{Key(day)}" : $"{Short(orderItemId, 100)}:{Key(day)}";

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
