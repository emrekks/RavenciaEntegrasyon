using System.Linq.Expressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The approval state a product import reads from a marketplace's not-yet-approved list (Trendyol /products/unapproved)
// is written to the imported listing profile, so the products list and the editor say "Onay Bekliyor" or "Reddedildi"
// with the reasons the marketplace gave (owner, 14 Nov 2026). Only what the list says is written: a row whose state is
// unknown leaves the listing as it is, and a rejected row without reasons gets no reason. When a later read finds the
// variant on the approved list, the waiting or refused state is cleared back to UNKNOWN (the approved list says nothing
// more about the listing; ImportedListingProfiles). A profile the panel made is never touched: its own approval check
// owns its state. Only the database is written.
internal static class ImportedApprovalStates
{
    public const string Pending = "APPROVAL_PENDING";
    public const string PartialPending = "APPROVAL_PARTIAL_PENDING";
    public const string Rejected = "REJECTED";
    public const string PartialRejected = "PARTIAL_REJECTED";

    // A listing the marketplace still reviews or refused: not on sale there, whatever link an import made for it.
    // KATALOG-10: written once as an expression; the products filter and the Trendyol update run it in the database, the
    // views compile it.
    public static readonly Expression<Func<string?, bool>> IsHeldListingRule = actualStatus => actualStatus == Pending || actualStatus == Rejected;
    private static readonly Func<string?, bool> IsHeldListingCompiled = IsHeldListingRule.Compile();
    public static bool IsHeldListing(string? actualStatus) => IsHeldListingCompiled(actualStatus);

    private static readonly HashSet<string> OwnStatuses = new(StringComparer.Ordinal) { Pending, PartialPending, Rejected, PartialRejected };

    // The listing state of one not-yet-approved row: its marketplace word, or null (unknown, nothing is written).
    public static string? ListingStatus(string? approvalStatus) => approvalStatus switch
    {
        "PENDING_APPROVAL" => Pending,
        "REJECTED" => Rejected,
        _ => null
    };

    // The profile's state over its listings: all refused REJECTED, all waiting APPROVAL_PENDING, some waiting
    // APPROVAL_PARTIAL_PENDING, some refused PARTIAL_REJECTED; none of these leaves a state this class wrote back to
    // UNKNOWN and any other state as it is.
    public static string ProfileStatus(IReadOnlyCollection<string> listingStatuses, string current)
    {
        if (listingStatuses.Count > 0 && listingStatuses.All(status => status == Rejected)) return Rejected;
        if (listingStatuses.Count > 0 && listingStatuses.All(status => status == Pending)) return Pending;
        if (listingStatuses.Contains(Pending)) return PartialPending;
        if (listingStatuses.Contains(Rejected)) return PartialRejected;
        return OwnStatuses.Contains(current) ? ImportedListingProfiles.UnknownState : current;
    }

    public static async Task ApplyAsync(AppDbContext db, Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken)
    {
        // code (variant id or barcode) -> (listing status or null for approved, reason)
        var read = new Dictionary<string, (string? Status, string? Reason)>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            var status = snapshot.IsPendingApproval ? ListingStatus(snapshot.ApprovalStatus) : null;
            if (snapshot.IsPendingApproval && status is null) continue;
            var reason = status == Rejected && !string.IsNullOrWhiteSpace(snapshot.RejectionReason) ? snapshot.RejectionReason.Trim() : null;
            foreach (var variant in snapshot.Variants)
                foreach (var code in new[] { variant.ExternalVariantId, variant.Barcode })
                    // Trendyol can list a code under review on both lists; the approved list wins.
                    if (!string.IsNullOrWhiteSpace(code) && !(read.TryGetValue(code.Trim(), out var seen) && seen.Status is null))
                        read[code.Trim()] = (status, reason);
        }
        if (read.Count == 0) return;
        var codes = read.Keys.ToArray();
        var links = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (codes.Contains(x.ExternalId) || (x.ExternalBarcode != null && codes.Contains(x.ExternalBarcode))))
            .Select(x => new { x.VariantId, x.ExternalId, x.ExternalBarcode })
            .ToListAsync(cancellationToken);
        var byVariant = new Dictionary<Guid, (string? Status, string? Reason)>();
        foreach (var link in links)
        {
            if (read.TryGetValue(link.ExternalId, out var value) || (link.ExternalBarcode is not null && read.TryGetValue(link.ExternalBarcode, out value)))
                byVariant[link.VariantId] = value;
        }
        if (byVariant.Count == 0) return;
        var variantIds = byVariant.Keys.ToArray();
        var variantProducts = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && variantIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ProductId }).ToListAsync(cancellationToken);
        var productIds = variantProducts.Select(x => x.ProductId).Distinct().ToList();
        // A waiting or refused product needs its imported profile to carry the state (a new-only run makes none itself).
        var toEnsure = variantProducts.Where(x => byVariant[x.Id].Status is not null).Select(x => x.ProductId).Distinct().ToList();
        if (toEnsure.Count > 0) await ImportedListingProfiles.EnsureAsync(db, tenantId, connectionId, toEnsure, cancellationToken);

        var profiles = await db.ChannelListingProfiles
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && productIds.Contains(x.ProductId) && x.Source == ChannelListingProfileSource.MarketplaceImport)
            .ToListAsync(cancellationToken);
        if (profiles.Count == 0) return;
        var profileIds = profiles.Select(x => x.Id).ToArray();
        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && profileIds.Contains(x.ProfileId)).ToListAsync(cancellationToken);
        var listedVariantIds = listings.Select(x => x.VariantId).Distinct().ToArray();
        var states = await db.MarketplaceListingStates
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && listedVariantIds.Contains(x.VariantId))
            .ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var changed = false;
        foreach (var listing in listings)
        {
            if (!byVariant.TryGetValue(listing.VariantId, out var value)) continue;
            string? status;
            string? code;
            if (value.Status is not null) { status = value.Status; code = value.Reason; }
            else if (OwnStatuses.Contains(listing.ActualStatus)) { status = ImportedListingProfiles.UnknownState; code = null; }
            else continue;
            if (listing.ActualStatus != status || listing.RejectionCode != code) { listing.ActualStatus = status; listing.RejectionCode = code; changed = true; }
            if (states.TryGetValue(listing.VariantId, out var state) && (state.ActualStatus != status || state.LastRejectionCode != code))
            {
                state.ActualStatus = status;
                state.LastRejectionCode = code;
                state.Version++;
                changed = true;
            }
        }
        foreach (var profile in profiles)
        {
            var own = listings.Where(x => x.ProfileId == profile.Id).ToList();
            var next = ProfileStatus(own.Select(x => x.ActualStatus).ToList(), profile.ActualStatus);
            var reason = next is Rejected or PartialRejected ? own.Where(x => x.ActualStatus == Rejected).Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) : null;
            var nextReason = OwnStatuses.Contains(next) || OwnStatuses.Contains(profile.ActualStatus) ? reason : profile.LastRejectionCode;
            if (profile.ActualStatus == next && profile.LastRejectionCode == nextReason) continue;
            profile.ActualStatus = next;
            profile.LastRejectionCode = nextReason;
            profile.Version++;
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(cancellationToken);
    }
}
