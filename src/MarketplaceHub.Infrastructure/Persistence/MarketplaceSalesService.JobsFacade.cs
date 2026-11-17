using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: the sync job enqueues live in SalesJobService; the facade keeps the interface and forwards.
public sealed partial class MarketplaceSalesService
{
    private SalesJobService? jobs;
    internal SalesJobService Jobs => jobs ??= new(Sales);

    public Task<ServiceResult<Guid>> EnqueueOrderSyncAsync(Guid tenantId, Guid connectionId, string? externalOrderId, bool full, string correlationId, CancellationToken cancellationToken, string? packageNumber = null) =>
        Jobs.EnqueueOrderSyncAsync(tenantId, connectionId, externalOrderId, full, correlationId, cancellationToken, packageNumber);
    public Task<ServiceResult<Guid>> EnqueueProductSyncAsync(Guid tenantId, Guid connectionId, bool full, bool newOnly, bool existingOnly, bool mappingOnly, bool optionsOnly, bool includeArchived, bool includeDrafts, bool includePendingApproval, bool updateExistingProducts, bool updatePrices, bool updateStock, string? productLookup, string correlationId, CancellationToken cancellationToken) =>
        Jobs.EnqueueProductSyncAsync(tenantId, connectionId, full, newOnly, existingOnly, mappingOnly, optionsOnly, includeArchived, includeDrafts, includePendingApproval, updateExistingProducts, updatePrices, updateStock, productLookup, correlationId, cancellationToken);
    public Task<ServiceResult<CatalogStructureRunView>> EnqueueCatalogStructureAsync(Guid tenantId, Guid connectionId, string mode, string correlationId, CancellationToken cancellationToken) =>
        Jobs.EnqueueCatalogStructureAsync(tenantId, connectionId, mode, correlationId, cancellationToken);
    public Task<ServiceResult<CatalogStructureRunView>> CatalogStructureRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken) =>
        Jobs.CatalogStructureRunAsync(tenantId, runId, cancellationToken);
    public Task<IReadOnlyList<CatalogStructureRunView>> CatalogStructureRunsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Jobs.CatalogStructureRunsAsync(tenantId, connectionId, cancellationToken);
    public Task<ServiceResult<Guid>> EnqueueReferenceSyncAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken) =>
        Jobs.EnqueueReferenceSyncAsync(tenantId, connectionId, resourceType, parentExternalId, correlationId, cancellationToken);
    public Task<ServiceResult<Guid>> EnqueueStageTestOrderAsync(Guid tenantId, Guid actorUserId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken) =>
        Jobs.EnqueueStageTestOrderAsync(tenantId, actorUserId, connectionId, idempotencyKey, correlationId, cancellationToken);
    public Task<ServiceResult<Guid>> EnqueueReturnSyncAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) =>
        Jobs.EnqueueReturnSyncAsync(tenantId, connectionId, correlationId, cancellationToken);
    public Task<ServiceResult<Guid>> EnqueueQuestionSyncAsync(Guid tenantId, Guid connectionId, string? kind, string correlationId, CancellationToken cancellationToken) =>
        Jobs.EnqueueQuestionSyncAsync(tenantId, connectionId, kind, correlationId, cancellationToken);

    internal IntegrationJob NewJob(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId) => Jobs.NewJob(tenantId, connectionId, type, dedup, payload, correlationId);
    internal static int Priority(string type) => SalesJobService.Priority(type);
}
