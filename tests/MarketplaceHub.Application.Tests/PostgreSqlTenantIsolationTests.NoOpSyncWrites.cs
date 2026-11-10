using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Speed plan item 8 (10 Nov 2026): live rewrote ~220 orders an hour of which only 6 had changed on the marketplace,
// and every rewrite sent a data-change event (25k order events, 5.6k question events a day). An order read that
// brings the same order again changes no row and sends no event; a real change still does both.
public sealed partial class PostgreSqlTenantIsolationTests
{
    // withInvoice: the read also carries the package's invoice (seen again as it was).
    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrendyolOrderSync_ReadingTheSameOrderAgainRewritesNoRowAndSendsNoEvent(bool withInvoice)
    {
        var tenant = NewTenant("ty-noop-sync");
        var connection = NewQuestionConnection(tenant);
        const string externalOrderId = "11697864111";
        const string packageNumber = "4234396111";
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(fixture.Now.AddMinutes(-30).ToUnixTimeMilliseconds());
        var modifiedAt = createdAt.AddMinutes(5);
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
        }

        const string customer = """{"customerFirstName":"Ayşe","customerLastName":"Yılmaz","customerEmail":"a@example.com","identityNumber":"11111111111"}""";
        const string address = """{"firstName":"Ayşe","lastName":"Yılmaz","address1":"Çiçek Sok. 4","city":"İstanbul","district":"Kadıköy","countryCode":"TR"}""";
        RemoteOrder Remote(string rawStatus, DateTimeOffset at) => new(
            externalOrderId, externalOrderId, createdAt.AddMinutes(-1), at, "TRY", 100m, 0m, 100m, customer, address, address,
            [new RemoteOrderLine("line-1", "SKU-1", "868000000001", "Test ürün", 1m, 100m, 10m, rawStatus, """{"id":1,"productName":"Test ürün","barcode":"868000000001"}""")],
            [new RemotePackage(packageNumber, null, rawStatus, at, "10", "7330000001", [new RemotePackageAllocation("line-1", 1m, 0m, 0m, 0m, 0m)],
                GrossAmount: 100m, NetAmount: 100m,
                Invoice: !withInvoice ? null : new RemotePackageInvoiceObservation("INVOICED", "TY2026000000001","https://example.com/invoice.pdf", at, ReferencesAreAuthoritative: true))],
            "{}");

