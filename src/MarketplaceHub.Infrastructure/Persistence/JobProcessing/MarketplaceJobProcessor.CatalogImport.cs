using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 2 (16 Nov 2026): the catalog import (product sync, catalog structure import, product and
// variant link writes) lives in CatalogImportService, one per run; the product sync and the structure import jobs run in
// their run handlers (stage 4), and the processor's remaining callers use the run's service through these members.
public sealed partial class MarketplaceJobProcessor
{
    // Part of the structure run's stored report (StructureCategoryReport), so it stays a public type of the processor.
    public sealed record StructureCategoryDifference(string ComparedWith, string ComparedSource, int SharedCount, List<string> OnlyInMarketplace, List<string> OnlyInPanel);

    internal CatalogImportService CatalogImport => Services.CatalogImport;

    // The VAT rate a newly imported offer takes (called by the import VAT tests on the processor).
    internal Task<decimal> NewImportedOfferVatRateAsync(Guid tenantId, Guid connectionId, Guid variantId, decimal? remoteVatRate, CancellationToken cancellationToken) =>
        CatalogImport.NewImportedOfferVatRateAsync(tenantId, connectionId, variantId, remoteVatRate, cancellationToken);

    internal static string Short(string? value, int maximum) => CatalogAttributeService.Short(value, maximum);
}
