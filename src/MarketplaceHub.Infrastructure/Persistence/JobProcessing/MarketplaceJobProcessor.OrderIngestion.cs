using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3b (16 Nov 2026): the order ingestion lives in OrderIngestionService; the processor keeps one service
// per run (bound to that run's context). What remains here is what tests call on the processor.
public sealed partial class MarketplaceJobProcessor
{
    private OrderIngestionService OrderIngestion => Services.OrderIngestion;

    internal Task UpsertOrders(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteOrder> remotes, CancellationToken cancellationToken) =>
        OrderIngestion.UpsertOrders(tenantId, connectionId, remotes, cancellationToken);

    internal Task<OrderIngestionService.OrderLineRematchOutcome> RematchUnmatchedOrderLines(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, int batchSize = OrderIngestionService.OrderLineRematchBatchSize) =>
        OrderIngestion.RematchUnmatchedOrderLines(tenantId, connectionId, cancellationToken, batchSize);

    // Called by the stock tests through reflection, as before the move.
    private Task ProjectOrderReservations(Guid tenantId, Guid connectionId, IEnumerable<(OrderLine Line, DateTimeOffset ModifiedAt)> sourceLines, CancellationToken cancellationToken) =>
        OrderIngestion.ProjectOrderReservations(tenantId, connectionId, sourceLines, cancellationToken);
}