        async Task SyncAsync(RemoteOrder remoteOrder)
        {
            await using var processorDb = fixture.CreateContext();
            processorDb.SavingChanges += (_, _) =>
            {
                foreach (var entry in processorDb.ChangeTracker.Entries().Where(x => x.State == EntityState.Modified))
                    output.WriteLine($"{entry.Metadata.ClrType.Name}: {string.Join(", ", entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name))}");
            };
            var processor = new MarketplaceJobProcessor(
                processorDb,
                null!, null!, null!, null!, new ReadOnlyOrderTestPort(remoteOrder.Packages[0], externalOrderId: externalOrderId, directOrderResult: remoteOrder), null!, null!, null!, null!, null!,
                new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderSync,
                $$"""{"externalOrderId":"{{externalOrderId}}","full":false,"packageNumber":"{{packageNumber}}"}""", "noop-sync", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
        }

        try
        {
            await SyncAsync(Remote("Created", modifiedAt));
            var before = await RowStampsAsync(tenant.Id);
            var eventsBefore = await OrderEventCountAsync(tenant.Id);

            // The overlap window brings the very same order again (twice): no row is rewritten, no event is sent.
            output.WriteLine("-- replay 1");
            await SyncAsync(Remote("Created", modifiedAt));
            output.WriteLine("-- replay 2");
            await SyncAsync(Remote("Created", modifiedAt));
            var after = await RowStampsAsync(tenant.Id);
            Assert.Equal(before with { PackageRow = after.PackageRow }, after);
            // A package whose invoice is seen again keeps its "last checked" time fresh (the invoice check reads it),
            // without a new version or UpdatedAt; without an invoice in the read the package row is not touched at all.
            if (!withInvoice) Assert.Equal(before.PackageRow, after.PackageRow);
            Assert.Equal(eventsBefore, await OrderEventCountAsync(tenant.Id));

            // A real change (the package is picked) still writes and still tells the screens.
            output.WriteLine("-- real change");
            await SyncAsync(Remote("Picking", modifiedAt.AddMinutes(5)));
            await using (var check = fixture.CreateContext())
            {
                var package = await check.ShipmentPackages.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
                var order = await check.Orders.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
                Assert.Equal(ShipmentPackageStatus.Processing, package.Status);
                Assert.True(package.Version > before.PackageVersion);
                Assert.True(order.Version > before.OrderVersion);
            }
            Assert.True(await OrderEventCountAsync(tenant.Id) > eventsBefore);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task QuestionSync_ReadingAnUnchangedQuestionSendsNoEvent()
    {
        var tenant = NewTenant("question-noop-event");
        var connection = NewQuestionConnection(tenant);
        var syncState = new MarketplaceQuestionSyncState
        {
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            HistoryImported = true,
            HistoryStartedAt = fixture.Now.AddYears(-1),
            ProgressStatus = "IDLE",
            LastSuccessAt = fixture.Now.AddMinutes(-1),
            Version = 1
        };
        var askedAt = DateTimeOffset.FromUnixTimeMilliseconds(fixture.Now.AddMinutes(-5).ToUnixTimeMilliseconds());
        var remote = NewRemoteQuestion("WAITING_FOR_ANSWER", [new("Customer", "Ürün birebir aynı mı?", askedAt)], askedAt);
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.MarketplaceQuestionSyncStates.Add(syncState);
            await seedDb.SaveChangesAsync();
        }

        async Task SyncAsync(RemoteMarketplaceQuestion question)
        {
            await using var db = fixture.CreateContext();
            var service = new MarketplaceQuestionService(db, new WaitingQuestionListPort(question), fixture.TimeProvider, NullLogger<MarketplaceQuestionService>.Instance);
            Assert.Null((await service.SyncConnectionAsync(tenant.Id, connection.Id, "PRODUCT", CancellationToken.None)).Error);
        }

        async Task<int> EventsAsync()
        {
            await using var db = fixture.CreateContext();
            return await db.IntegrationOutboxEvents.CountAsync(x => x.TenantId == tenant.Id && x.ResourceType == "questions");
        }

        try
        {
            await SyncAsync(remote);
            var events = await EventsAsync();
            Assert.True(events > 0);

            await SyncAsync(remote);
            await SyncAsync(remote);
            Assert.Equal(events, await EventsAsync());

            // The customer writes again: the screens hear of it.
            await SyncAsync(remote with { Conversations = [new("Customer", "Ürün birebir aynı mı?", askedAt), new("Customer", "Cevap bekliyorum.", askedAt.AddMinutes(2))], LastModifiedAt = askedAt.AddMinutes(2) });
            Assert.True(await EventsAsync() > events);
        }
        finally
        {
            await DeleteQuestionSyncTestTenantAsync(tenant.Id);
        }
    }

    // Speed plan item 11: Pazarama's order status scan was queued 27 times an hour with no open order to follow.
    [PostgreSqlFact]
    public async Task Scheduler_QueuesAnOpenPackageStatusScanOnlyWhileAPackageIsOpen()
    {
        var tenant = NewTenant("pazarama-lifecycle-idle");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "PAZARAMA";
        connection.Environment = "PRODUCTION";
        var policy = new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "ORDER_LIFECYCLE", IntervalSeconds = 120, OverlapSeconds = 600, JitterSeconds = 0, Enabled = true, Version = 1 };
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, Guid.CreateVersion7(), "PZ-1001", isReturnClaim: false, fixture.Now);
        var package = NewInvoiceTestPackage(tenant.Id, connection.Id, order.Id, Guid.CreateVersion7(), "PZ-1001-1", fixture.Now);
        // The scheduler works on every operational connection in the database: inside a rolled-back transaction the jobs
        // it would queue for connections other tests left behind are never committed (as in the policy test above).
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.ConnectionSyncPolicies.Add(policy);
        db.Orders.Add(order);
        db.ShipmentPackages.Add(package); // Delivered: nothing open.
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        async Task<int> StatusScansAsync()
        {
            await new ScheduledJobProducer(db, fixture.TimeProvider, new ConfigurationBuilder().Build()).EnqueueDueAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return await db.IntegrationJobs.CountAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PazaramaOrderStatusSync);
        }

        Assert.Equal(0, await StatusScansAsync());

        await db.ShipmentPackages.Where(x => x.Id == package.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ShipmentPackageStatus.Shipped));
        Assert.Equal(1, await StatusScansAsync());
        await transaction.RollbackAsync();
    }

    // A reconcile run walks its window from where the last one stopped, so an order that does not change (and so keeps
    // its UpdatedAt) is not read again on every run.
    [PostgreSqlFact]
    public async Task OrderReconciliation_WalksTheWholeWindowAcrossRuns()
    {
        var tenant = NewTenant("reconcile-rotation");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "SHOPIFY";
        var orders = Enumerable.Range(0, 5).Select(index =>
        {
            var order = NewInvoiceTestOrder(tenant.Id, connection.Id, Guid.CreateVersion7(), $"SH-{index}", isReturnClaim: false, fixture.Now);
            order.OrderedAt = fixture.Now.AddHours(-10 + index);
            return order;
        }).ToList();
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.AddRange(orders);
            await seedDb.SaveChangesAsync();
        }

        var read = new List<string>();
        try
        {
            for (var run = 0; run < 3; run++)
            {
                await using var db = fixture.CreateContext();
                var port = new RecordingNotFoundOrderPort(read);
                var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, port, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderReconciliation, """{"lookbackDays":1,"batchSize":2}""", $"reconcile-rotation-{run}", CancellationToken.None);
                Assert.True(result.Succeeded, result.ErrorSummary);
            }
            // 2 + 2 + (1 and the wrap to the first).
            Assert.Equal(["SH-0", "SH-1", "SH-2", "SH-3", "SH-4", "SH-0"], read);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    // Answers every order read with "not found" and records which orders were asked for.
    private sealed class RecordingNotFoundOrderPort(List<string> read) : IOrderPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
        {
            read.Add(externalOrderId);
            return Task.FromResult(AdapterResult<RemoteOrder>.Failure(new(AdapterErrorClass.NotFound, "REMOTE_ORDER_NOT_FOUND", "not found", 404, null, null)));
        }
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed record RowStamps(string Rows, string PackageRow, long OrderVersion, long PackageVersion, DateTimeOffset OrderUpdatedAt, DateTimeOffset PackageUpdatedAt);

    // xmin changes on every UPDATE of a row, a no-op one included: equal stamps mean no row was rewritten.
    private async Task<RowStamps> RowStampsAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        var rows = await db.Database.SqlQuery<string>($"""
            SELECT string_agg(stamp, ',' ORDER BY stamp) AS "Value" FROM (
                SELECT 'o' || xmin::text AS stamp FROM sales.orders WHERE "TenantId" = {tenantId}
                UNION ALL SELECT 'l' || xmin::text FROM sales.order_lines WHERE "TenantId" = {tenantId}
                UNION ALL SELECT 'a' || xmin::text FROM sales.package_line_allocations WHERE "TenantId" = {tenantId}) stamps
            """).SingleAsync();
        var packageRow = await db.Database.SqlQuery<string>($"""SELECT xmin::text AS "Value" FROM sales.shipment_packages WHERE "TenantId" = {tenantId}""").SingleAsync();
        var order = await db.Orders.AsNoTracking().SingleAsync(x => x.TenantId == tenantId);
        var package = await db.ShipmentPackages.AsNoTracking().SingleAsync(x => x.TenantId == tenantId);
        return new(rows, packageRow, order.Version, package.Version, order.UpdatedAt, package.UpdatedAt);
    }

    private async Task<int> OrderEventCountAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        return await db.IntegrationOutboxEvents.CountAsync(x => x.TenantId == tenantId && x.ResourceType == "orders");
    }
}
