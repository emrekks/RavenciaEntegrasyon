namespace MarketplaceHub.Application;

// Siparişler › Yeniden Gönderim gear (owner, 23 Nov 2026): re-shipments the owner adds by hand, a marketplace connection
// and an order number each. Panel only: nothing is sent to a marketplace, no stock or invoice changes.
public sealed record ManualResendView(Guid Id, Guid OrderId, string OrderNumber, Guid ConnectionId, string PlatformCode, string ConnectionName,
    DateTimeOffset CreatedAt, DateTimeOffset? ShippedAt, long Version);

public sealed record ManualResendListView(IReadOnlyList<ManualResendView> Items);

public sealed record ManualResendCreateCommand(Guid ConnectionId, string? OrderNumber);

// On an order row: the hand-added re-shipment it is listed for. Shipped once the owner marked it handed to the carrier;
// Id and Version address that mark (POST /manual-resends/{id}/shipped, If-Match).
public sealed record OrderManualResendView(Guid Id, long Version, bool Shipped, DateTimeOffset? ShippedAt);

public interface IManualResendService
{
    Task<ManualResendListView> ListAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<ManualResendView>> AddAsync(Guid tenantId, Guid userId, ManualResendCreateCommand command, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> RemoveAsync(Guid tenantId, Guid userId, Guid id, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ManualResendView>> MarkShippedAsync(Guid tenantId, Guid userId, Guid id, long expectedVersion, bool marked, string correlationId, CancellationToken cancellationToken);
}
