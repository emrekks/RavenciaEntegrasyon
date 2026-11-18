using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// 17 Nov 2026: Pazarama's token service answered empty for every request, yet the order status scan ended SUCCEEDED every
// two minutes and moved its ORDER_LIFECYCLE watermark and last success. Rule A (OpenOrderReadTally): an open-order scan
// that got no answer, every failure the connection's (token or session, sign-in, network, quota), fails with that error
// and keeps its position and watermark. Server errors only and mixed runs go on exactly as before; their label names
// the orders not read (the tests below pin that behaviour, position, older-pass stamp and job result as before).
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlTheory]
    [InlineData("SHOPIFY", MarketplaceJobTypes.ShopifyOrderStatusSync)]
    [InlineData("PAZARAMA", MarketplaceJobTypes.PazaramaOrderStatusSync)]
    [InlineData("N11", MarketplaceJobTypes.OrderStatusSync)]
    [InlineData("TRENDYOL", MarketplaceJobTypes.OrderStatusSync)]
    public async Task RuleA_OpenOrderScanWithNoTokenFailsAndKeepsItsCursor(string platform, string jobType)
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

    // An unclear token answer (PAZARAMA_TOKEN_INVALID, a contract code) on every order is a manual review, not a success.
    [PostgreSqlFact]
    public async Task RuleA_OpenOrderScanWithAnUnclearTokenAnswerIsAManualReview()
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

    [PostgreSqlFact]
    public async Task RuleA_OpenOrderScanRefusedSignInIsHeldNotSucceeded()
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

    // A real network outage keeps failing however many runs it lasts, the watermark in place.
    [PostgreSqlFact]
    public async Task RuleA_ANetworkOutageKeepsFailingWithTheWatermarkInPlace()
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

    // With a full batch the scan moves its position before asking; a connection failure puts it back.
    [PostgreSqlFact]
    public async Task RuleA_AConnectionFailurePutsAFullBatchsPositionBack()
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

    // Rule A on PttAVM's older-order loop (open orders older than its windows).
    [PostgreSqlFact]
    public async Task RuleA_PttAvmOlderOrdersWithoutNetworkFailTheScan()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-ptt", "PTTAVM", 3, orderedAt: fixture.Now.AddYears(-1));
        var port = new TallyOrderPort(_ => Remote("PTTAVM_NETWORK_ERROR", AdapterErrorClass.TransientNetwork));
        try
        {
            Assert.Equal(JobCompletionKind.Retry, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PttAvmOrderStatusSync, port, jobId)).Kind);
            Assert.Equal(3, port.Calls);
            await AssertWatermarkUnchangedAsync(tenant.Id, "ORDER_LIFECYCLE", watermark);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // K3: Hepsiburada's cancelled-order list, read, is an answer: its orders' network errors do not fail the scan.
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
        var port = new TallyOrderPort(_ => Remote("HEPSIBURADA_NETWORK_ERROR", AdapterErrorClass.TransientNetwork));
        var cancelled = new EmptyCancelledListPort();
        try
        {
            var result = await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.HepsiburadaOrderStatusSync, port, jobId, cancelled);

            Assert.True(cancelled.Calls > 0);
            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
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
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PazaramaOrderStatusSync, port, jobId)).Kind);
            await AssertLifecycleMovedAsync(tenant.Id, watermark);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // A partly failed scan succeeds and names the orders it could not ask, out of Okunan and out of the records' Hatalı.
    [PostgreSqlFact]
    public async Task OpenOrderScanWithOneAnswerSucceedsAndNamesTheUnreadOrders()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-partial", "PAZARAMA", 3);
        var port = new TallyOrderPort(id => id.EndsWith("-00", StringComparison.Ordinal)
            ? Remote("NOT_FOUND", AdapterErrorClass.NotFound)
            : Remote("PAZARAMA_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PazaramaOrderStatusSync, port, jobId)).Kind);
            await AssertLifecycleMovedAsync(tenant.Id, watermark);
            await using var check = fixture.CreateContext();
            Assert.Equal(2, (await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE")).LastFailedCount);
            var job = await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            AssertNoteWithoutCount(job.ProgressLabel);
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
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId)).Kind);
            await AssertLifecycleMovedAsync(tenant.Id, watermark);
            Assert.DoesNotContain("okunamadı", await JobLabelAsync(jobId));
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Server errors only: as before. n11, three open orders all answering 5xx: a success, watermark and last success move,
    // the label names the three orders.
    [PostgreSqlFact]
    public async Task ServerErrorsOnly_N11ScanGoesOnAsBefore()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-n11-5xx", "N11", 3);
        var port = new TallyOrderPort(_ => Remote("N11_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, port, jobId)).Kind);
            Assert.Equal(3, port.Calls);
            await AssertLifecycleMovedAsync(tenant.Id, watermark);
            Assert.Null(await LifecycleOpaqueCursorAsync(tenant.Id));
            AssertNoteWithoutCount(await JobLabelAsync(jobId));
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Server errors only, 27 open orders: the keyset position moves past the first 25 as before; the next run asks the rest.
    [PostgreSqlFact]
    public async Task ServerErrorsOnly_TheKeysetPositionMovesAsBefore()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-keyset", "SHOPIFY", 27);
        var port = new TallyOrderPort(id => int.Parse(id[^2..], System.Globalization.CultureInfo.InvariantCulture) < 25
            ? Remote("SHOPIFY_REMOTE_ERROR", AdapterErrorClass.Remote5xx)
            : Remote("NOT_FOUND", AdapterErrorClass.NotFound));
        try
        {
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId)).Kind);
            await AssertLifecycleMovedAsync(tenant.Id, watermark);
            Assert.Equal("after:tally-keyset-24", await LifecycleOpaqueCursorAsync(tenant.Id));
            port.Requested.Clear();
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId)).Kind);
            Assert.Equal(["tally-keyset-25", "tally-keyset-26"], port.Requested);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // One open order that answers 5xx for good: a success each run, as before, with the note.
    [PostgreSqlFact]
    public async Task ServerErrorsOnly_OneOrderThatAlwaysAnswers5xxGoesOnAsBefore()
    {
        var (tenant, connection, _, jobId) = await SeedOpenOrderTallyAsync("tally-single", "SHOPIFY", 1);
        var port = new TallyOrderPort(_ => Remote("SHOPIFY_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            for (var run = 0; run < 3; run++)
                Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, port, jobId)).Kind);
            AssertNoteWithoutCount(await JobLabelAsync(jobId));
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // Trendyol, server errors only: one request for all packages (one storefront) or one per storefront, recent packages or
    // the daily pass over older ones. As before: a success, the scan position cleared, the older pass stamped, the
    // watermark moved; the label names the packages' orders not read.
    [PostgreSqlTheory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 3)]
    public async Task ServerErrorsOnly_TrendyolScanGoesOnAsBefore(bool storefronts, bool older, int requests)
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync($"tally-ty-{storefronts}-{older}".ToLowerInvariant(), "TRENDYOL", 3,
            storefronts: storefronts, orderedAt: older ? fixture.Now.AddDays(-120) : null);
        var port = new TallyOrderPort(_ => Remote("TRENDYOL_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, port, jobId)).Kind);
            Assert.Equal(requests, port.Calls);
            await AssertLifecycleMovedAsync(tenant.Id, watermark);
            Assert.Null(await LifecycleOpaqueCursorAsync(tenant.Id));
            await using var check = fixture.CreateContext();
            Assert.NotNull((await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE_OLDER")).LastSuccessAt);
            AssertNoteWithoutCount(await JobLabelAsync(jobId));
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // E1: the label says "some orders were not read" without a count, and only for a real order or package read error.
    // Trendyol, one storefront: one request carries the three packages and answers 5xx: note, no count.
    // Trendyol, a storefront each: one network error and the other storefronts "not found": note.
    // Trendyol, a storefront each, all "not found": no note.
    [PostgreSqlTheory]
    [InlineData(false, "5xx", true)]
    [InlineData(true, "network+notfound", true)]
    [InlineData(true, "notfound", false)]
    public async Task TheNoteFollowsRealReadErrorsOnly(bool storefronts, string answers, bool note)
    {
        var (tenant, connection, _, jobId) = await SeedOpenOrderTallyAsync($"tally-note-{answers}".Replace('+', '-'), "TRENDYOL", 3, storefronts: storefronts);
        var port = new TallyOrderPort(id => answers switch
        {
            "5xx" => Remote("TRENDYOL_REMOTE_ERROR", AdapterErrorClass.Remote5xx),
            "network+notfound" when id.EndsWith("-00", StringComparison.Ordinal) => Remote("TRENDYOL_NETWORK_ERROR", AdapterErrorClass.TransientNetwork),
            _ => Remote("NOT_FOUND", AdapterErrorClass.NotFound)
        });
        try
        {
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, port, jobId)).Kind);
            var label = await JobLabelAsync(jobId);
            if (note) AssertNoteWithoutCount(label);
            else Assert.DoesNotContain("okunamadı", label);
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // E1: n11, 25 open orders all answering 5xx: note, no count.
    [PostgreSqlFact]
    public async Task TheNoteOnTwentyFiveServerErrorsHasNoCount()
    {
        var (tenant, connection, _, jobId) = await SeedOpenOrderTallyAsync("tally-note-n11", "N11", 25);
        var port = new TallyOrderPort(_ => Remote("N11_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, port, jobId)).Kind);
            Assert.Equal(25, port.Calls);
            AssertNoteWithoutCount(await JobLabelAsync(jobId));
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    // E1: another failure of the same job (Hepsiburada's cancelled-order list) is no order read error: no note when every
    // order answered.
    [PostgreSqlFact]
    public async Task AnotherFailureOfTheJobDoesNotWriteTheNote()
    {
        var (tenant, connection, _, jobId) = await SeedOpenOrderTallyAsync("tally-note-hb", "HEPSIBURADA", 3);
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = HepsiburadaPackageStatusReconciliationPolicy.CursorResourceType, LastSuccessAt = fixture.Now, Version = 1 });
            await seedDb.SaveChangesAsync();
        }
        var port = new TallyOrderPort(_ => Remote("NOT_FOUND", AdapterErrorClass.NotFound));
        var cancelled = new EmptyCancelledListPort(fails: true);
        try
        {
            Assert.Equal(JobCompletionKind.Succeeded, (await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.HepsiburadaOrderStatusSync, port, jobId, cancelled)).Kind);
            Assert.True(cancelled.Calls > 0);
            await using var check = fixture.CreateContext();
            Assert.True((await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE")).LastFailedCount > 0);
            Assert.DoesNotContain("okunamadı", await JobLabelAsync(jobId));
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    private static void AssertNoteWithoutCount(string? label)
    {
        Assert.Contains(" · bazı siparişler okunamadı", label);
        Assert.DoesNotMatch("[0-9]+ sipariş okunamadı", label!);
    }

    private static AdapterError Remote(string code, AdapterErrorClass @class) => new(@class, code, code, 503, null, null);

    private async Task<string?> LifecycleOpaqueCursorAsync(Guid tenantId)
    {
        await using var check = fixture.CreateContext();
        return (await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.ResourceType == "ORDER_LIFECYCLE")).OpaqueCursor;
    }

    private async Task AssertLifecycleMovedAsync(Guid tenantId, DateTimeOffset watermark)
    {
        await using var check = fixture.CreateContext();
        var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.ResourceType == "ORDER_LIFECYCLE");
        Assert.True(cursor.LastModifiedWatermark > watermark);
        Assert.True(cursor.LastSuccessAt > watermark);
        Assert.Equal(0, cursor.ConsecutiveFailureCount);
    }

    private async Task<string> JobLabelAsync(Guid jobId)
    {
        await using var check = fixture.CreateContext();
        return (await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId)).ProgressLabel ?? "";
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

    // Hepsiburada's cancelled-order list, answered empty, or failing with a server error.
    private sealed class EmptyCancelledListPort(bool fails = false) : IHepsiburadaInvoiceStatusPort
    {
        public int Calls { get; private set; }

        public Task<AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>> ListMissingInvoicePackagesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>> ListCancelledOrderLinesAsync(AdapterContext context, DateTimeOffset since, DateTimeOffset until, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            Calls++;
            if (fails) return Task.FromResult(AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>.Failure(Remote("HEPSIBURADA_REMOTE_ERROR", AdapterErrorClass.Remote5xx)));
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
