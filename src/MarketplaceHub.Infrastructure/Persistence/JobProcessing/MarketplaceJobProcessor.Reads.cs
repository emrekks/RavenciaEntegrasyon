using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 5 (16 Nov 2026): every job family runs in its own service through a run handler. The processor keeps
// only what tests still call on it: the Hepsiburada address event of the webhook ingest (WebhookIngestService).
public sealed partial class MarketplaceJobProcessor
{
    internal Task ApplyHepsiburadaShippingAddressAsync(Guid tenantId, Guid connectionId, string orderNumber, JsonElement root, string eventKey, CancellationToken cancellationToken) =>
        Services.WebhookIngest.ApplyHepsiburadaShippingAddressAsync(tenantId, connectionId, orderNumber, root, eventKey, cancellationToken);
}
