using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// J3 (15 Nov 2026): the read loops that had no page cap (n11 orders, catalog structure, n11 and Pazarama returns) were
// walked until the lease ran out when a marketplace kept saying "more" or named the page just read as the next one. Each
// now stops at a generous cap or a repeated page with its own code, ends for review, and moves no watermark, so the range
// is read again.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlTheory]
    [InlineData(false, "N11_ORDER_PAGE_CAP")]
    [InlineData(true, "N11_ORDER_PAGE_REPEATED")]
    public async Task J3_N11OrderListThatNeverEndsStopsWithoutMovingTheWatermark(bool repeat, string code)
    {
        var (tenant, connection, watermark) = await SeedReadGuardConnectionAsync(repeat ? "j3-n11-repeat" : "j3-n11-cap", "N11", "ORDERS_HOT");
        var port = new EndlessOrderPort(repeat);
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.N11OrderSync, orders: port);
            Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
            Assert.Equal(code, result.ErrorCode);
            Assert.Equal(repeat ? 2 : OrderReadService.N11OrderPageCap, port.Calls);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDERS_HOT", watermark);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    [PostgreSqlTheory]
    [InlineData("N11", "N11_RETURN_PAGE_CAP")]
    [InlineData("PAZARAMA", "PAZARAMA_RETURN_PAGE_CAP")]
    public async Task J3_ReturnListThatNeverEndsStopsWithoutMovingTheWatermark(string platform, string code)
    {
        var (tenant, connection, watermark) = await SeedReadGuardConnectionAsync($"j3-returns-{platform.ToLowerInvariant()}", platform, "RETURNS");
        var port = new EndlessReturnPort();
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnSync, returns: port);
            Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
            Assert.Equal(code, result.ErrorCode);
            Assert.Equal(ReturnReadService.ReturnPageCap, port.Calls);
            await AssertWatermarkUnchangedAsync(tenant.Id, "RETURNS", watermark);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    // ISLEYICI-9 round 10: Hepsiburada's paid-order list and claim list had no page cap of their own; a list that names the
    // offset just read again, or never ends, now stops with its own code and leaves the watermark where it was.
    [PostgreSqlTheory]
    [InlineData(false, "HEPSIBURADA_ORDER_PAGE_CAP")]
    [InlineData(true, "HEPSIBURADA_ORDER_PAGE_REPEATED")]
    public async Task J3_HepsiburadaOrderListThatNeverEndsStopsWithoutMovingTheWatermark(bool repeat, string code)
    {
        var (tenant, connection, watermark) = await SeedReadGuardConnectionAsync(repeat ? "j3-hb-order-repeat" : "j3-hb-order-cap", "HEPSIBURADA", "ORDERS_HOT");
        var port = new EndlessOrderPort(repeat);
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.HepsiburadaOrderSync, orders: port);
            Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
            Assert.Equal(code, result.ErrorCode);
            Assert.Equal(repeat ? 2 : OrderReadService.HepsiburadaOrderPageCap, port.Calls);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDERS_HOT", watermark);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task J3_HepsiburadaClaimListThatNeverEndsStopsWithoutMovingTheWatermark()
    {
        var (tenant, connection, watermark) = await SeedReadGuardConnectionAsync("j3-hb-returns-cap", "HEPSIBURADA", "RETURNS");
        var port = new EndlessReturnPort();
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.HepsiburadaReturnSync, returns: port);
            Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
            Assert.Equal("HEPSIBURADA_RETURN_PAGE_CAP", result.ErrorCode);
            Assert.Equal(ReturnReadService.HepsiburadaReturnPageCap, port.Calls);
            await AssertWatermarkUnchangedAsync(tenant.Id, "RETURNS", watermark);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    // The return reconciliation is Trendyol's read: on another marketplace it ends blocked before any request.
    [PostgreSqlTheory]
    [InlineData("HEPSIBURADA")]
    [InlineData("N11")]
    [InlineData("SHOPIFY")]
    public async Task ReturnReconciliationOutsideTrendyolIsBlockedBeforeAnyRequest(string platform)
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync($"return-reconcile-{platform.ToLowerInvariant()}", platform, "RETURNS");
        var port = new EndlessReturnPort();
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnReconciliation, """{"lookbackDays":3}""", returns: port);
            Assert.Equal(JobCompletionKind.Blocked, result.Kind);
            Assert.Equal("RETURN_RECONCILIATION_NOT_SUPPORTED", result.ErrorCode);
            Assert.Equal(0, port.Calls);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    [PostgreSqlTheory]
    [InlineData(false, "CATALOG_STRUCTURE_PAGE_CAP")]
    [InlineData(true, "CATALOG_STRUCTURE_PAGE_REPEATED")]
    public async Task J3_CatalogStructureImportOfAListThatNeverEndsFailsItsRun(bool repeat, string code)
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync(repeat ? "j3-structure-repeat" : "j3-structure-cap", "TRENDYOL", "PRODUCTS");
        var run = new CatalogStructureRun { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, Mode = "PREVIEW", Status = "QUEUED", CreatedAt = fixture.Now };
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.CatalogStructureRuns.Add(run);
            await seedDb.SaveChangesAsync();
        }
        var port = new EndlessCatalogPort(repeat);
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.CatalogStructureImport, $$"""{"runId":"{{run.Id}}"}""", products: port);
            Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
            Assert.Equal(code, result.ErrorCode);
            Assert.Equal(repeat ? 2 : CatalogImportService.CatalogStructurePageCap, port.Calls);
            await using var check = fixture.CreateContext();
            var saved = await check.CatalogStructureRuns.AsNoTracking().SingleAsync(x => x.Id == run.Id);
            Assert.Equal("FAILED", saved.Status);
        }
        finally
        {
            await using (var cleanupDb = fixture.CreateContext())
                await cleanupDb.CatalogStructureRuns.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteReadGuardTenantAsync(tenant.Id);
        }
    }

    // Hepsiburada's cancelled-order list is a cross-check of the open-order scan: at its page cap the scan still finishes,
    // but the list's cursors stay where they were (it used to stop as if the list had ended and move them) and an issue
    // names the stop; a complete read moves them and closes the issue.
    [PostgreSqlTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task J3_HepsiburadaCancelledListCutAtItsCapIsNotTakenAsRead(bool endless)
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync(endless ? "j3-hb-cancel-cap" : "j3-hb-cancel-end", "HEPSIBURADA", "ORDER_LIFECYCLE");
        var port = new PagingCancellationPort(endless ? int.MaxValue : 2);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, port, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.HepsiburadaOrderStatusSync, "{}", "j3-hb-cancel", CancellationToken.None);
                Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            }
            Assert.Equal(endless ? HepsiburadaCancellationPolicy.MaxPages : 2, port.Calls);
            await using var check = fixture.CreateContext();
            var cursors = await check.SyncCursors.AsNoTracking().Where(x => x.TenantId == tenant.Id && (x.ResourceType == HepsiburadaCancellationPolicy.CursorResource || x.ResourceType == HepsiburadaCancellationPolicy.WideCursorResource)).ToListAsync();
            Assert.Equal(2, cursors.Count);
            Assert.All(cursors, cursor => Assert.Equal(endless, cursor.LastSuccessAt is null));
            var issues = await check.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.Code == "HEPSIBURADA_CANCELLED_LIST_INCOMPLETE" && x.ResolvedAt == null).CountAsync();
            Assert.Equal(endless ? 1 : 0, issues);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    // B-10 (25 Nov 2026): a month's cancelled list cut at its cap is tried again only after one last-day read, so the new
    // cancellations are read meanwhile; before, every run asked the month again and stopped at the cap.
    [PostgreSqlFact]
    public async Task B10_AfterAWideCancelledListStopsAtItsCapTheNextRunReadsTheLastDay()
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync("b10-hb-cancel-wide", "HEPSIBURADA", "ORDER_LIFECYCLE");
        var port = new PagingCancellationPort(int.MaxValue);
        async Task RunAsync()
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, port, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            Assert.Equal(JobCompletionKind.Succeeded, (await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.HepsiburadaOrderStatusSync, "{}", "b10-hb-cancel", CancellationToken.None)).Kind);
        }
        try
        {
            await RunAsync();
            double DaysBack(int call) => (fixture.TimeProvider.GetUtcNow() - port.Since[call]).TotalDays;
            Assert.InRange(DaysBack(0), 29.9, 30.1);
            var second = port.Calls;
            await RunAsync();
            Assert.InRange(DaysBack(second), 0.9, 1.1);
            var third = port.Calls;
            await RunAsync();
            Assert.InRange(DaysBack(third), 29.9, 30.1);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    // Pazarama's open-order scan reads whole order-date windows when more orders are open than one run reads by number. A
    // window cut at its page cap used to be reported while the scan still finished and moved its watermark; now the scan
    // stops there with the same code and the watermark stays.
    [PostgreSqlFact]
    public async Task J3_PazaramaOpenOrderWindowCutAtItsCapStopsTheScan()
    {
        var (tenant, connection, watermark) = await SeedReadGuardConnectionAsync("j3-pazarama-open", "PAZARAMA", "ORDER_LIFECYCLE");
        await using (var seedDb = fixture.CreateContext())
        {
            for (var index = 0; index < 26; index++)
            {
                var orderId = Guid.CreateVersion7();
                var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"j3-open-{index}", isReturnClaim: false, fixture.Now);
                order.OrderedAt = fixture.Now.AddHours(-2).AddMinutes(-index);
                seedDb.Orders.Add(order);
                var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), $"j3-open-package-{index}", fixture.Now);
                package.Status = ShipmentPackageStatus.New;
                package.RawStatus = "New";
                seedDb.ShipmentPackages.Add(package);
            }
            await seedDb.SaveChangesAsync();
        }
        var port = new EndlessOrderPort(repeat: false);
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PazaramaOrderStatusSync, orders: port);
            Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
            Assert.Equal("PAZARAMA_OPEN_ORDER_WINDOW_TRUNCATED", result.ErrorCode);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDER_LIFECYCLE", watermark);
            await using var check = fixture.CreateContext();
            Assert.True(await check.OperationalIssues.AnyAsync(x => x.TenantId == tenant.Id && x.Code == "PAZARAMA_OPEN_ORDER_WINDOW_TRUNCATED" && x.ResolvedAt == null));
        }
        finally
        {
            await using (var cleanupDb = fixture.CreateContext())
            {
                await cleanupDb.ShipmentPackages.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
                await cleanupDb.Orders.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            }
            await DeleteReadGuardTenantAsync(tenant.Id);
        }
    }

    [Fact]
    public void J3_AReturnPageKeyNamesItsClaimsAndThePageNumberOnlyMovesForward()
    {
        RemoteReturnClaim Claim(string id) => new(id, "order-1", "CREATED", null, null, null, null, [], "{}");
        Assert.Null(ReturnReadService.ReturnPageKey([]));
        Assert.Equal(ReturnReadService.ReturnPageKey([Claim("b"), Claim("a")]), ReturnReadService.ReturnPageKey([Claim("a"), Claim("b")]));
        Assert.Equal("5", ReturnReadService.NextReturnPageNumber(1, "5"));
        Assert.Equal("2", ReturnReadService.NextReturnPageNumber(1, "1"));
        Assert.Equal("2", ReturnReadService.NextReturnPageNumber(1, null));
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection, DateTimeOffset Watermark)> SeedReadGuardConnectionAsync(string name, string platform, string cursorResource)
    {
        var tenant = NewTenant(name);
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = platform;
        connection.Environment = "PRODUCTION";
        var watermark = DateTimeOffset.FromUnixTimeSeconds(fixture.Now.AddHours(-1).ToUnixTimeSeconds());
        await using var seedDb = fixture.CreateContext();
        seedDb.Tenants.Add(tenant);
        seedDb.PlatformConnections.Add(connection);
        seedDb.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = cursorResource, LastModifiedWatermark = watermark, LastSuccessAt = watermark, Version = 1 });
        await seedDb.SaveChangesAsync();
        return (tenant, connection, watermark);
    }

    private async Task AssertWatermarkUnchangedAsync(Guid tenantId, string resource, DateTimeOffset watermark)
    {
        await using var check = fixture.CreateContext();
        var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.ResourceType == resource);
        Assert.Equal(watermark, cursor.LastModifiedWatermark);
        Assert.Equal(watermark, cursor.LastSuccessAt);
    }

    private async Task<JobExecutionResult> RunReadGuardJobAsync(Guid tenantId, Guid connectionId, string jobType, string payload = "{}", IOrderPort? orders = null, IReturnPort? returns = null, IProductPort? products = null)
    {
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, products!, null!, orders!, null!, null!, returns!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(tenantId, connectionId, jobType, payload, "j3-read-guards", CancellationToken.None);
    }

    private async Task DeleteReadGuardTenantAsync(Guid tenantId)
    {
        await using var cleanupDb = fixture.CreateContext();
        await cleanupDb.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanupDb.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await DeleteQuestionSyncTestTenantAsync(tenantId);
    }

    // Empty pages that always say there is another one: page n names n + 1 next, or (repeat) names page "1" again.
    private sealed class EndlessOrderPort(bool repeat) : IOrderPort
    {
        public int Calls { get; private set; }

        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            Calls++;
            var number = page.Cursor is null ? 0 : int.Parse(page.Cursor, System.Globalization.CultureInfo.InvariantCulture);
            var next = repeat ? "1" : (number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteOrder>>.Success(new AdapterPageResult<RemoteOrder>([], next, true, 0)));
        }

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Empty cancelled-order pages numbered 1, 2, 3, ...; page `lastPage` says there is no next one.
    private sealed class PagingCancellationPort(int lastPage) : IHepsiburadaInvoiceStatusPort
    {
        public int Calls { get; private set; }
        // The start of the window each list read asked for.
        public List<DateTimeOffset> Since { get; } = [];

        public Task<AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>> ListCancelledOrderLinesAsync(AdapterContext context, DateTimeOffset since, DateTimeOffset until, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            Calls++;
            Since.Add(since);
            var number = page.Cursor is null ? 1 : int.Parse(page.Cursor, System.Globalization.CultureInfo.InvariantCulture);
            var more = number < lastPage;
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>.Success(new([], more ? (number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null, more)));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>> ListMissingInvoicePackagesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EndlessReturnPort : IReturnPort
    {
        public int Calls { get; private set; }

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new AdapterPageResult<RemoteReturnClaim>([], null, true)));
        }

        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EndlessCatalogPort(bool repeat) : IProductPort
    {
        public int Calls { get; private set; }

        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
        {
            Calls++;
            var number = page.Cursor is null ? 0 : int.Parse(page.Cursor, System.Globalization.CultureInfo.InvariantCulture);
            var next = repeat ? "1" : (number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new([], next, true, null)));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
