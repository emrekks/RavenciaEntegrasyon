using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-10 (15 Nov 2026) characterization: what the package action, the common label and the return decision write to
// their ExternalEffect record and to their own state for each answer of the marketplace (accepted, unclear, quota,
// refused), and what a second run does after an unclear answer. Written against the code before ExternalEffectGuard, so
// moving the sites onto the guard must keep every line of it.
public sealed partial class PostgreSqlTenantIsolationTests
{
    internal static AdapterError? EffectAnswer(string outcome) => outcome switch
    {
        "success" => null,
        "uncertain" => new AdapterError(AdapterErrorClass.Remote5xx, "TRENDYOL_REMOTE_5XX", "Trendyol 500 döndü.", 500, null, "req-5xx"),
        "quota" => new AdapterError(AdapterErrorClass.RateLimit, "TRENDYOL_RATE_LIMITED", "Trendyol istek sınırı aşıldı.", 429, TimeSpan.FromSeconds(20), "req-429"),
        "refused" => new AdapterError(AdapterErrorClass.Validation, "TRENDYOL_VALIDATION", "Talep reddedildi.", 400, null, "req-400"),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    [PostgreSqlTheory]
    [InlineData("success", JobCompletionKind.Succeeded, null, true)]
    [InlineData("uncertain", JobCompletionKind.ManualReview, "EXTERNAL_EFFECT_AMBIGUOUS", false)]
    [InlineData("quota", JobCompletionKind.Retry, "TRENDYOL_RATE_LIMITED", null)]
    [InlineData("refused", JobCompletionKind.Blocked, "TRENDYOL_VALIDATION", null)]
    public async Task EffectSites_PackageAction(string outcome, JobCompletionKind kind, string? code, bool? effectCompleted)
    {
        var (tenant, connection, package, actionJob, _, _) = await SeedEffectPackageAsync($"effect-ship-{outcome}");
        var port = new EffectOrderPort(EffectAnswer(outcome));
        try
        {
            var result = await RunEffectOrderJobAsync(tenant.Id, connection.Id, port, MarketplaceJobTypes.ShipmentAction, actionJob.PayloadJson);
            Assert.Equal(kind, result.Kind);
            Assert.Equal(code, result.ErrorCode);
            Assert.Equal(1, port.PackageActions);
            await using (var check = fixture.CreateContext())
            {
                var effects = await check.ExternalEffectRecords.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
                if (effectCompleted is null) Assert.Empty(effects);
                else
                {
                    var effect = Assert.Single(effects);
                    Assert.Equal(MarketplaceJobTypes.ShipmentAction, effect.EffectType);
                    Assert.Equal(actionJob.EffectIdempotencyKey, effect.IdempotencyKey);
                    Assert.Equal(connection.Id, effect.ConnectionId);
                    Assert.Equal(package.Id, effect.TargetId);
                    Assert.Equal("PICKING", effect.Action);
                    Assert.Null(effect.ExpectedValue);
                    Assert.Equal(effectCompleted.Value, effect.CompletedAt is not null);
                }
            }

            // The second run: after an unclear answer nothing is sent again; after an accepted one the job is done; after a
            // quota answer or a refusal the request goes again.
            var again = await RunEffectOrderJobAsync(tenant.Id, connection.Id, port, MarketplaceJobTypes.ShipmentAction, actionJob.PayloadJson);
            if (outcome == "uncertain")
            {
                Assert.Equal(JobCompletionKind.ManualReview, again.Kind);
                Assert.Equal("EXTERNAL_EFFECT_AMBIGUOUS", again.ErrorCode);
                Assert.Equal(1, port.PackageActions);
            }
            else if (outcome == "success")
            {
                Assert.Equal(JobCompletionKind.Succeeded, again.Kind);
                Assert.Equal(1, port.PackageActions);
            }
            else Assert.Equal(2, port.PackageActions);
        }
        finally { await CleanupEffectPackageAsync(tenant.Id); }
    }

    [PostgreSqlTheory]
    [InlineData("success", JobCompletionKind.Retry, "COMMON_LABEL_PENDING", true, "POLLING")]
    [InlineData("uncertain", JobCompletionKind.ManualReview, "EXTERNAL_EFFECT_AMBIGUOUS", false, "MANUAL_REVIEW")]
    // J2 (15 Nov 2026): a quota answer keeps the attempt open (PENDING, no end time) while the job sends it again.
    [InlineData("quota", JobCompletionKind.Retry, "TRENDYOL_RATE_LIMITED", null, "PENDING")]
    [InlineData("refused", JobCompletionKind.Blocked, "TRENDYOL_VALIDATION", null, "FAILED")]
    public async Task EffectSites_CommonLabel(string outcome, JobCompletionKind kind, string code, bool? effectCompleted, string attemptStatus)
    {
        var (tenant, connection, _, _, labelJob, attempt) = await SeedEffectPackageAsync($"effect-label-{outcome}");
        var port = new EffectOrderPort(EffectAnswer(outcome));
        try
        {
            var result = await RunEffectOrderJobAsync(tenant.Id, connection.Id, port, MarketplaceJobTypes.CommonLabel, labelJob.PayloadJson);
            Assert.Equal(kind, result.Kind);
            Assert.Equal(code, result.ErrorCode);
            Assert.Equal(1, port.Labels);
            await using (var check = fixture.CreateContext())
            {
                var effects = await check.ExternalEffectRecords.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
                if (effectCompleted is null) Assert.Empty(effects);
                else
                {
                    var effect = Assert.Single(effects);
                    Assert.Equal(MarketplaceJobTypes.CommonLabel, effect.EffectType);
                    Assert.Equal(labelJob.EffectIdempotencyKey, effect.IdempotencyKey);
                    Assert.Null(effect.ConnectionId);
                    Assert.Null(effect.TargetId);
                    Assert.Null(effect.Action);
                    Assert.Equal(effectCompleted.Value, effect.CompletedAt is not null);
                }
                var savedAttempt = await check.ShipmentDocumentAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt.Id);
                Assert.Equal(attemptStatus, savedAttempt.Status);
                Assert.Equal(outcome == "success" ? null : outcome == "uncertain" ? "EXTERNAL_EFFECT_AMBIGUOUS" : code, savedAttempt.ErrorCode);
                Assert.Equal(attemptStatus is "FAILED" or "MANUAL_REVIEW", savedAttempt.CompletedAt is not null);
            }
            if (outcome == "uncertain")
            {
                var again = await RunEffectOrderJobAsync(tenant.Id, connection.Id, port, MarketplaceJobTypes.CommonLabel, labelJob.PayloadJson);
                Assert.Equal(JobCompletionKind.ManualReview, again.Kind);
                Assert.Equal("EXTERNAL_EFFECT_AMBIGUOUS", again.ErrorCode);
                Assert.Equal(1, port.Labels);
            }
        }
        finally { await CleanupEffectPackageAsync(tenant.Id); }
    }

