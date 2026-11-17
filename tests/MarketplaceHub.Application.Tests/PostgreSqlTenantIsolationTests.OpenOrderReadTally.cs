using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// 17 Nov 2026: Pazarama's token service answered empty for every request, yet the order status scan ended SUCCEEDED every
// two minutes and moved its ORDER_LIFECYCLE watermark and last success. An open-order scan that asked and got no answer at
// all, every failure a passing one (network, 5xx, quota, sign-in), now fails with the first error and leaves its cursor
// where it was; a scan with at least one answer stays a success and writes its failures on the job (OpenOrderReadTally).
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlTheory]
    [InlineData("SHOPIFY", MarketplaceJobTypes.ShopifyOrderStatusSync)]
    [InlineData("PAZARAMA", MarketplaceJobTypes.PazaramaOrderStatusSync)]
    [InlineData("N11", MarketplaceJobTypes.OrderStatusSync)]
    [InlineData("TRENDYOL", MarketplaceJobTypes.OrderStatusSync)]
    public async Task OpenOrderScanThatHeardNothingFailsAndKeepsItsCursor(string platform, string jobType)
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
            await using var check = fixture.CreateContext();
            Assert.Null((await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE")).OpaqueCursor);
            // What the scan found out about each order is still recorded for the owner.
            Assert.True(await check.OperationalIssues.AnyAsync(x => x.TenantId == tenant.Id && x.Code == "PAZARAMA_TOKEN_REMOTE_ERROR"));
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

    [PostgreSqlFact]
    public async Task OpenOrderScanWithOneAnswerSucceedsAndCountsItsFailuresOnTheJob()
    {
        var (tenant, connection, watermark, jobId) = await SeedOpenOrderTallyAsync("tally-partial", "PAZARAMA", 3);
        var port = new TallyOrderPort(id => id.EndsWith("-00", StringComparison.Ordinal)
            ? Remote("NOT_FOUND", AdapterErrorClass.NotFound)
            : Remote("PAZARAMA_TOKEN_REMOTE_ERROR", AdapterErrorClass.Remote5xx));
        try
        {
            var result = await RunTallyJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PazaramaOrderStatusSync, port, jobId);

            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            await using var check = fixture.CreateContext();
            var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDER_LIFECYCLE");
            Assert.True(cursor.LastModifiedWatermark > watermark);
            Assert.Equal(2, cursor.LastFailedCount);
            var job = await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            Assert.Equal(2, job.ProgressFailed);
            Assert.Contains("2 hatalı", job.ProgressLabel);
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
            Assert.DoesNotContain("hatalı", job.ProgressLabel ?? "");
        }
        finally { await DeleteOpenOrderTallyAsync(tenant.Id); }
    }

    private static AdapterError Remote(string code, AdapterErrorClass @class) => new(@class, code, code, 503, null, null);

    private async Task<(Tenant Tenant, PlatformConnection Connection, DateTimeOffset Watermark, Guid JobId)> SeedOpenOrderTallyAsync(string name, string platform, int openOrders)
    {
        var (tenant, connection, watermark) = await SeedReadGuardConnectionAsync(name, platform, "ORDER_LIFECYCLE");
        var jobId = Guid.CreateVersion7();
        await using var seedDb = fixture.CreateContext();
        for (var index = 0; index < openOrders; index++)
        {
            var orderId = Guid.CreateVersion7();
            seedDb.Orders.Add(NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"{name}-{index:00}", isReturnClaim: false, fixture.Now));
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

    private async Task<JobExecutionResult> RunTallyJobAsync(Guid tenantId, Guid connectionId, string jobType, IOrderPort orders, Guid jobId)
    {
        await using (var db = fixture.CreateContext())
            await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.JobType, jobType));
        await using var run = fixture.CreateContext();
        var processor = new MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor(run, null!, null!, null!, null!, orders, null!, null!, null!, null!, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(tenantId, connectionId, jobType, "{}", "open-order-tally", CancellationToken.None, jobId);
    }

    private async Task DeleteOpenOrderTallyAsync(Guid tenantId)
    {
        await using (var cleanupDb = fixture.CreateContext())
        {
            await cleanupDb.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await cleanupDb.ShipmentPackages.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await cleanupDb.Orders.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        }
        await DeleteReadGuardTenantAsync(tenantId);
    }

    // Every order read and every Trendyol package batch answers with the error the test names for the order.
    private sealed class TallyOrderPort(Func<string, AdapterError> answer) : IOrderPort
    {
        public int Calls { get; private set; }

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
        {
            Calls++;
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
