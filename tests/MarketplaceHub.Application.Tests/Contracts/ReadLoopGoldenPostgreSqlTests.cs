using System.Globalization;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 3e (16 Nov 2026): the safety net for moving the order and return read loops out of the job processor.
// These loops decide what is asked of the marketplaces, so each run records the sequence of port calls (method, the
// idempotency key, the window, the page cursor and size), the job's result and the sync cursors it left, on a fixed
// clock. Four scenarios per marketplace and read: a first read (two pages), a resumed read (the second page fails, the
// next run resumes), a page-cap stop (a marketplace that always says "more") and an empty page (a second read ten minutes
// after a completed one gets an empty last page). The goldens must not change while the code moves.
[Collection("postgres-integration")]
public sealed class ReadLoopGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    public enum Scenario { First, Resumed, PageCap, Empty }

    public static TheoryData<string, string, Scenario> Reads()
    {
        var data = new TheoryData<string, string, Scenario>();
        foreach (var platform in new[] { "TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY" })
            foreach (var jobType in new[] { MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.ReturnStatusSync, MarketplaceJobTypes.ReturnReconciliation })
                foreach (var scenario in Enum.GetValues<Scenario>())
                    data.Add(platform, jobType, scenario);
        return data;
    }

    [PostgreSqlTheory]
    [MemberData(nameof(Reads))]
    public async Task AReadAsksTheMarketplaceTheSameThings(string platform, string sharedJobType, Scenario scenario)
    {
        var jobType = MarketplaceJobTypes.ForPlatform(platform, sharedJobType);
        var clock = new SteppingClock(Start);
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"loop-{Guid.NewGuid():N}"[..30], DisplayName = "loop golden", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = Start, UpdatedAt = Start, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = platform, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            await seed.SaveChangesAsync();
        }

        var runs = new List<object>();
        async Task RunAsync(string label, Pager pager)
        {
            var port = new RecordingPorts(pager);
            JobExecutionResult result;
            await using (var db = fixture.CreateContext())
            {
                var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, port, port, port, port, null!, null!, new ConfigurationBuilder().Build(), clock);
                result = await processor.ProcessAsync(tenant.Id, connection.Id, jobType, "{}", $"loop-golden-{label}", CancellationToken.None);
            }
            runs.Add(new { Run = label, Result = result.Kind.ToString(), result.ErrorCode, Calls = Summarize(port.Calls) });
        }

        switch (scenario)
        {
            case Scenario.First:
                await RunAsync("first", Pager.TwoPages);
                break;
            case Scenario.Resumed:
                await RunAsync("failing", Pager.SecondPageFails);
                clock.Advance(TimeSpan.FromMinutes(1));
                await RunAsync("resumed", Pager.TwoPages);
                break;
            case Scenario.PageCap:
                await RunAsync("endless", Pager.Endless);
                break;
            case Scenario.Empty:
                await RunAsync("first", Pager.TwoPages);
                clock.Advance(TimeSpan.FromMinutes(10));
                await RunAsync("empty", Pager.EmptyLastPage);
                break;
        }

        await using var check = fixture.CreateContext();
        // Ordered here, not by the database: its collation decides whether "ORDERS_HOT" or "ORDER_LINE_REMATCH" comes first
        // (C locally, en_US on CI), and the golden must not depend on that.
        var cursors = (await check.SyncCursors.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync())
            .OrderBy(x => x.ResourceType, StringComparer.Ordinal).ToList();
        var issues = await check.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Code + (x.ResolvedAt == null ? "" : " (resolved)")).ToListAsync();
        var summary = new
        {
            JobType = jobType,
            Runs = runs,
            Cursors = cursors.Select(cursor => new { cursor.ResourceType, Watermark = Time(cursor.LastModifiedWatermark), LastSuccess = Time(cursor.LastSuccessAt), cursor.OpaqueCursor, cursor.Version }).ToList(),
            Issues = issues.Order(StringComparer.Ordinal).ToList()
        };
        ContractFixture.MatchesGolden($"ReadLoops/{platform.ToLowerInvariant()}-{sharedJobType.ToLowerInvariant()}-{scenario.ToString().ToLowerInvariant()}.golden.json", summary);
    }

    // The whole call list when it is short; the first five and the last three with the count otherwise.
    private static object Summarize(IReadOnlyList<string> calls) =>
        calls.Count <= 12 ? new { Count = calls.Count, Head = calls.ToList(), Tail = new List<string>() } : new { Count = calls.Count, Head = calls.Take(5).ToList(), Tail = calls.TakeLast(3).ToList() };

    private static string? Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string Window(DateTimeOffset? after, DateTimeOffset? before) => $"{Time(after) ?? "-"}..{Time(before) ?? "-"}";

    public enum Pager { TwoPages, SecondPageFails, Endless, EmptyLastPage }

    // Each reading of the clock moves it one second on, so a loop that runs until a time budget is spent ends.
    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public void Advance(TimeSpan by) => now += by;
        public override DateTimeOffset GetUtcNow() => now += TimeSpan.FromSeconds(1);
    }

    // Every page read answers from the pager with no records: page n (its cursor read as a number, 0 when none) names
    // n + 1 next. Reads by number find nothing.
    private sealed class RecordingPorts(Pager pager) : IOrderPort, IOrderPackageReadPort, IReturnPort, IHepsiburadaInvoiceStatusPort
    {
        private const int EndlessLimit = 6_000;
        private int pages;
        public List<string> Calls { get; } = [];

        private AdapterResult<AdapterPageResult<T>> Page<T>(string call, AdapterPageRequest page)
        {
            if (Calls.Count >= 20_000) throw new InvalidOperationException("golden: runaway read");
            Calls.Add($"{call} cursor={page.Cursor ?? "-"} limit={page.Limit}");
            pages++;
            var number = page.Cursor is null ? 0 : int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
            var next = (number + 1).ToString(CultureInfo.InvariantCulture);
            return pager switch
            {
                Pager.SecondPageFails when pages >= 2 => AdapterResult<AdapterPageResult<T>>.Failure(new(AdapterErrorClass.TransientNetwork, "GOLDEN_UNAVAILABLE", "golden: not reachable", 503, null, null)),
                // A loop with no page cap of its own (or a cap above this) is stopped here so the test ends; the golden
                // then shows GOLDEN_ENDLESS_LIMIT as the result.
                Pager.Endless when pages > EndlessLimit => AdapterResult<AdapterPageResult<T>>.Failure(new(AdapterErrorClass.TransientNetwork, "GOLDEN_ENDLESS_LIMIT", "golden: endless read stopped by the test", 503, null, null)),
                Pager.Endless => AdapterResult<AdapterPageResult<T>>.Success(new([], next, true)),
                Pager.EmptyLastPage => AdapterResult<AdapterPageResult<T>>.Success(new([], null, false)),
                _ => AdapterResult<AdapterPageResult<T>>.Success(new([], pages < 2 ? next : null, pages < 2))
            };
        }

        private AdapterResult<T> NotFound<T>(string call)
        {
            if (Calls.Count >= 20_000) throw new InvalidOperationException("golden: runaway read");
            Calls.Add(call);
            return AdapterResult<T>.Failure(new(AdapterErrorClass.NotFound, "GOLDEN_NOT_FOUND", "golden: not found", 404, null, null));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(Page<RemoteOrder>($"orders.Poll key={context.IdempotencyKey} window={Window(window.ModifiedAfter, window.ModifiedBefore)} storefront={window.StoreFrontCode ?? "-"}", page));

        public Task<AdapterResult<AdapterPageResult<RemoteOrderPackage>>> PollPackagesAsync(AdapterContext context, PackagePollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(Page<RemoteOrderPackage>($"packages.Poll key={context.IdempotencyKey} window={Window(window.ModifiedAfter, window.ModifiedBefore)} statusLists={window.IncludeStatusLists}", page));

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(Page<RemoteReturnClaim>($"returns.Poll key={context.IdempotencyKey} window={Window(window.ModifiedAfter, window.ModifiedBefore)} storefront={window.StoreFrontCode ?? "-"} status={window.Status ?? "-"} statusWindow={Window(window.StatusModifiedAfter, window.StatusModifiedBefore)}", page));

        public Task<AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>> ListCancelledOrderLinesAsync(AdapterContext context, DateTimeOffset since, DateTimeOffset until, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(Page<RemoteCancelledOrderLine>($"cancelled.List key={context.IdempotencyKey} window={Window(since, until)}", page));

        public Task<AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>> ListMissingInvoicePackagesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(Page<RemoteMissingInvoicePackage>($"missingInvoices.List key={context.IdempotencyKey}", page));

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(NotFound<RemoteOrder>($"orders.Get key={context.IdempotencyKey} id={externalOrderId}"));

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, OrderLookupHint? hint, CancellationToken cancellationToken) =>
            Task.FromResult(NotFound<RemoteOrder>($"orders.Get key={context.IdempotencyKey} id={externalOrderId} hint={hint is not null}"));

        public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) =>
            Task.FromResult(NotFound<RemoteOrderPackage>($"orders.GetPackage key={context.IdempotencyKey} id={externalPackageId}"));

        public Task<AdapterResult<IReadOnlyList<RemoteOrder>>> GetShipmentPackagesAsync(AdapterContext context, IReadOnlyList<string> externalPackageIds, OrderLookupHint? hint, CancellationToken cancellationToken) =>
            Task.FromResult(NotFound<IReadOnlyList<RemoteOrder>>($"orders.GetPackages key={context.IdempotencyKey} ids={externalPackageIds.Count}"));

        public Task<AdapterResult<PackageTrackingStatusSnapshot>> GetPackageTrackingInfoAsync(AdapterContext context, string packageNumber, CancellationToken cancellationToken) =>
            Task.FromResult(NotFound<PackageTrackingStatusSnapshot>($"packages.Tracking key={context.IdempotencyKey} id={packageNumber}"));

        Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
            Task.FromResult(NotFound<RemoteReturnClaim>($"returns.Get key={context.IdempotencyKey} id={externalReturnId}"));

        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