    [PostgreSqlTheory]
    [InlineData("success", JobCompletionKind.Succeeded, null, true, "SUBMITTED")]
    [InlineData("uncertain", JobCompletionKind.ManualReview, "EXTERNAL_EFFECT_AMBIGUOUS", false, "MANUAL_REVIEW")]
    [InlineData("quota", JobCompletionKind.Retry, "TRENDYOL_RATE_LIMITED", null, "PENDING")]
    [InlineData("refused", JobCompletionKind.Blocked, "TRENDYOL_VALIDATION", null, "FAILED")]
    public async Task EffectSites_ReturnDecision(string outcome, JobCompletionKind kind, string? code, bool? effectCompleted, string decisionStatus)
    {
        var name = $"effect-return-{outcome}";
        var tenant = NewTenant(name);
        var connection = NewQuestionConnection(tenant);
        var orderId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"{name}-order", isReturnClaim: false, fixture.Now);
        var orderLine = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = orderId, ExternalLineId = $"{name}-line", Sku = "SKU-1", TitleSnapshot = "Test ürün", OrderedQuantity = 1, UnitPrice = 100, VatRate = 20, RawStatus = "Delivered" };
        var claim = NewReturnLifecycleClaim(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), $"{name}-claim", fixture.Now, hasCargo: false);
        claim.Status = ReturnClaimStatus.ActionRequired;
        claim.RawStatus = "WaitingInAction";
        var decision = new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, Action = "APPROVE", IdempotencyKey = $"{name}-{Guid.NewGuid():N}", Status = "PENDING", ActorUserId = Guid.CreateVersion7(), CreatedAt = fixture.Now };
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.Add(order);
            seedDb.OrderLines.Add(orderLine);
            seedDb.ReturnClaims.Add(claim);
            seedDb.ReturnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = $"{name}-line", Quantity = 1, RawStatus = "WaitingInAction" });
            seedDb.ReturnDecisions.Add(decision);
            await seedDb.SaveChangesAsync();
        }
        var port = new EffectReturnPort(EffectAnswer(outcome));
        async Task<JobExecutionResult> RunAsync()
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, port, null!, null!,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
            return await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnAction, $$"""{"decisionId":"{{decision.Id}}"}""", name, CancellationToken.None);
        }
        try
        {
            var result = await RunAsync();
            Assert.Equal(kind, result.Kind);
            Assert.Equal(code, result.ErrorCode);
            Assert.Equal(1, port.Executions);
            await using (var check = fixture.CreateContext())
            {
                var effects = await check.ExternalEffectRecords.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
                if (effectCompleted is null) Assert.Empty(effects);
                else
                {
                    var effect = Assert.Single(effects);
                    Assert.Equal(MarketplaceJobTypes.ReturnAction, effect.EffectType);
                    Assert.Equal(decision.IdempotencyKey, effect.IdempotencyKey);
                    Assert.Equal(connection.Id, effect.ConnectionId);
                    Assert.Equal(claim.Id, effect.TargetId);
                    Assert.Equal("RETURN_APPROVE", effect.Action);
                    Assert.Equal(effectCompleted.Value, effect.CompletedAt is not null);
                }
                var saved = await check.ReturnDecisions.AsNoTracking().SingleAsync(x => x.Id == decision.Id);
                Assert.Equal(decisionStatus, saved.Status);
                Assert.Equal(outcome == "success" ? null : EffectAnswer(outcome)!.Code, saved.ErrorCode);
                Assert.Equal(decisionStatus is "FAILED" or "MANUAL_REVIEW", saved.CompletedAt is not null);
                Assert.Equal(outcome == "success" ? "return-op" : EffectAnswer(outcome)!.RemoteRequestId, saved.ExternalOperationId);
            }
            var again = await RunAsync();
            if (outcome == "uncertain")
            {
                Assert.Equal(JobCompletionKind.ManualReview, again.Kind);
                Assert.Equal(1, port.Executions);
            }
            else if (outcome == "success")
            {
                Assert.Equal(JobCompletionKind.Succeeded, again.Kind);
                Assert.Equal(1, port.Executions);
            }
            else if (outcome == "quota") Assert.Equal(2, port.Executions);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.ExternalEffectRecords.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.OperationalIssues.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.ReturnDecisions.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.ReturnLines.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.OrderLines.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteReturnLifecycleTestTenantAsync(tenant.Id);
        }
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection, ShipmentPackage Package, IntegrationJob ActionJob, IntegrationJob LabelJob, ShipmentDocumentAttempt Attempt)> SeedEffectPackageAsync(string name)
    {
        var tenant = NewTenant(name);
        var connection = NewQuestionConnection(tenant);
        var orderId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"{name}-order", isReturnClaim: false, fixture.Now);
        var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), $"{name}-package", fixture.Now);
        package.CargoTrackingNumber = $"{name}-track";
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
        await using var seedDb = fixture.CreateContext();
        seedDb.Tenants.Add(tenant);
        seedDb.PlatformConnections.Add(connection);
        seedDb.Orders.Add(order);
        seedDb.ShipmentPackages.Add(package);
        seedDb.IntegrationJobs.AddRange(actionJob, labelJob);
        seedDb.ShipmentDocumentAttempts.Add(attempt);
        await seedDb.SaveChangesAsync();
        return (tenant, connection, package, actionJob, labelJob, attempt);
    }

    private async Task<JobExecutionResult> RunEffectOrderJobAsync(Guid tenantId, Guid connectionId, EffectOrderPort port, string jobType, string payload)
    {
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, port, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(tenantId, connectionId, jobType, payload, "effect-sites", CancellationToken.None);
    }

    private async Task CleanupEffectPackageAsync(Guid tenantId)
    {
        await using var cleanupDb = fixture.CreateContext();
        await cleanupDb.ExternalEffectRecords.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanupDb.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanupDb.ShipmentDocumentAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanupDb.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await DeleteInvoiceTestTenantAsync(tenantId);
    }

    // Answers each write with the given refusal (null: accepted); a read-back finds nothing, so the processor records it as
    // pending instead of reading an order.
    private sealed class EffectOrderPort(AdapterError? answer) : IOrderPort
    {
        public int PackageActions { get; private set; }
        public int Labels { get; private set; }

        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
        {
            PackageActions++;
            return Task.FromResult(answer is null
                ? AdapterResult<PackageActionResult>.Success(new PackageActionResult(command.ExternalPackageId, "OK", "package-op"))
                : AdapterResult<PackageActionResult>.Failure(answer));
        }

        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken)
        {
            Labels++;
            return Task.FromResult(answer is null ? AdapterResult<bool>.Success(true) : AdapterResult<bool>.Failure(answer));
        }

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteOrder>.Failure(new AdapterError(AdapterErrorClass.NotFound, "TRENDYOL_ORDER_NOT_FOUND", "Sipariş bulunamadı.", 404, null, null)));
        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EffectReturnPort(AdapterError? answer) : IReturnPort
    {
        public int Executions { get; private set; }

        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult(answer is null
                ? AdapterResult<ReturnActionResult>.Success(new ReturnActionResult(command.ExternalClaimId, "OK", "return-op"))
                : AdapterResult<ReturnActionResult>.Failure(answer));
        }

        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteReturnClaim>.Failure(new AdapterError(AdapterErrorClass.NotFound, "TRENDYOL_CLAIM_NOT_FOUND", "İade bulunamadı.", 404, null, null)));
        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
