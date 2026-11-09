using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MarketplaceHub.Infrastructure.Persistence;

// The sales reports already worked out, per tenant and query, in the API's memory (ReportsService.SalesAsync). A period
// that reaches today is kept for OpenLifetime only: its orders move all day, and the panel itself re-reads a report a
// minute old. A closed period is kept up to ClosedLifetime and is checked against the tenant's change events
// (integration.outbox_events) before each use: a change to an order, package, return claim or invoice of the report's
// days (the previous period's too), a question that may have been asked in it, a product, stock, store or other data
// change, or a finished marketplace finance read (whose records raise no events of their own) drops it. The entry's
// time is taken before its reads began, so a change made while it was being worked out drops it too.
public sealed class ReportCache
{
    public static readonly TimeSpan OpenLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ClosedLifetime = TimeSpan.FromHours(1);
    // A few dozen reports of a few MB each at most; the oldest go first.
    private const int Entries = 48;

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = Entries });

    // The aggregates whose events say nothing to a report on their own: a job's state (a finance read is told by its
    // job type below), and those checked against the report's days. Every other data change drops the entry.
    private static readonly string[] Checked = ["IntegrationJob", "Order", "ShipmentPackage", "ReturnClaim", "Invoice", "MarketplaceQuestion",
        "ManualOrderInvoiceDocument", "MarketplaceInvoiceReferenceHistory", DashboardSnapshotRefreshPolicy.SnapshotAggregateType];

    // The jobs that write the marketplaces' finance records and cargo invoices (their rows raise no events of their own),
    // as their state change event names them (JobLeaseService.NewJobEvent).
    private static readonly string[] FinanceReads = [.. new[] { MarketplaceJobTypes.TrendyolFinanceSync, MarketplaceJobTypes.HepsiburadaFinanceSync, MarketplaceJobTypes.N11SettlementSync, MarketplaceJobTypes.PazaramaFinanceSync }
        .Select(jobType => $$"""{"jobType":"{{jobType}}"}""")];

    public sealed record Entry(ReportSalesView View, DateTimeOffset ComputedAt, DateTimeOffset Start, DateTimeOffset End, bool Closed);

    public static string Key(Guid tenantId, string timezone, DateOnly from, DateOnly to, string granularity, bool includeTest, IEnumerable<string>? platforms, IEnumerable<Guid>? connections) =>
        string.Join('|', "sales", tenantId.ToString("N"), timezone, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), granularity, includeTest ? "1" : "0",
            string.Join(',', (platforms ?? []).Order(StringComparer.Ordinal)), string.Join(',', (connections ?? []).Order()));

    // The entry still good for the tenant now, else null (and it is dropped).
    public async Task<ReportSalesView?> GetAsync(AppDbContext db, Guid tenantId, string key, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(key, out Entry? entry) || entry is null) return null;
        if (!entry.Closed) return entry.View;
        if (!await ChangedSinceAsync(db, tenantId, entry, cancellationToken)) return entry.View;
        cache.Remove(key);
        return null;
    }

    public void Set(string key, Entry entry) =>
        cache.Set(key, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = entry.Closed ? ClosedLifetime : OpenLifetime });

    // Whether a change since the entry was worked out can move its figures (see the class comment). The time is written
    // into the SQL as a constant so the (TenantId, CreatedAt) index reads only the events since (DashboardSnapshotRefreshPolicy).
    internal static Task<bool> ChangedSinceAsync(AppDbContext db, Guid tenantId, Entry entry, CancellationToken cancellationToken)
    {
        var start = entry.Start;
        var end = entry.End;
        // A question asked in the period carries a creation time before its end (a day's slack for the marketplace's clock).
        var asked = end.AddDays(1);
        var quiet = Checked;
        var inPeriod = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId && order.OrderedAt >= start && order.OrderedAt < end).Select(order => order.Id);
        return db.IntegrationOutboxEvents.AsNoTracking()
            .Where(change => change.TenantId == tenantId && change.CreatedAt > EF.Constant(entry.ComputedAt))
            .AnyAsync(change =>
                (change.AggregateType == "IntegrationJob" && (EF.Functions.JsonContains(change.PayloadJson, FinanceReads[0]) || EF.Functions.JsonContains(change.PayloadJson, FinanceReads[1])
                    || EF.Functions.JsonContains(change.PayloadJson, FinanceReads[2]) || EF.Functions.JsonContains(change.PayloadJson, FinanceReads[3])))
                || (change.AggregateType == "Order" && inPeriod.Contains(change.AggregateId!.Value))
                || (change.AggregateType == "ShipmentPackage" && db.ShipmentPackages.Any(package => package.TenantId == tenantId && package.Id == change.AggregateId && inPeriod.Contains(package.OrderId)))
                || (change.AggregateType == "ReturnClaim" && db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.Id == change.AggregateId && inPeriod.Contains(claim.OrderId)))
                || (change.AggregateType == "Invoice" && db.Invoices.Any(invoice => invoice.TenantId == tenantId && invoice.Id == change.AggregateId && inPeriod.Contains(invoice.OrderId)))
                || (change.AggregateType == "MarketplaceQuestion" && db.MarketplaceQuestions.Any(question => question.TenantId == tenantId && question.Id == change.AggregateId
                    && (question.CreatedAt == null || question.CreatedAt < asked)))
                || !quiet.Contains(change.AggregateType), cancellationToken);
    }
}
