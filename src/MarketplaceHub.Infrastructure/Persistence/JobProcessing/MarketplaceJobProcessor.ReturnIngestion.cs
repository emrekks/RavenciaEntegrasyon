using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3c (16 Nov 2026): the claim ingestion lives in ReturnIngestionService, one per run like the order
// ingestion it stores claim orders through; the processor's methods of the same names call into it.
public sealed partial class MarketplaceJobProcessor
{
    private ReturnIngestionService ReturnIngestion => Services.ReturnIngestion;

    internal Task UpsertReturn(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken) =>
        ReturnIngestion.UpsertReturn(tenantId, connectionId, remote, cancellationToken);
}
