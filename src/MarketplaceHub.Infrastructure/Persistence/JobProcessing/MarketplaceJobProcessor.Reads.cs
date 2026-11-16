using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3e (16 Nov 2026): the order and return read loops live in OrderReadService and ReturnReadService, one
// per run like the ingestion services they store through; the processor's methods of the same names call into them.
public sealed partial class MarketplaceJobProcessor
{
    private OrderReadService? orderRead;
    private ReturnReadService? returnRead;

    private OrderReadService OrderRead =>
        orderRead is { } current && ReferenceEquals(current.Run, run)
            ? current
            : orderRead = new OrderReadService(db, issues, cursors, orders, orderPackages, hepsiburadaInvoiceStatus, configuration, timeProvider, OrderIngestion, run);

    private ReturnReadService ReturnRead =>
        returnRead is { } current && ReferenceEquals(current.Run, run)
            ? current
            : returnRead = new ReturnReadService(db, issues, cursors, returns, orders, configuration, timeProvider, OrderIngestion, ReturnIngestion, run);

    private Task<bool> SyncOrdersAndMatchLines(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken) =>
        OrderRead.SyncOrdersAndMatchLines(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);

    private Task<bool> SyncHepsiburadaOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken) =>
        OrderRead.SyncHepsiburadaOrders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);

    private Task<bool> SyncOpenOrders(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) =>
        OrderRead.SyncOpenOrders(tenantId, connectionId, correlationId, cancellationToken);

    private Task<bool> ReconcileTrendyolCargoInfo(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) =>
        OrderRead.ReconcileTrendyolCargoInfo(tenantId, connectionId, correlationId, cancellationToken);

    private Task<JobExecutionResult> ReconcileTrendyolOrderWindows(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken) =>
        OrderRead.ReconcileTrendyolOrderWindows(tenantId, connectionId, payloadJson, correlationId, cancellationToken);

    private Task<bool> ReconcileOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken) =>
        OrderRead.ReconcileOrders(tenantId, connectionId, payloadJson, correlationId, cancellationToken);

    private Task<OrderLookupHint?> OrderHint(Guid tenantId, Guid connectionId, string externalOrderId, CancellationToken cancellationToken) =>
        OrderRead.OrderHint(tenantId, connectionId, externalOrderId, cancellationToken);

    private Task ReadMissingHepsiburadaOrdersAsync(Guid tenantId, Guid connectionId, string correlationId, IEnumerable<(string OrderNumber, string? PackageNumber)> references, CancellationToken cancellationToken) =>
        OrderRead.ReadMissingHepsiburadaOrdersAsync(tenantId, connectionId, correlationId, references, cancellationToken);

    private Task SaveRefusedOrdersForRereadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        OrderRead.SaveRefusedOrdersForRereadAsync(tenantId, connectionId, cancellationToken);

    private static string? TrendyolWebhookStorefront(string rawPackageJson) => OrderReadService.TrendyolWebhookStorefront(rawPackageJson);

    private Task<bool> SyncReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken) =>
        ReturnRead.SyncReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);

    private Task<bool> SyncOpenReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) =>
        ReturnRead.SyncOpenReturns(tenantId, connectionId, correlationId, cancellationToken);

    private Task<bool> ReconcileReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken) =>
        ReturnRead.ReconcileReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
}
