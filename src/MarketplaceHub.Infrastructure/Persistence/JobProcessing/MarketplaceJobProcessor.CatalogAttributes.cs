namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 1 (16 Nov 2026): the catalog attribute machinery lives in CatalogAttributeService, one per run,
// shared by the product import and the catalog structure import (CatalogImportService, step 2). The service reads
// references through this processor's SyncReferences, handed in as a delegate.
public sealed partial class MarketplaceJobProcessor
{
    private CatalogAttributeService? catalogAttributes;

    private CatalogAttributeService CatalogAttributes =>
        catalogAttributes is { } current && ReferenceEquals(current.Run, run)
            ? current
            : catalogAttributes = new CatalogAttributeService(db, timeProvider, SyncReferences, run);
}
