using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: the return decisions live in ReturnDecisionService; the facade keeps the interface and forwards.
public sealed partial class MarketplaceSalesService
{
    private ReturnDecisionService? returnDecisions;
    internal ReturnDecisionService ReturnDecisions => returnDecisions ??= new(Sales, ReturnQueries);

    private static readonly IReadOnlyList<string> ReturnActions = ReturnDecisionService.ReturnActions;
    private IQueryable<MarketplaceHub.Domain.ReturnDecision> OpenReturnDecisions(Guid tenantId, Guid claimId, bool sentDecisionHoldsClaim) => ReturnDecisions.OpenReturnDecisions(tenantId, claimId, sentDecisionHoldsClaim);

    public Task<ServiceResult<ReturnDetailView>> MarkReturnReceivedAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken) =>
        ReturnDecisions.MarkReturnReceivedAsync(tenantId, userId, claimId, expectedVersion, idempotencyKey, correlationId, cancellationToken);
    public Task<ServiceResult<ReturnDetailView>> MarkExchangeApprovedAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, bool marked, string correlationId, CancellationToken cancellationToken) =>
        ReturnDecisions.MarkExchangeApprovedAsync(tenantId, userId, claimId, expectedVersion, marked, correlationId, cancellationToken);
    public Task<ServiceResult<ReturnDetailView>> ProcessReturnActionInstantAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, ReturnDecisionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken) =>
        ReturnDecisions.ProcessReturnActionInstantAsync(tenantId, userId, claimId, expectedVersion, command, idempotencyKey, correlationId, cancellationToken);
    public Task<ServiceResult<ReturnDetailView>> ApplyDispositionAsync(Guid tenantId, Guid userId, Guid claimId, ReturnDispositionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken) =>
        ReturnDecisions.ApplyDispositionAsync(tenantId, userId, claimId, command, idempotencyKey, correlationId, cancellationToken);
}
