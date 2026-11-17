using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: the shipment side lives in ShipmentService; the facade keeps the interface and forwards.
public sealed partial class MarketplaceSalesService
{
    private ShipmentService? shipments;
    internal ShipmentService Shipments => shipments ??= new(Sales, this);

    public Task<PageResult<ShipmentView>> ShipmentsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken) =>
        Shipments.ShipmentsAsync(tenantId, limit, after, status, cancellationToken);
    public Task<ServiceResult<ShipmentDetailView>> ShipmentAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        Shipments.ShipmentAsync(tenantId, id, cancellationToken);
    public Task<ServiceResult<IReadOnlyList<RemoteCargoCompany>>> ChangeableCargoCompaniesAsync(Guid tenantId, Guid packageId, string correlationId, CancellationToken cancellationToken) =>
        Shipments.ChangeableCargoCompaniesAsync(tenantId, packageId, correlationId, cancellationToken);
    public Task<ServiceResult<CreateOrderPackageResult>> CreateOrderPackageInstantAsync(Guid tenantId, Guid orderId, long expectedVersion, OrderPackageCreateRequest command, string idempotencyKey, string correlationId, CancellationToken cancellationToken) =>
        Shipments.CreateOrderPackageInstantAsync(tenantId, orderId, expectedVersion, command, idempotencyKey, correlationId, cancellationToken);
    public Task<ServiceResult<ShopifyOrderStatusResult>> SetShopifyOrderStatusAsync(Guid tenantId, Guid userId, Guid orderId, long expectedVersion, string? status, string idempotencyKey, string correlationId, CancellationToken cancellationToken) =>
        Shipments.SetShopifyOrderStatusAsync(tenantId, userId, orderId, expectedVersion, status, idempotencyKey, correlationId, cancellationToken);
    public Task<bool> MarkLabelPrintedAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        Shipments.MarkLabelPrintedAsync(tenantId, orderId, cancellationToken);
    public Task<ServiceResult<Guid>> EnqueueShipmentActionAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken) =>
        Shipments.EnqueueShipmentActionAsync(tenantId, packageId, expectedVersion, command, idempotencyKey, correlationId, cancellationToken);
    public Task<ServiceResult<ShipmentView>> ProcessShipmentInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken) =>
        Shipments.ProcessShipmentInstantAsync(tenantId, packageId, expectedVersion, idempotencyKey, correlationId, cancellationToken);
    public Task<ServiceResult<ShipmentView>> ChangeCargoProviderInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken) =>
        Shipments.ChangeCargoProviderInstantAsync(tenantId, packageId, expectedVersion, command, idempotencyKey, correlationId, cancellationToken);
    public Task<ServiceResult<Guid>> EnqueueCommonLabelAsync(Guid tenantId, Guid packageId, long expectedVersion, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken, string format = "ZPL") =>
        Shipments.EnqueueCommonLabelAsync(tenantId, packageId, expectedVersion, boxQuantity, volumetricHeight, idempotencyKey, correlationId, cancellationToken, format);
    public Task<ServiceResult<Guid>> EnqueueLabelCapabilityProbeAsync(Guid tenantId, Guid actorUserId, Guid packageId, long expectedVersion, string capabilityCode, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken) =>
        Shipments.EnqueueLabelCapabilityProbeAsync(tenantId, actorUserId, packageId, expectedVersion, capabilityCode, boxQuantity, volumetricHeight, idempotencyKey, correlationId, cancellationToken);
}
