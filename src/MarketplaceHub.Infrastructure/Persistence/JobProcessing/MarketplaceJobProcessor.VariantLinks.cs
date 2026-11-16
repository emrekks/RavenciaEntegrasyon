using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 1 (16 Nov 2026): the variant link bookkeeping lives in VariantLinkBookkeeping; the processor's
// methods of the same names call into it (the listing verification and the price/stock answers), the import calls it
// through CatalogImportService.
public sealed partial class MarketplaceJobProcessor
{
    private VariantLinkBookkeeping? variantLinks;

    internal VariantLinkBookkeeping VariantLinks => variantLinks ??= new VariantLinkBookkeeping(db, timeProvider, new ListingVerificationRules(db));

    // ISLEYICI-9 stage 5 step 1: the listing verification lives in ListingVerificationService; the price/stock answers
    // queue their lookups through it, and the dedup tests add a lookup job through the processor.
    internal Task QueueListingLookupsForRefusedCodes(Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        Services.ListingVerification.QueueListingLookupsForRefusedCodes(tenantId, connectionId, variantIds, cancellationToken);

    internal Task<bool> AddListingLookupJobAsync(Guid tenantId, Guid connectionId, string dedup, ListingVerificationService.ListingLookupJobPayload payload, DateTimeOffset now, CancellationToken cancellationToken) =>
        Services.ListingVerification.AddListingLookupJobAsync(tenantId, connectionId, dedup, payload, now, cancellationToken);

    private Task<bool> ExternalIdTaken(Guid tenantId, Guid connectionId, string externalId, Guid? exceptLinkId, CancellationToken cancellationToken) =>
        VariantLinks.ExternalIdTaken(tenantId, connectionId, externalId, exceptLinkId, cancellationToken);

    private static string MarketplacePlatformName(string? platformCode) => VariantLinkBookkeeping.MarketplacePlatformName(platformCode);
}
