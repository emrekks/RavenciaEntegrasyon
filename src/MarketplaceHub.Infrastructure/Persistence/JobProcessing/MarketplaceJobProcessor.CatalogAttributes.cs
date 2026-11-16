namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 1 (16 Nov 2026): the catalog attribute machinery lives in CatalogAttributeService, one per run,
// shared by the product import and the catalog structure import (CatalogImportService, step 2). Round 10 step 2: the
// reference reads live in ReferenceSyncService, one per run, which the attribute service reads through; the processor's
// reference job calls into it.
public sealed partial class MarketplaceJobProcessor
{
    private CatalogAttributeService? catalogAttributes;
    private ReferenceSyncService? referenceSync;

    private ReferenceSyncService ReferenceSync =>
        referenceSync is { } current && ReferenceEquals(current.Run, run)
            ? current
            : referenceSync = new ReferenceSyncService(db, references, timeProvider, OrderIngestion, run);

    private CatalogAttributeService CatalogAttributes =>
        catalogAttributes is { } current && ReferenceEquals(current.Run, run)
            ? current
            : catalogAttributes = new CatalogAttributeService(db, timeProvider, ReferenceSync, run);

    private Task<bool> SyncReferenceJob(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken) =>
        ReferenceSync.SyncReferenceJob(tenantId, connectionId, payloadJson, correlationId, jobId, cancellationToken);
}
