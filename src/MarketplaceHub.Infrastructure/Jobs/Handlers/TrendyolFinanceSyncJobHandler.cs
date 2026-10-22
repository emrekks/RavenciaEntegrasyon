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
    // A guard against a paging answer that never ends; 200 pages of 500 is far beyond a 15-day window of one store.
    private const int MaximumPages = 200;

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
        var windows = range is { } dates ? TrendyolFinanceSyncPlan.Windows(dates.Start, dates.End, TrendyolFinanceRequestContract.MaximumWindow) : [];
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

        if (windows.Count == 0)
        {
            state.LastSyncedAt = now;
            state.Version++;
        }
        await ResolveIssueAsync(request.TenantId, FailureKey(request.ConnectionId), now, cancellationToken);
        if (rejected.Count == 0) await ResolveIssueAsync(request.TenantId, RejectedKey(request.ConnectionId), now, cancellationToken);
        else await UpsertIssueAsync(request.TenantId, RejectedKey(request.ConnectionId), RejectedTypesCode,
            $"Trendyol şu finans kayıt türlerini kabul etmedi; bu türlerin kayıtları okunamadı: {string.Join(", ", rejected)}.", now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new(JobExecutionResult.Success(), telemetry.ToTelemetry());
    }

    private sealed record WindowRead(List<RemoteStatementRecord> Records, Dictionary<string, List<RemoteCargoInvoiceItem>> CargoItems, AdapterError? Error);

    private async Task<WindowRead> ReadWindowAsync(JobRequest request, AdapterContext context, DateTimeOffset start, DateTimeOffset end, Counter telemetry,
        SortedSet<string> rejected, CancellationToken cancellationToken)
    {
        var records = new List<RemoteStatementRecord>();
        var cargo = new Dictionary<string, List<RemoteCargoInvoiceItem>>(StringComparer.Ordinal);
        var queries = TrendyolFinanceCategorizer.SettlementTypes.Select(type => (Source: TrendyolFinanceRequestContract.Settlements, Type: type, SubType: (string?)null))
            .Concat(TrendyolFinanceCategorizer.OtherFinancialTypes.Select(type => (Source: TrendyolFinanceRequestContract.OtherFinancials, Type: type, SubType: (string?)null)))
            // After all deduction invoices: the ones that are the platform service fee.
            .Append((Source: TrendyolFinanceRequestContract.OtherFinancials, Type: TrendyolFinanceCategorizer.DeductionInvoices, SubType: (string?)TrendyolFinanceCategorizer.PlatformServiceFee));
        foreach (var query in queries)
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
                    if (page == 0 && result.Error!.Class == AdapterErrorClass.Validation && result.Error.HttpStatus == 400)
                    {
                        rejected.Add(query.SubType is null ? query.Type : $"{query.Type}/{query.SubType}");
                        break;
                    }
                    return new(records, cargo, result.Error);
                }
                var value = result.Value!;
                records.AddRange(value.Items);
                telemetry.Received += value.Items.Count;
                if (value.Items.Count == 0 || page + 1 >= value.TotalPages) break;
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
        return new(records, cargo, null);
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
