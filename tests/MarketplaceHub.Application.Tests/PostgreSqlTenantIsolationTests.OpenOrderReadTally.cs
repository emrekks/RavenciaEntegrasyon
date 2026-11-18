using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// 17 Nov 2026: Pazarama's token service answered empty for every request, yet the order status scan ended SUCCEEDED every
// two minutes and moved its ORDER_LIFECYCLE watermark and last success. OpenOrderReadTally's rules: A, the connection's
// failure, fails the job and keeps the position; B, 5xx only from two requests up, ends the run unread (blocked, no last
// success, the failure streak grows) and moves the position on; C and D are a success naming the orders not read.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlTheory]
    [InlineData("SHOPIFY", MarketplaceJobTypes.ShopifyOrderStatusSync)]
    [InlineData("PAZARAMA", MarketplaceJobTypes.PazaramaOrderStatusSync)]
    [InlineData("N11", MarketplaceJobTypes.OrderStatusSync)]
    [InlineData("TRENDYOL", MarketplaceJobTypes.OrderStatusSync)]
    public async Task OpenOrderScanWithNoTokenFailsAndKeepsItsCursor(string platform, string jobType)
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync($"tally-none-{platform.ToLowerInvariant()}", platform, 3);
        var port = new TallyOrderPort(_ => Remote("PAZARAMA_TOKEN_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            var result = await RunTallyJobAsync(tenant.Id, connection.Id, jobType, port, jobId);

            Assert.Equal(JobCompletionKind.Retry, result.Kind);
            Assert.Equal("PAZARAMA_TOKEN_REMOTE_ERROR", result.ErrorCode);
            Assert.True(port.Calls > 0);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDER_LIFECYCLE", watermark);
            Assert.Null(await LifecycleOpaqueCursorAsync(tenant.Id));
            await using var check = fixture.CreateContext();
            // What the scan found out about each order is still recorded for the owner.
            Assert.True(await check.OperationalIssues.AnyAsync(x => x.TenantId == tenant.Id && x.Code == "PAZARAMA_TOKEN_REMOTE_ERROR"));
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Ö1: an unclear token answer (PAZARAMA_TOKEN_INVALID, a contract code) on every order is a manual review, not a success.
    [PostgreSqlFact]
    public async Task OpenOrderScanWithAnUnclearTokenAnswerIsAManualReview()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-token-invalid", "PAZARAMA", 3);
        var port = new TallyOrderPort(_ => Remote("PAZARAMA_TOKEN_INVALID", AdapterErrorClass.ContractViolation));
        try
        {
            var result = await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PazaramaOrderStatusSync, port, jobId);

            Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
            Assert.Equal("PAZARAMA_TOKEN_INVALID", result.ErrorCode);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDER_LIFECYCLE", watermark);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Per-order contract errors (single broken records) keep the scan a success.
    [PostgreSqlFact]
    public async Task OpenOrderScanWithPerOrderContractErrorsSucceeds()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-contract", "PAZARAMA", 3);
        var port = new TallyOrderPort(_ => Remote("PAZARAMA_ORDER_CONTRACT_INVALID", AdapterErrorClass.ContractViolation));
        try
        {
            var result = await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PazaramaOrderStatusSync, port, jobId);

            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            await using var check = fixture.CreateContext();
            Assert.True((await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE")).LastModifiedWatermark > watermark);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task OpenOrderScanRefusedSignInIsHeldNotSucceeded()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-auth", "SHOPIFY", 2);
        var port = new TallyOrderPort(_ => Remote("SHOPIFY_CREDENTIALS_REJECTED", AdapterErrorClass.Authentication));
        try
        {
            var result = await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId);

            Assert.Equal(JobCompletionKind.Blocked, result.Kind);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDER_LIFECYCLE", watermark);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // K1: a partly failed scan succeeds, names the orders it could not ask in its label, and keeps them out of Okunan and
    // out of the records' Hatalı.
    [PostgreSqlFact]
    public async Task OpenOrderScanWithOneAnswerSucceedsAndNamesTheUnreadOrders()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-partial", "PAZARAMA", 3);
        var port = new TallyOrderPort(id => id.EndsWith("-00", StringComparison.Ordinal)
            ? Remote("NOT_FOUND", AdapterErrorClass.NotFound)
            : Remote("PAZARAMA_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            var result = await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PazaramaOrderStatusSync, port, jobId);

            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            await using var check = fixture.CreateContext();
            var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE");
            Assert.True(cursor.LastModifiedWatermark > watermark);
            Assert.Equal(2, cursor.LastFailedCount);
            var job = await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            Assert.Contains("2 sipariş okunamadı", job.ProgressLabel);
            Assert.Equal(0, job.ProgressFailed);
            Assert.Equal(0, job.ProgressReceived);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task OpenOrderScanWhereEveryOrderAnsweredIsUnchanged()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-all", "SHOPIFY", 2);
        var port = new TallyOrderPort(_ => Remote("NOT_FOUND", AdapterErrorClass.NotFound));
        try
        {
            var result = await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId);

            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            await using var check = fixture.CreateContext();
            Assert.True((await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE")).LastModifiedWatermark > watermark);
            var job = await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            Assert.Equal(0, job.ProgressFailed);
            Assert.DoesNotContain("okunamadı", job.ProgressLabel ?? "");
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Rule B, review scenario 1: n11, three open orders always answering the same 5xx, six scheduled runs. No run is a
    // success, the last success stays, the failure streak reaches the threshold and the owner gets the notice.
    [PostgreSqlFact]
    public async Task RuleB_A5xxOutageIsNeverASuccessAndWarnsAfterThreeRuns()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-n11-outage", "N11", 3);
        var port = new TallyOrderPort(_ => Remote("N11_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            for (var run = 0; run < 6; run++)
            {
                var result = await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, port, jobId);
                Assert.Equal(JobCompletionKind.Blocked, result.Kind);
                Assert.Equal("N11_REMOTE_ERROR", result.ErrorCode);
            }
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDER_LIFECYCLE", watermark);
            await using var check = fixture.CreateContext();
            var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE");
            Assert.Equal(6, cursor.ConsecutiveFailureCount);
            var notices = await new AttentionNoticeService(check, fixture.TimeProvider).ListAsync(tenant.Id, CancellationToken.None);
            Assert.Contains(notices, notice => notice.ResourceType == "ORDER_LIFECYCLE" && notice.ConnectionId == connection.Id);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Review scenario 2: a 20-second wobble (one run of 5xx, the next answered) warns nobody; the second run is a success.
    [PostgreSqlFact]
    public async Task RuleB_AWobbleIsForgottenOnTheNextAnsweredRun()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-wobble", "N11", 3);
        var down = true;
        var port = new TallyOrderPort(_ => down ? Remote("N11_REMOTE_ERROR", AdapterErrorClass.Remote5xx) : Remote("NOT_FOUND", AdapterErrorClass.NotFound));
        try
        {
            Assert.Equal(JobCompletionKind.Blocked, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, port, jobId)).Kind);
            down = false;
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, port, jobId)).Kind);
            await using var check = fixture.CreateContext();
            var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE");
            Assert.Equal(0, cursor.ConsecutiveFailureCount);
            Assert.True(cursor.LastSuccessAt > watermark);
            Assert.Empty(await new AttentionNoticeService(check, fixture.TimeProvider).ListAsync(tenant.Id, CancellationToken.None));
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Rule B, review scenario 3a: 27 open orders, the first 25 always answer 5xx. The run is unread but its position moves
    // on, in the cursor text the scan always wrote (no prefix: a deploy overlap reads it as before), so the next run asks
    // the 26th and the 27th and is a success.
    [PostgreSqlFact]
    public async Task RuleB_TheScanGoesPastABatchThatAlwaysAnswers5xx()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-poison", "SHOPIFY", 27);
        var port = new TallyOrderPort(id => int.Parse(id[^2..], System.Globalization.CultureInfo.InvariantCulture) < 25
            ? Remote("SHOPIFY_REMOTE_ERROR", AdapterErrorClass.Remote5xx)
            : Remote("NOT_FOUND", AdapterErrorClass.NotFound));
        try
        {
            Assert.Equal(JobCompletionKind.Blocked, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId)).Kind);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDER_LIFECYCLE", watermark);
            Assert.Equal("after:tally-poison-24", await LifecycleOpaqueCursorAsync(tenant.Id));

            port.Requested.Clear();
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId)).Kind);
            Assert.Equal(["tally-poison-25", "tally-poison-26"], port.Requested);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Rule B, review scenario 3b: Trendyol's open-package queue always answers 5xx; the position moves past it in the
    // scan's own cursor text, and the next run reaches the daily pass over older packages.
    [PostgreSqlFact]
    public async Task RuleB_TrendyolOpenPackageScanGoesPastAQueueThatAlwaysAnswers5xx()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-ty-poison", "TRENDYOL", 3, storefronts: true);
        var port = new TallyOrderPort(_ => Remote("TRENDYOL_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            Assert.Equal(JobCompletionKind.Blocked, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, port, jobId)).Kind);
            Assert.Equal(3, port.Calls);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDER_LIFECYCLE", watermark);
            Assert.StartsWith(OrderReadService.TrendyolOpenScanCursorPrefix + "recent:", await LifecycleOpaqueCursorAsync(tenant.Id));

            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, port, jobId)).Kind);
            Assert.Equal(3, port.Calls);
            await using var check = fixture.CreateContext();
            Assert.NotNull((await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE_OLDER")).LastSuccessAt);
            Assert.Null(await LifecycleOpaqueCursorAsync(tenant.Id));
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Rule B on PttAVM's older-order loop (open orders older than its windows): unread, the last success stays.
    [PostgreSqlFact]
    public async Task RuleB_PttAvmOlderOrdersAnswering5xxAreAnUnreadRun()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-ptt", "PTTAVM", 3, orderedAt: fixture.Now.AddYears(-1));
        var port = new TallyOrderPort(_ => Remote("PTTAVM_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            Assert.Equal(JobCompletionKind.Blocked, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PttAvmOrderStatusSync, port, jobId)).Kind);
            Assert.Equal(3, port.Calls);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDER_LIFECYCLE", watermark);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Review scenario 3c: one open order that answers 5xx for good does not make a failing job each run (rule D).
    [PostgreSqlFact]
    public async Task RuleD_OneOrderThatAlwaysAnswers5xxDoesNotFailTheScan()
    {
        var (tenant, connection, _, jobId) = await SeedOpenOrderTallyAsync("tally-single", "SHOPIFY", 1);
        var port = new TallyOrderPort(_ => Remote("SHOPIFY_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            for (var run = 0; run < 3; run++)
                Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId)).Kind);
            await using var check = fixture.CreateContext();
            Assert.Contains("1 sipariş okunamadı", (await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId)).ProgressLabel);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Rule A: a real network outage keeps failing however many runs it lasts, the watermark in place.
    [PostgreSqlFact]
    public async Task ARealOutageKeepsFailingWithTheWatermarkInPlace()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-outage", "SHOPIFY", 3);
        var port = new TallyOrderPort(_ => Remote("SHOPIFY_NETWORK_ERROR", AdapterErrorClass.TransientNetwork));
        try
        {
            for (var run = 0; run < 4; run++)
                Assert.Equal(JobCompletionKind.Retry, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId)).Kind);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDER_LIFECYCLE", watermark);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // K2: with a full batch the scan moves its position before asking; a connection failure puts it back.
    [PostgreSqlFact]
    public async Task AConnectionFailurePutsAFullBatchsPositionBack()
    {
        var (tenant, connection, _, jobId) = await SeedOpenOrderTallyAsync("tally-revert", "SHOPIFY", 27);
        var port = new TallyOrderPort(_ => Remote("SHOPIFY_NETWORK_ERROR", AdapterErrorClass.TransientNetwork));
        try
        {
            Assert.Equal(JobCompletionKind.Retry, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId)).Kind);
            Assert.Null(await LifecycleOpaqueCursorAsync(tenant.Id));
            port.Requested.Clear();
            await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId);
            Assert.Equal("tally-revert-00", port.Requested[0]);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // K3: Hepsiburada's cancelled-order list, read, is an answer: the scan is not failed for its orders' 5xx.
    [PostgreSqlFact]
    public async Task HepsiburadaCancelledListReadCountsAsAnAnswer()
    {
        var (tenant, connection, _, jobId) = await SeedOpenOrderTallyAsync("tally-hb", "HEPSIBURADA", 3);
        await using (var seedDb = fixture.CreateContext())
        {
            // The package status reconciliation has just run, so it does not run here.
            seedDb.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = HepsiburadaPackageStatusReconciliationPolicy.CursorResourceType, LastSuccessAt = fixture.Now, Version = 1 });
            await seedDb.SaveChangesAsync();
        }
        var port = new TallyOrderPort(_ => Remote("HEPSIBURADA_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        var cancelled = new EmptyCancelledListPort();
        try
        {
            var result = await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.HepsiburadaOrderStatusSync, port, jobId, cancelled);

            Assert.True(cancelled.Calls > 0);
            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    private static AdapterError Remote(string code, AdapterErrorClass @class) => new(@class, code, code, 503, null, null);

    private async Task<string?> LifecycleOpaqueCursorAsync(Guid tenantId)
    {
        await using var check = fixture.CreateContext();
        return (await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.ResourceType == "ORDER_LIFECYCLE")).OpaqueCursor;
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection, DateTimeOffset Watermark, Guid JobId)> SeedOpenOrderTallyAsync(string name, string platform, int openOrders, bool storefronts = false, DateTimeOffset? orderedAt = null)
    {
        var (tenant, connection, watermark) = await SeedReadGuardConnectionAsync(name, platform, "ORDER_LIFECYCLE");
        var jobId = Guid.CreateVersion7();
        await using var seedDb = fixture.CreateContext();
        for (var index = 0; index < openOrders; index++)
        {
            var orderId = Guid.CreateVersion7();
            var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"{name}-{index:00}", isReturnClaim: false, fixture.Now);
            // One storefront each: Trendyol reads each in its own request.
            if (storefronts) order.StoreFrontCode = $"S{index}";
            if (orderedAt is { } at) order.OrderedAt = at;
            seedDb.Orders.Add(order);
            var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), $"{name}-package-{index:00}", fixture.Now);
            package.Status = ShipmentPackageStatus.New;
            package.RawStatus = "New";
            seedDb.ShipmentPackages.Add(package);
        }
        var suffix = Guid.NewGuid().ToString("N");
        seedDb.IntegrationJobs.Add(new IntegrationJob
        {
            Id = jobId,
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            JobType = "ORDER_STATUS_TALLY_TEST",
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "tally",
            JobDedupKey = $"tally-{suffix}",
            EffectIdempotencyKey = $"tally-{suffix}",
            Priority = 1,
            Status = JobStatus.Leased,
            AvailableAt = fixture.Now,
            MaxAttempts = 3,
            CorrelationId = $"tally-{suffix}",
            CreatedAt = fixture.Now,
            Version = 1
        });
        await seedDb.SaveChangesAsync();
        return (tenant, connection, watermark, jobId);
    }

    private async Task<JobExecutionResult> RunTallyJobAsync(Guid tenantId, Guid connectionId, string jobType, IOrderPort orders, Guid jobId, IHepsiburadaInvoiceStatusPort? hepsiburada = null)
    {
        await using (var db = fixture.CreateContext())
            await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.JobType, jobType));
        await using var run = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(run, null!, null!, null!, null!, orders, hepsiburada!, null!, null!, null!, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(tenantId, connectionId, jobType, "{}", "open-order-tally", CancellationToken.None, jobId);
    }

    private async Task DeleteOpenOrderTallyAsync(Guid tenantId)
    {
        await using (var cleanupDb = fixture.CreateContext())
        {
            await cleanupDb.JobReadSummaries.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await cleanupDb.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await cleanupDb.ShipmentPackages.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await cleanupDb.Orders.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        }
        await DeleteReadGuardTenantAsync(tenantId);
    }

    // Hepsiburada's cancelled-order list, answered empty.
    private sealed class EmptyCancelledListPort : IHepsiburadaInvoiceStatusPort
    {
        public int Calls { get; private set; }

        public Task<AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>> ListMissingInvoicePackagesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>> ListCancelledOrderLinesAsync(AdapterContext context, DateTimeOffset since, DateTimeOffset until, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>.Success(new AdapterPageResult<RemoteCancelledOrderLine>([], null, false, 0)));
        }
    }

    // Every order read and every Trendyol package batch answers with the error the test names for the order.
    private sealed class TallyOrderPort(Func<string, AdapterError> answer) : IOrderPort
    {
        public int Calls { get; private set; }
        public List<string> Requested { get; } = [];

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
        {
            Calls++;
            Requested.Add(externalOrderId);
            return Task.FromResult(AdapterResult<RemoteOrder>.Failure(answer(externalOrderId)));
        }

        public Task<AdapterResult<IReadOnlyList<RemoteOrder>>> GetShipmentPackagesAsync(AdapterContext context, IReadOnlyList<string> externalPackageIds, OrderLookupHint? hint, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AdapterResult<IReadOnlyList<RemoteOrder>>.Failure(answer(externalPackageIds[0])));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
