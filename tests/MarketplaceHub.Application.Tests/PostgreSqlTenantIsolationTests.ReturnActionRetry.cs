using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-4 (15 Nov 2026): a return decision refused with a quota answer (the job retries it) stays waiting instead of
// showing "Başarısız" while the job still sends it; a final refusal still fails it.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task ReturnActionRetry_AQuotaRefusalKeepsTheDecisionPending()
    {
        var (saved, result, effects) = await RunReturnDecisionAsync("return-retry-quota", new AdapterError(AdapterErrorClass.RateLimit, "TRENDYOL_RATE_LIMITED", "Trendyol istek sınırı aşıldı.", 429, TimeSpan.FromSeconds(20), null));

        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.Equal("TRENDYOL_RATE_LIMITED", result.ErrorCode);
        Assert.Equal("PENDING", saved.Status);
        Assert.Null(saved.CompletedAt);
        Assert.Equal("TRENDYOL_RATE_LIMITED", saved.ErrorCode);
        Assert.False(effects);
    }

    [PostgreSqlFact]
    public async Task ReturnActionRetry_AFinalRefusalFailsTheDecision()
    {
        var (saved, result, effects) = await RunReturnDecisionAsync("return-retry-final", new AdapterError(AdapterErrorClass.Validation, "TRENDYOL_VALIDATION", "Talep reddedildi.", 400, null, null));

        Assert.Equal(JobCompletionKind.Blocked, result.Kind);
        Assert.Equal("FAILED", saved.Status);
        Assert.NotNull(saved.CompletedAt);
        Assert.False(effects);
    }

    // Owner, 18 Nov 2026: an approval queued for a Trendyol claim later known as an exchange request is not sent (the
    // refusing port would answer TRENDYOL_VALIDATION if it were asked).
    [PostgreSqlFact]
    public async Task ReturnActionRetry_AQueuedApprovalOfATrendyolExchangeIsNotSent()
    {
        var (saved, result, effects) = await RunReturnDecisionAsync("return-exchange-queued", new AdapterError(AdapterErrorClass.Validation, "TRENDYOL_VALIDATION", "Talep reddedildi.", 400, null, null), ReturnCustomerRequestPolicy.Change);

        Assert.Equal(JobCompletionKind.Blocked, result.Kind);
        Assert.Equal(ReturnCustomerRequestPolicy.TrendyolExchangeApproveBlockedCode, result.ErrorCode);
        Assert.Equal(("FAILED", ReturnCustomerRequestPolicy.TrendyolExchangeApproveBlockedCode), (saved.Status, saved.ErrorCode));
        Assert.False(effects);
    }

    private async Task<(ReturnDecision Saved, JobExecutionResult Result, bool EffectsLeft)> RunReturnDecisionAsync(string name, AdapterError refusal, string? customerRequest = null)
    {
        var tenant = NewTenant(name);
        var connection = NewQuestionConnection(tenant);
        var orderId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"{name}-order", isReturnClaim: false, fixture.Now);
        var orderLine = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = orderId, ExternalLineId = $"{name}-line", Sku = "SKU-1", TitleSnapshot = "Test ürün", OrderedQuantity = 1, UnitPrice = 100, VatRate = 20, RawStatus = "Delivered" };
        var claim = NewReturnLifecycleClaim(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), $"{name}-claim", fixture.Now, hasCargo: false);
        claim.Status = ReturnClaimStatus.ActionRequired;
        claim.RawStatus = "WaitingInAction";
        claim.CustomerRequest = customerRequest;
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

        try
        {
            JobExecutionResult result;
            await using (var db = fixture.CreateContext())
            {
                var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, new RefusingReturnPort(refusal), null!, null!,
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
                result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnAction, $$"""{"decisionId":"{{decision.Id}}"}""", name, CancellationToken.None);
            }
            await using var check = fixture.CreateContext();
            var saved = await check.ReturnDecisions.AsNoTracking().SingleAsync(x => x.Id == decision.Id);
            return (saved, result, await check.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenant.Id));
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

    private sealed class RefusingReturnPort(AdapterError refusal) : IReturnPort
    {
        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<ReturnActionResult>.Failure(refusal));

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
