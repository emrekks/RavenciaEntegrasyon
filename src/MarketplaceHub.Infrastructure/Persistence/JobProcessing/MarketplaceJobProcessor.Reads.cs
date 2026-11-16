using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3e (16 Nov 2026): the order and return read loops live in OrderReadService and ReturnReadService, one
// per run like the ingestion services they store through. The read jobs run in OrderReadRunHandler and
// ReturnReadRunHandler (stage 4c, 4d); these members are what the processor's own code (webhooks, the invoice checks, the
// cargo-info check) still calls.
public sealed partial class MarketplaceJobProcessor
{
    private OrderReadService OrderRead => Services.OrderRead;

    private ReturnReadService ReturnRead => Services.ReturnRead;

    private Task<bool> SyncHepsiburadaOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken) =>
        OrderRead.SyncHepsiburadaOrders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);

    private Task SaveRefusedOrdersForRereadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        OrderRead.SaveRefusedOrdersForRereadAsync(tenantId, connectionId, cancellationToken);

    // A Hepsiburada unpack notification marks the package as the package action does (ActionJobService).
    private Task MarkHepsiburadaPackageUnpackedAsync(Guid tenantId, Guid connectionId, Guid packageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Services.ActionJobs.MarkHepsiburadaPackageUnpackedAsync(tenantId, connectionId, packageId, now, cancellationToken);

    private static string? TrendyolWebhookStorefront(string rawPackageJson) => OrderReadService.TrendyolWebhookStorefront(rawPackageJson);

    private Task<bool> SyncReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken) =>
        ReturnRead.SyncReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
}
