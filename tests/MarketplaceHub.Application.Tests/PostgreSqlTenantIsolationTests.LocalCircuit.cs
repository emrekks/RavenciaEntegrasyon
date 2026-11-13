using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan 13 Nov 2026 #1: a package action, a return decision or a common label refused by the panel's own open circuit was
// never sent. Before, each was taken for an uncertain write (manual review, resend blocked by the ExternalEffect left
// behind); now no effect is left, the job keeps its attempt and the next run sends it again.
public sealed partial class PostgreSqlTenantIsolationTests
{
    private static AdapterError LocalCircuitRefusal => new(AdapterErrorClass.RateLimit, LocalCircuitOpen.Code("TRENDYOL"), "Trendyol isteği gönderilmedi.", 429, TimeSpan.FromSeconds(20), null);

    private static void AssertLocalCircuitDeferred(JobExecutionResult result)
    {
        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.Equal("TRENDYOL_LOCAL_CIRCUIT_OPEN", result.ErrorCode);
        Assert.True(result.RefundAttempt);
    }

    [PostgreSqlFact]
    public async Task LocalCircuit_APackageActionAndACommonLabelAreSentAgain_WithNoUnclearEffect()
    {
        var tenant = NewTenant("local-circuit-package");
        var connection = NewQuestionConnection(tenant);
        var orderId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, "local-circuit-order", isReturnClaim: false, fixture.Now);
        var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), "local-circuit-package", fixture.Now);
        package.CargoTrackingNumber = "LC-TRACK-1";
        package.CargoProviderExternalId = "ARASMP";
        var actionJob = NewJob(tenant.Id);
        actionJob.ConnectionId = connection.Id;
        actionJob.JobType = MarketplaceJobTypes.ShipmentAction;
        actionJob.PayloadJson = JsonSerializer.Serialize(new ShipmentActionJobPayload(actionJob.Id, package.Id, "PICKING", "{}"));
        var labelJob = NewJob(tenant.Id);
        labelJob.ConnectionId = connection.Id;
        labelJob.JobType = MarketplaceJobTypes.CommonLabel;
        labelJob.PayloadJson = JsonSerializer.Serialize(new CommonLabelJobPayload(labelJob.Id, package.Id, "SUBMIT", 1, 0m, fixture.Now.AddMinutes(-1), fixture.Now.AddHours(1)));
        var attempt = new ShipmentDocumentAttempt { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PackageId = package.Id, IdempotencyKey = labelJob.EffectIdempotencyKey, Status = "PENDING", CreatedAt = fixture.Now };
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.Add(order);
            seedDb.ShipmentPackages.Add(package);
            seedDb.IntegrationJobs.AddRange(actionJob, labelJob);
            seedDb.ShipmentDocumentAttempts.Add(attempt);
            await seedDb.SaveChangesAsync();
        }
        var port = new LocalCircuitOrderPort();

        try
        {
            async Task<JobExecutionResult> RunAsync(string jobType, string payload)
            {
                await using var db = fixture.CreateContext();
                var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, port, null!, null!, null!, null!, null!,
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
                return await processor.ProcessAsync(tenant.Id, connection.Id, jobType, payload, "local-circuit", CancellationToken.None);
            }

            AssertLocalCircuitDeferred(await RunAsync(MarketplaceJobTypes.ShipmentAction, actionJob.PayloadJson));
            AssertLocalCircuitDeferred(await RunAsync(MarketplaceJobTypes.ShipmentAction, actionJob.PayloadJson));
            Assert.Equal(2, port.PackageActions);

            AssertLocalCircuitDeferred(await RunAsync(MarketplaceJobTypes.CommonLabel, labelJob.PayloadJson));
            AssertLocalCircuitDeferred(await RunAsync(MarketplaceJobTypes.CommonLabel, labelJob.PayloadJson));
            Assert.Equal(2, port.Labels);

            await using var check = fixture.CreateContext();
            Assert.False(await check.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenant.Id));
            var savedAttempt = await check.ShipmentDocumentAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt.Id);
            Assert.Equal("PENDING", savedAttempt.Status);
            Assert.Null(savedAttempt.CompletedAt);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.ExternalEffectRecords.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.ShipmentDocumentAttempts.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.JobAttempts.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task LocalCircuit_AReturnDecisionStaysWaitingAndIsSentAgain()
    {
        var tenant = NewTenant("local-circuit-return");
        var connection = NewQuestionConnection(tenant);
        var orderId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, "local-circuit-return-order", isReturnClaim: false, fixture.Now);
        var orderLine = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = orderId, ExternalLineId = "local-circuit-line", Sku = "SKU-1", TitleSnapshot = "Test ürün", OrderedQuantity = 1, UnitPrice = 100, VatRate = 20, RawStatus = "Delivered" };
        var claim = NewReturnLifecycleClaim(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), "local-circuit-claim", fixture.Now, hasCargo: false);
        claim.Status = ReturnClaimStatus.ActionRequired;
        claim.RawStatus = "WaitingInAction";
        var decision = new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, Action = "APPROVE", IdempotencyKey = $"local-circuit-{Guid.NewGuid():N}", Status = "PENDING", ActorUserId = Guid.CreateVersion7(), CreatedAt = fixture.Now };
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.Add(order);
            seedDb.OrderLines.Add(orderLine);
            seedDb.ReturnClaims.Add(claim);
            seedDb.ReturnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = "local-circuit-line", Quantity = 1, RawStatus = "WaitingInAction" });
            seedDb.ReturnDecisions.Add(decision);
            await seedDb.SaveChangesAsync();
        }
        var port = new LocalCircuitReturnPort();

        try
        {
            async Task<JobExecutionResult> RunAsync()
            {
                await using var db = fixture.CreateContext();
                var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, port, null!, null!,
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
                return await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnAction, $$"""{"decisionId":"{{decision.Id}}"}""", "local-circuit-return", CancellationToken.None);
            }

            AssertLocalCircuitDeferred(await RunAsync());
            AssertLocalCircuitDeferred(await RunAsync());

            Assert.Equal(2, port.Executions);
            await using var check = fixture.CreateContext();
            var saved = await check.ReturnDecisions.AsNoTracking().SingleAsync(x => x.Id == decision.Id);
            Assert.Equal("PENDING", saved.Status);
            Assert.Null(saved.CompletedAt);
            Assert.Equal("TRENDYOL_LOCAL_CIRCUIT_OPEN", saved.ErrorCode);
            Assert.False(await check.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenant.Id));
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.ExternalEffectRecords.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.ReturnDecisions.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.ReturnLines.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.OrderLines.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteReturnLifecycleTestTenantAsync(tenant.Id);
        }
    }

    private sealed class LocalCircuitOrderPort : IOrderPort
    {
        public int PackageActions { get; private set; }
        public int Labels { get; private set; }

        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
        {
            PackageActions++;
            return Task.FromResult(AdapterResult<PackageActionResult>.Failure(LocalCircuitRefusal));
        }

        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken)
        {
            Labels++;
            return Task.FromResult(AdapterResult<bool>.Failure(LocalCircuitRefusal));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class LocalCircuitReturnPort : IReturnPort
    {
        public int Executions { get; private set; }

        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult(AdapterResult<ReturnActionResult>.Failure(LocalCircuitRefusal));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
