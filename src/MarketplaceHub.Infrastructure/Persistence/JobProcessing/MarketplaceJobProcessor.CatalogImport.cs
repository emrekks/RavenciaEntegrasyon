using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 2 (16 Nov 2026): the catalog import (product sync, catalog structure import, product and
// variant link writes) lives in CatalogImportService, one per run; the processor's methods of the same names call into it.
public sealed partial class MarketplaceJobProcessor
{
    // Part of the structure run's stored report (StructureCategoryReport), so it stays a public type of the processor.
    public sealed record StructureCategoryDifference(string ComparedWith, string ComparedSource, int SharedCount, List<string> OnlyInMarketplace, List<string> OnlyInPanel);

    private CatalogImportService? catalogImport;

    internal CatalogImportService CatalogImport =>
        catalogImport is { } current && ReferenceEquals(current.Run, run)
            ? current
            : catalogImport = new CatalogImportService(db, issues, cursors, products, timeProvider, logger, CatalogAttributes, VariantLinks, OrderIngestion, run);

    private Task<bool> SyncProducts(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken) =>
        CatalogImport.SyncProducts(tenantId, connectionId, payloadJson, correlationId, jobId, cancellationToken);

    private Task<bool> ImportCatalogStructure(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken) =>
        CatalogImport.ImportCatalogStructure(tenantId, connectionId, payloadJson, correlationId, jobId, cancellationToken);

    // The listing verification completes the listing profiles of what it read the same way the import does.
    private Task CompleteImportedListingProfiles(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken) =>
        CatalogImport.CompleteImportedListingProfiles(tenantId, connectionId, snapshots, cancellationToken);

    // The VAT rate a newly imported offer takes (called by the import VAT tests on the processor).
    internal Task<decimal> NewImportedOfferVatRateAsync(Guid tenantId, Guid connectionId, Guid variantId, decimal? remoteVatRate, CancellationToken cancellationToken) =>
        CatalogImport.NewImportedOfferVatRateAsync(tenantId, connectionId, variantId, remoteVatRate, cancellationToken);

    internal static string Short(string? value, int maximum) => CatalogAttributeService.Short(value, maximum);

    private static IReadOnlySet<string> PttAvmOptionValues(IEnumerable<RemoteCatalogVariant> remoteVariants) => CatalogImportService.PttAvmOptionValues(remoteVariants);

    private static IReadOnlyList<string> PttAvmMissingOptionValues(string? localSignature, IReadOnlySet<string> optionValues) => CatalogImportService.PttAvmMissingOptionValues(localSignature, optionValues);
}
