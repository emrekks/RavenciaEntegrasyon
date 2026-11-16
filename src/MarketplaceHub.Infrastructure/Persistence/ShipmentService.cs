using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: the shipment side of the sales service: the package list and detail, the instant package actions
// (picking, cargo change, package create, Shopify status), the label print mark and the queued shipment and label jobs.
// Moved out of MarketplaceSalesService unchanged; the facade (IMarketplaceSalesService) forwards to it. The order read
// it queues after an action is the facade's job enqueue (MarketplaceSalesService.Jobs), reached through jobs.
internal sealed partial class ShipmentService(SalesContext sales, MarketplaceSalesService jobs)
{
    private readonly AppDbContext db = sales.Db;
    private readonly CursorCodec cursors = sales.Cursors;
    private readonly IConfiguration configuration = sales.Configuration;
    private readonly IOrderPort orders = sales.Orders;
    private readonly TimeProvider timeProvider = sales.TimeProvider;

    private IQueryable<ShipmentPackage> CurrentShipmentPackages() => sales.CurrentShipmentPackages();
    private Task<bool> IsStageConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => sales.IsStageConnection(tenantId, connectionId, cancellationToken);
    private Task<bool> IsProductionConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => sales.IsProductionConnection(tenantId, connectionId, cancellationToken);
    private Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => sales.WritesEnabled(tenantId, connectionId, cancellationToken);
    private Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => sales.ExternalWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);
    private Task<(bool Enabled, int IntervalSeconds)> ExternalWritePolicyAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => sales.ExternalWritePolicyAsync(tenantId, connectionId, resourceType, cancellationToken);
    private Task<IReadOnlyList<string>> CapabilityValues(Guid tenantId, Guid connectionId, string code, string property, CancellationToken cancellationToken) => sales.CapabilityValues(tenantId, connectionId, code, property, cancellationToken);
    private Task<bool> Supported(Guid tenantId, Guid connectionId, string code, CancellationToken cancellationToken) => sales.Supported(tenantId, connectionId, code, cancellationToken);
    private Task<ServiceResult<T>> VerifyLaterAsync<T>(ExternalEffectRecord effect, CancellationToken cancellationToken) => sales.VerifyLaterAsync<T>(effect, cancellationToken);
    private static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db) => MarketplaceSalesService.CurrentShipmentPackages(db);
    private IntegrationJob NewJob(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId) => jobs.NewJob(tenantId, connectionId, type, dedup, payload, correlationId);
    private Task<ServiceResult<Guid>> EnqueueOrderSyncAsync(Guid tenantId, Guid connectionId, string? externalOrderId, bool full, string correlationId, CancellationToken cancellationToken, string? packageNumber = null) =>
        jobs.EnqueueOrderSyncAsync(tenantId, connectionId, externalOrderId, full, correlationId, cancellationToken, packageNumber);
}
