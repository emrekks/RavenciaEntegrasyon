using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3b (16 Nov 2026): the order ingestion lives in OrderIngestionService; the processor keeps one service
// per run (bound to that run's context) and its methods of the same names call into it.
public sealed partial class MarketplaceJobProcessor
{
    private OrderIngestionService? orderIngestion;

    private OrderIngestionService OrderIngestion =>
        orderIngestion is { } current && ReferenceEquals(current.Run, run)
            ? current
            : orderIngestion = new OrderIngestionService(db, issues, cursors, timeProvider, invoiceBilling, run);

    internal Task UpsertOrders(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteOrder> remotes, CancellationToken cancellationToken) =>
        OrderIngestion.UpsertOrders(tenantId, connectionId, remotes, cancellationToken);

    private Task<bool> UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken, OrderIngestionService.OrderIngestionBatch? batch = null, bool saveChanges = true, bool persistFinancialObservations = false) =>
        OrderIngestion.UpsertOrder(tenantId, connectionId, remote, cancellationToken, batch, saveChanges, persistFinancialObservations);

    private Task IngestOrderPageAsync(Guid tenantId, Guid connectionId, AdapterPageResult<RemoteOrder> page, string issueKeyPrefix, CancellationToken cancellationToken) =>
        OrderIngestion.IngestOrderPageAsync(tenantId, connectionId, page, issueKeyPrefix, cancellationToken);

    private void NoteRefusedOrders(IReadOnlyList<AdapterPageIssue>? issues) => OrderIngestion.NoteRefusedOrders(issues);

    private Task MergeMarketplaceInvoiceState(ShipmentPackage package, RemotePackage remotePackage, CancellationToken cancellationToken, string? discoverySource = null) =>
        OrderIngestion.MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken, discoverySource);

    internal Task<OrderIngestionService.OrderLineRematchOutcome> RematchUnmatchedOrderLines(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, int batchSize = OrderIngestionService.OrderLineRematchBatchSize) =>
        OrderIngestion.RematchUnmatchedOrderLines(tenantId, connectionId, cancellationToken, batchSize);

    private Task<IReadOnlyList<string>> RefusedOrdersToRereadAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<string> listed, DateTimeOffset now, CancellationToken cancellationToken) =>
        OrderIngestion.RefusedOrdersToRereadAsync(tenantId, connectionId, listed, now, cancellationToken);

    // Called by the stock tests through reflection, as before the move.
    private Task ProjectOrderReservations(Guid tenantId, Guid connectionId, IEnumerable<(OrderLine Line, DateTimeOffset ModifiedAt)> sourceLines, CancellationToken cancellationToken) =>
        OrderIngestion.ProjectOrderReservations(tenantId, connectionId, sourceLines, cancellationToken);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        OrderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

    private static StockLedgerSource CatalogStockSource(string movementType, Guid connectionId, string eventId, DateTimeOffset occurredAt, DateTimeOffset now) =>
        OrderIngestionService.CatalogStockSource(movementType, connectionId, eventId, occurredAt, now);
}
