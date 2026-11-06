using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs.Handlers;

// Reads a production Trendyol store's accounting records (current account statement) and the lines of its cargo
// invoices into the finance tables, read only. A run covers the dates TrendyolFinanceSyncPlan.Range gives, window by
// window (at most 15 days each); a window is written only once all of it was read, in one save that also moves the
// coverage, so a failure never leaves a window half stored and marked done. Long runs continue in slices.
public sealed class TrendyolFinanceSyncJobHandler(AppDbContext db, ITrendyolFinancePort finance, TimeProvider timeProvider, IConfiguration configuration) : IJobHandler
{
    internal const string FailureCode = "TRENDYOL_FINANCE_SYNC_FAILED";
    internal const string RejectedTypesCode = "TRENDYOL_FINANCE_TYPE_REJECTED";
    internal const string PayoutFailureCode = "TRENDYOL_PAYOUT_SYNC_FAILED";
    // A guard against a paging answer that never ends; 200 pages of 500 is far beyond a 15-day window of one store.
    private const int MaximumPages = 200;
    // Ten payouts a page: 400 payouts, years of weekly payouts in several regions.
    private const int MaximumPayoutPages = 40;
    private static readonly TimeSpan StatementFreshFor = TimeSpan.FromMinutes(30);

    public bool Handles(string jobType) => jobType == MarketplaceJobTypes.TrendyolFinanceSync;

    public async Task<JobOutcome> HandleAsync(JobRequest request, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == request.TenantId && x.Id == request.ConnectionId)
            .Select(x => new { x.PlatformCode, x.Environment })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null || !string.Equals(connection.PlatformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
            return new(JobExecutionResult.Blocked("TRENDYOL_FINANCE_CONNECTION_INVALID", "Finans okuması yalnız Trendyol bağlantısında çalışır."), new());
        if (!string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase))
            return new(JobExecutionResult.Blocked("TRENDYOL_FINANCE_PRODUCTION_ONLY", "Finans okuması yalnız canlı (production) Trendyol mağazasında çalışır."), new());

        var state = await db.MarketplaceFinanceSyncStates.SingleOrDefaultAsync(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId, cancellationToken);
        if (state is null)
        {
            state = new MarketplaceFinanceSyncState { TenantId = request.TenantId, ConnectionId = request.ConnectionId };
            db.MarketplaceFinanceSyncStates.Add(state);
        }
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var range = TrendyolFinanceSyncPlan.Range(state.CoveredFrom, state.CoveredThrough, state.LastSyncedAt, now,
            Setting("BackfillDays", 90, 1, 365), Setting("OverlapDays", 3, 0, 14));
        // A statement read finished minutes ago is not read again: the payout slices that follow it carry on with the payouts only.
        var fresh = state.LastSyncedAt is { } synced && now - synced < StatementFreshFor && state.CoveredThrough is { } through && through <= synced;
        var windows = range is { } dates && !fresh ? TrendyolFinanceSyncPlan.Windows(dates.Start, dates.End, TrendyolFinanceRequestContract.MaximumWindow) : [];
        var perSlice = Setting("WindowsPerSlice", 2, 1, 10);
        var telemetry = new Counter();
        var rejected = new SortedSet<string>(StringComparer.Ordinal);
        var context = new AdapterContext(request.TenantId, request.ConnectionId, request.CorrelationId, $"trendyol-finance:{request.ConnectionId:N}", now.AddMinutes(10), Operation: IntegrationOperation.Automatic);

        foreach (var window in windows.Take(perSlice))
        {
            var read = await ReadWindowAsync(request, context, window.Start, window.End, telemetry, rejected, cancellationToken);
            if (read.Error is { } error)
            {
                // Nothing of the window was written; only the failure is.
                db.ChangeTracker.Clear();
                await RecordFailureAsync(request, window, error, now, cancellationToken);
                return new(JobExecutionResult.FromAdapterError(error), telemetry.ToTelemetry(failed: 1));
            }
            var last = window == windows[^1];
            await WriteWindowAsync(request, state, window, read, last ? now : null, last ? rejected : null, telemetry, now, cancellationToken);
        }
        if (windows.Count > perSlice)
            return new(JobExecutionResult.Continue("Trendyol finans kayıtları okunuyor; sonraki tarih aralığı birkaç saniye içinde okunacak."), telemetry.ToTelemetry());

        if (windows.Count == 0 && !fresh)
        {
            state.LastSyncedAt = now;
            state.Version++;
        }

