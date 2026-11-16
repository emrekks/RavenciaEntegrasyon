using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 1 (16 Nov 2026): the variant link bookkeeping lives in VariantLinkBookkeeping; the processor's
// methods of the same names call into it (the import, the listing verification and the price/stock answers alike).
public sealed partial class MarketplaceJobProcessor
{
    private VariantLinkBookkeeping? variantLinks;

    private VariantLinkBookkeeping VariantLinks => variantLinks ??= new VariantLinkBookkeeping(db, timeProvider, VerifiableVariantLinks);

    private Task<bool> ExternalIdTaken(Guid tenantId, Guid connectionId, string externalId, Guid? exceptLinkId, CancellationToken cancellationToken) =>
        VariantLinks.ExternalIdTaken(tenantId, connectionId, externalId, exceptLinkId, cancellationToken);

    private Task RekeyHepsiburadaVariantLinks(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken) =>
        VariantLinks.RekeyHepsiburadaVariantLinks(tenantId, connectionId, snapshots, cancellationToken);

    private Task MarkVariantLinksSeen(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> page, CancellationToken cancellationToken, bool heal = true) =>
        VariantLinks.MarkVariantLinksSeen(tenantId, connectionId, page, cancellationToken, heal);

    private Task MarkMissingVariantLinks(Guid tenantId, Guid connectionId, string platformCode, DateTimeOffset scanStartedAt, CancellationToken cancellationToken) =>
        VariantLinks.MarkMissingVariantLinks(tenantId, connectionId, platformCode, scanStartedAt, cancellationToken);

    // Called by the import tests through reflection, as before the move.
    private Task RecordVariantLinkCodes(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken, bool onlyAdded = false) =>
        VariantLinks.RecordVariantLinkCodes(tenantId, connectionId, snapshots, cancellationToken, onlyAdded);

    private static string MarketplacePlatformName(string? platformCode) => VariantLinkBookkeeping.MarketplacePlatformName(platformCode);
}
