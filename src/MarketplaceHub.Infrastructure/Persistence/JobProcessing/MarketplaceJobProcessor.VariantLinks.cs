using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 1 (16 Nov 2026): the variant link bookkeeping lives in VariantLinkBookkeeping; the processor's
// methods of the same names call into it (the listing verification and the price/stock answers), the import calls it
// through CatalogImportService.
public sealed partial class MarketplaceJobProcessor
{
    private VariantLinkBookkeeping? variantLinks;

    internal VariantLinkBookkeeping VariantLinks => variantLinks ??= new VariantLinkBookkeeping(db, timeProvider, ListingRules);

    private Task<bool> ExternalIdTaken(Guid tenantId, Guid connectionId, string externalId, Guid? exceptLinkId, CancellationToken cancellationToken) =>
        VariantLinks.ExternalIdTaken(tenantId, connectionId, externalId, exceptLinkId, cancellationToken);

    private Task MarkVariantLinksSeen(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> page, CancellationToken cancellationToken, bool heal = true) =>
        VariantLinks.MarkVariantLinksSeen(tenantId, connectionId, page, cancellationToken, heal);

    private static string MarketplacePlatformName(string? platformCode) => VariantLinkBookkeeping.MarketplacePlatformName(platformCode);
}