        // The statement is current; now the payouts and the records each one paid, without the types refused before.
        var skip = new SortedSet<string>(rejected, StringComparer.Ordinal);
        if (windows.Count == 0) skip.UnionWith((state.RejectedTypes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var payouts = await SyncPayoutsAsync(request, context, skip, now, telemetry, cancellationToken);
        if (payouts.Error is { } payoutError)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(JobExecutionResult.FromAdapterError(payoutError), telemetry.ToTelemetry(failed: 1));
        }
        if (payouts.Continues)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(JobExecutionResult.Continue("Trendyol ödeme kayıtları okunuyor; sonraki ödeme birkaç saniye içinde okunacak."), telemetry.ToTelemetry());
        }
        await ResolveIssueAsync(request.TenantId, FailureKey(request.ConnectionId), now, cancellationToken);
        // A run that read no statement window knows nothing new about refused types.
        if (windows.Count > 0 && rejected.Count == 0) await ResolveIssueAsync(request.TenantId, RejectedKey(request.ConnectionId), now, cancellationToken);
        else if (windows.Count > 0) await UpsertIssueAsync(request.TenantId, RejectedKey(request.ConnectionId), RejectedTypesCode,
            $"Trendyol şu finans kayıt türlerini kabul etmedi; bu türlerin kayıtları okunamadı: {string.Join(", ", rejected)}.", now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new(JobExecutionResult.Success(), telemetry.ToTelemetry());
    }

    // Every statement query, one type each: settlements, other financials, the platform service fee among the deduction
    // invoices and the money movements. `skip`: the types Trendyol refused before (a payout read does not ask again).
    private static IEnumerable<(string Source, string Type, string? SubType)> Queries(IReadOnlySet<string>? skip) =>
        TrendyolFinanceCategorizer.SettlementTypes.Select(type => (Source: TrendyolFinanceRequestContract.Settlements, Type: type, SubType: (string?)null))
            .Concat(TrendyolFinanceCategorizer.OtherFinancialTypes.Select(type => (Source: TrendyolFinanceRequestContract.OtherFinancials, Type: type, SubType: (string?)null)))
            // After all deduction invoices: the ones that are the platform service fee.
            .Append((Source: TrendyolFinanceRequestContract.OtherFinancials, Type: TrendyolFinanceCategorizer.DeductionInvoices, SubType: (string?)TrendyolFinanceCategorizer.PlatformServiceFee))
            .Concat(TrendyolFinanceCategorizer.MoneyMovementTypes.Select(type => (Source: MarketplaceFinanceSources.MoneyMovements, Type: type, SubType: (string?)null)))
            .Where(query => skip is null || !skip.Contains(query.SubType is null ? query.Type : $"{query.Type}/{query.SubType}"));

    private sealed record PayoutRead(bool Continues, AdapterError? Error);

    // Reads Trendyol's paid payouts (every page, at most MaximumPayoutPages) and stores them, then the records of the
    // recent payouts not read yet by their id: Trendyol fills the payout id in on a record only after the payout, often
    // after the statement window that read the record was closed. A payout is read a day after it was made (the
    // documentation's advice) and only within the backfill days. Two payouts a slice; the rest continue.
    // Failures that retrying cannot mend (the service refused or does not exist) open an issue and leave the statement
    // run successful; the others are retried.
    private async Task<PayoutRead> SyncPayoutsAsync(JobRequest request, AdapterContext context, IReadOnlySet<string> rejected, DateTimeOffset now, Counter telemetry, CancellationToken cancellationToken)
    {
        var remote = new List<RemotePayoutOrder>();
        for (var page = 0; page < MaximumPayoutPages; page++)
        {
            telemetry.Requests++;
            var result = await finance.GetPayoutOrdersAsync(context, page, cancellationToken);
            if (!result.IsSuccess) return await PayoutFailureAsync(request, result.Error!, now, cancellationToken);
            remote.AddRange(result.Value!.Items);
            if (result.Value.Items.Count == 0 || page + 1 >= result.Value.TotalPages) break;
        }
        var ids = remote.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).ToList();
        var stored = (await db.MarketplacePayoutOrders
                .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && ids.Contains(x.ExternalId))
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.ExternalId, StringComparer.Ordinal);
        foreach (var item in remote.DistinctBy(x => x.ExternalId))
        {
            if (!stored.TryGetValue(item.ExternalId, out var row))
            {
                row = new MarketplacePayoutOrder
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = request.TenantId,
                    ConnectionId = request.ConnectionId,
                    PlatformCode = "TRENDYOL",
                    ExternalId = Short(item.ExternalId, 64)!,
                    RawJson = item.RawJson,
                    FirstSeenAt = now,
                    UpdatedAt = now
                };
                db.MarketplacePayoutOrders.Add(row);
                stored[item.ExternalId] = row;
            }
            else if (row.RawJson == item.RawJson) continue;
            row.Status = Short(item.Status, 32);
            row.PaymentType = Short(item.PaymentType, 32);
            row.Amount = item.Amount;
            row.PayoutDate = item.PayoutDate?.ToUniversalTime();
            row.RegionCode = Short(item.RegionCode, 16);
            row.RegionName = Short(item.RegionName, 64);
            row.RawJson = item.RawJson;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);

        var readyBefore = now.AddDays(-1);
        var since = now.AddDays(-Setting("BackfillDays", 90, 1, 365));
        var pending = await db.MarketplacePayoutOrders
            .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && x.LinesReadAt == null
                && x.PayoutDate != null && x.PayoutDate <= readyBefore && x.PayoutDate >= since)
            .OrderByDescending(x => x.PayoutDate)
            .ToListAsync(cancellationToken);
        var perSlice = Setting("PayoutsPerSlice", 2, 1, 10);
        foreach (var payout in pending.Take(perSlice))
        {
            var records = new List<RemoteStatementRecord>();
            var skipped = new List<RemoteSkippedFinanceRecord>();
            // Not the service-fee filter: asked by payout id, Trendyol ignores transactionSubType and answers every deduction
            // invoice of the payout (4 Nov 2026: 95 cargo invoices came back marked as platform service fees).
            foreach (var query in Queries(rejected).Where(query => query.SubType is null))
            {
                for (var page = 0; page < MaximumPages; page++)
                {
                    telemetry.Requests++;
                    var result = await finance.GetPayoutRecordsAsync(context, query.Source, query.Type, query.SubType, payout.ExternalId, page, cancellationToken);
                    if (!result.IsSuccess)
                    {
                        if (page == 0 && result.Error!.Class == AdapterErrorClass.Validation && result.Error.HttpStatus == 400) break;
                        db.ChangeTracker.Clear();
                        return await PayoutFailureAsync(request, result.Error!, now, cancellationToken);
                    }
                    var value = result.Value!;
                    records.AddRange(query.Source == MarketplaceFinanceSources.MoneyMovements ? value.Items.Select(x => x with { Source = query.Source }) : value.Items);
                    skipped.AddRange(value.Skipped);
                    telemetry.Received += value.Items.Count + value.Skipped.Count;
                    if (value.Items.Count + value.Skipped.Count == 0 || page + 1 >= value.TotalPages) break;
                }
            }
            var collected = TrendyolFinanceSyncPlan.Collect(records);
            await FinanceRecordIssues.RecordAsync(db, request.TenantId, request.ConnectionId, "Trendyol", skipped, records.Select(x => x.ExternalId), now, cancellationToken);
            var keys = collected.Keys.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).ToList();
            var rows = new Dictionary<(string Source, string ExternalId), MarketplaceFinanceTransaction>();
            foreach (var chunk in keys.Chunk(1000))
                foreach (var row in await db.MarketplaceFinanceTransactions
                             .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && chunk.Contains(x.ExternalId))
                             .ToListAsync(cancellationToken))
                    rows[(row.Source, row.ExternalId)] = row;
            var (added, updated) = TrendyolFinanceSyncPlan.Merge(rows, collected.Values, request.TenantId, request.ConnectionId, now);
            db.MarketplaceFinanceTransactions.AddRange(added);
            payout.LinesReadAt = now;
            payout.UpdatedAt = now;
            // One save per payout: its records and its read mark together.
            await db.SaveChangesAsync(cancellationToken);
            telemetry.Inserted += added.Count;
            telemetry.Updated += updated;
        }
        await ResolveIssueAsync(request.TenantId, PayoutFailureKey(request.ConnectionId), now, cancellationToken);
        return new(pending.Count > perSlice, null);
    }

    private async Task<PayoutRead> PayoutFailureAsync(JobRequest request, AdapterError error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await UpsertIssueAsync(request.TenantId, PayoutFailureKey(request.ConnectionId), PayoutFailureCode,
            $"Trendyol ödeme kayıtları okunamadı ({error.Code}: {error.SafeMessage}); cari hesap kayıtları okundu, ödemeler sonraki çalışmada yeniden denenecek.", now, cancellationToken);
        var lasting = error.Class is AdapterErrorClass.Validation or AdapterErrorClass.NotFound or AdapterErrorClass.NotSupported or AdapterErrorClass.Authentication;
        return new(false, lasting ? null : error);
    }

    private sealed record WindowRead(List<RemoteStatementRecord> Records, Dictionary<string, List<RemoteCargoInvoiceItem>> CargoItems, AdapterError? Error)
    {
        // The records the pages sent without an id, a main amount or a date: not stored, named in issues.
        public List<RemoteSkippedFinanceRecord> Skipped { get; init; } = [];
    }

    private Task<bool> HasRecordsOfTypeAsync(JobRequest request, string type, CancellationToken cancellationToken) =>
        db.MarketplaceFinanceTransactions.AsNoTracking()
            .AnyAsync(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && x.PlatformCode == "TRENDYOL" && x.RequestedType == type, cancellationToken);

    private async Task<WindowRead> ReadWindowAsync(JobRequest request, AdapterContext context, DateTimeOffset start, DateTimeOffset end, Counter telemetry,
        SortedSet<string> rejected, CancellationToken cancellationToken)
    {
        var skipped = new List<RemoteSkippedFinanceRecord>();
        var records = new List<RemoteStatementRecord>();
        var cargo = new Dictionary<string, List<RemoteCargoInvoiceItem>>(StringComparer.Ordinal);
        foreach (var query in Queries(null))
        {
            for (var page = 0; page < MaximumPages; page++)
            {
                telemetry.Requests++;
                var result = query.Source == TrendyolFinanceRequestContract.Settlements
                    ? await finance.GetSettlementsAsync(context, query.Type, start, end, page, cancellationToken)
                    : await finance.GetOtherFinancialsAsync(context, query.Type, query.SubType, start, end, page, cancellationToken);
                if (!result.IsSuccess)
                {
                    // A type (or the service-fee filter) Trendyol does not take is skipped and reported; it cannot get
                    // better by retrying. Any other failure leaves the whole window unread.
                    // A type the connection already has records of was taken before: its 400 is this window's failure,
                    // not a refusal of the type (before 6 Nov 2026 the window was written as read without that type,
                    // and the next run started three days before its end, so the type's records there were never read).
                    if (page == 0 && result.Error!.Class == AdapterErrorClass.Validation && result.Error.HttpStatus == 400
                        && !(query.SubType is null && await HasRecordsOfTypeAsync(request, query.Type, cancellationToken)))
                    {
                        rejected.Add(query.SubType is null ? query.Type : $"{query.Type}/{query.SubType}");
                        break;
                    }
                    return new(records, cargo, result.Error);
                }
                var value = result.Value!;
                records.AddRange(query.Source == MarketplaceFinanceSources.MoneyMovements ? value.Items.Select(x => x with { Source = query.Source }) : value.Items);
                skipped.AddRange(value.Skipped);
                telemetry.Received += value.Items.Count + value.Skipped.Count;
                if (value.Items.Count + value.Skipped.Count == 0 || page + 1 >= value.TotalPages) break;
            }
        }

        // The lines of the cargo invoices not read before (an invoice's lines do not change once issued).
        var serials = records
            .Where(x => TrendyolFinanceCategorizer.IsCargoInvoice(x.Source, x.RequestedType, TrendyolFinanceCategorizer.Categorize(x.Source, x.RequestedType, x.TransactionSubType, x.TransactionType, x.Description)))
            .Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).ToList();
        if (serials.Count > 0)
        {
            var known = await db.MarketplaceCargoInvoiceItems.AsNoTracking()
                .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && serials.Contains(x.InvoiceSerialNumber))
                .Select(x => x.InvoiceSerialNumber).Distinct().ToListAsync(cancellationToken);
            foreach (var serial in serials.Except(known, StringComparer.Ordinal))
            {
                var items = new List<RemoteCargoInvoiceItem>();
                for (var page = 0; page < MaximumPages; page++)
                {
                    telemetry.Requests++;
                    var result = await finance.GetCargoInvoiceItemsAsync(context, serial, page, cancellationToken);
                    if (!result.IsSuccess)
                    {
                        // No lines for this serial: the invoice stays a store-level cost by its date.
                        if (result.Error!.Class == AdapterErrorClass.NotFound) break;
                        return new(records, cargo, result.Error);
                    }
                    items.AddRange(result.Value!.Items);
                    if (result.Value.Items.Count == 0 || page + 1 >= result.Value.TotalPages) break;
                }
                cargo[serial] = items;
            }
        }
        return new(records, cargo, null) { Skipped = skipped };
    }

    private async Task WriteWindowAsync(JobRequest request, MarketplaceFinanceSyncState state, (DateTimeOffset Start, DateTimeOffset End) window, WindowRead read,
        DateTimeOffset? finishedAt, SortedSet<string>? rejected, Counter telemetry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var collected = TrendyolFinanceSyncPlan.Collect(read.Records);
        var ids = collected.Keys.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).ToList();
        var stored = new Dictionary<(string Source, string ExternalId), MarketplaceFinanceTransaction>();
        foreach (var chunk in ids.Chunk(1000))
            foreach (var row in await db.MarketplaceFinanceTransactions
                         .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && chunk.Contains(x.ExternalId))
                         .ToListAsync(cancellationToken))
                stored[(row.Source, row.ExternalId)] = row;
        var (added, updated) = TrendyolFinanceSyncPlan.Merge(stored, collected.Values, request.TenantId, request.ConnectionId, now);
        db.MarketplaceFinanceTransactions.AddRange(added);
        await FinanceRecordIssues.RecordAsync(db, request.TenantId, request.ConnectionId, "Trendyol", read.Skipped, read.Records.Select(x => x.ExternalId), now, cancellationToken);

        if (read.CargoItems.Count > 0)
        {
            var serials = read.CargoItems.Keys.ToList();
            db.MarketplaceCargoInvoiceItems.RemoveRange(await db.MarketplaceCargoInvoiceItems
                .Where(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId && serials.Contains(x.InvoiceSerialNumber))
                .ToListAsync(cancellationToken));
            foreach (var (serial, items) in read.CargoItems)
                db.MarketplaceCargoInvoiceItems.AddRange(items.Select((item, index) => new MarketplaceCargoInvoiceItem
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = request.TenantId,
                    ConnectionId = request.ConnectionId,
                    InvoiceSerialNumber = serial,
                    LineNo = index,
                    ShipmentPackageType = Short(item.ShipmentPackageType, 120),
                    ParcelUniqueId = Short(item.ParcelUniqueId, 64),
                    OrderNumber = Short(item.OrderNumber, 128),
                    Amount = item.Amount,
                    Desi = item.Desi,
                    RawJson = item.RawJson,
                    FetchedAt = now
                }));
        }

        state.CoveredFrom = state.CoveredFrom is { } from && from < window.Start ? from : window.Start;
        state.CoveredThrough = state.CoveredThrough is { } through && through > window.End ? through : window.End;
        if (finishedAt is { } finished)
        {
            state.LastSyncedAt = finished;
            state.RejectedTypes = rejected is { Count: > 0 } ? Short(string.Join(",", rejected), 1000) : null;
            state.LastError = null;
            state.LastErrorAt = null;
        }
        state.Version++;
        // One save: the records, the cargo lines and the coverage are stored together or not at all.
        await db.SaveChangesAsync(cancellationToken);
        telemetry.Inserted += added.Count;
        telemetry.Updated += updated;
    }

    private async Task RecordFailureAsync(JobRequest request, (DateTimeOffset Start, DateTimeOffset End) window, AdapterError error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await db.MarketplaceFinanceSyncStates.SingleOrDefaultAsync(x => x.TenantId == request.TenantId && x.ConnectionId == request.ConnectionId, cancellationToken);
        if (state is null)
        {
            state = new MarketplaceFinanceSyncState { TenantId = request.TenantId, ConnectionId = request.ConnectionId };
            db.MarketplaceFinanceSyncStates.Add(state);
        }
        var summary = $"Trendyol finans kayıtları {window.Start:yyyy-MM-dd}–{window.End:yyyy-MM-dd} aralığı için okunamadı ({error.Code}: {error.SafeMessage}); bu aralık kaydedilmedi, sonraki çalışmada yeniden denenecek.";
        state.LastError = Short(summary, 1024);
        state.LastErrorAt = now;
        state.Version++;
        await UpsertIssueAsync(request.TenantId, FailureKey(request.ConnectionId), FailureCode, summary, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static string FailureKey(Guid connectionId) => $"trendyol-finance:{connectionId:N}";
    internal static string RejectedKey(Guid connectionId) => $"trendyol-finance-types:{connectionId:N}";
    internal static string PayoutFailureKey(Guid connectionId) => $"trendyol-payouts:{connectionId:N}";

    private async Task UpsertIssueAsync(Guid tenantId, string key, string code, string summary, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var issue = await db.OperationalIssues.SingleOrDefaultAsync(item => item.TenantId == tenantId && item.DedupeKey == key, cancellationToken);
        if (issue is null)
        {
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = Short(summary, 1000)!, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            return;
        }
        issue.Code = code;
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
        Math.Clamp(configuration.GetValue($"MarketplaceSync:TrendyolFinance:{name}", fallback), minimum, maximum);

    private static string? Short(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

    private sealed class Counter
    {
        public int Requests;
        public int Received;
        public int Inserted;
        public int Updated;

        public JobTelemetry ToTelemetry(int failed = 0) => new(Requests, Received, Inserted, Updated, Inserted + Updated, failed);
    }
}
