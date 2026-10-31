using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// "Platformlarda Güncelle" on Pazarama: the VAT rate of each listed barcode (product/vatRate/bulk), the one product field
// Pazarama's API changes after the create. It rides the product update job as the variant request of an approved
// product; the content and delivery requests stay empty and are skipped without a call.
internal sealed class PazaramaProductUpdateComposer(AppDbContext db)
{
    public async Task<ServiceResult<ProductUpdateDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null) return Fail("LISTING_PROFILE_REQUIRED", "Pazarama güncellemesi için bu bağlantıda yayın kaydı gerekir.");
        // The job reads every listing of the card, so each one must name the barcode Pazarama holds.
        var listings = await (from listing in db.ChannelListingVariants.AsNoTracking()
                              join variant in db.ProductVariants.AsNoTracking() on new { listing.TenantId, listing.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                              where listing.TenantId == tenantId && listing.ProfileId == profile.Id
                              orderby variant.Id
                              select new { variant.Id, variant.Sku, listing.ExternalBarcode }).ToListAsync(cancellationToken);
        if (listings.Count == 0 || listings.Any(x => string.IsNullOrWhiteSpace(x.ExternalBarcode)))
            return Fail("REMOTE_BARCODE_REQUIRED", "KDV oranı güncellemesi için her varyantın Pazarama barkodu gerekir.");
        if (listings.Count > PazaramaVatRateRules.MaxItemsPerRequest)
            return Fail("PRODUCT_BATCH_LIMIT_EXCEEDED", $"Tek KDV oranı isteğinde en fazla {PazaramaVatRateRules.MaxItemsPerRequest} barkod gönderilebilir.");

        var variantIds = listings.Select(x => x.Id).ToArray();
        var offers = await db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId) && x.Status == "ACTIVE").ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var rates = new List<(string Barcode, int VatRate)>(listings.Count);
        foreach (var listing in listings)
        {
            // The rate is the channel offer's, as the create sent it; a card without one has nothing to send.
            if (!offers.TryGetValue(listing.Id, out var offer)) return Fail("CHANNEL_OFFER_REQUIRED", $"'{listing.Sku}' için etkin Pazarama fiyat teklifi gerekir.");
            if (offer.VatRate != decimal.Truncate(offer.VatRate) || !PazaramaVatRateRules.ValidRates.Contains((int)offer.VatRate))
                return Fail("VAT_RATE_INVALID", $"'{listing.Sku}' için KDV oranı {offer.VatRate.ToString("0.##", CultureInfo.InvariantCulture)}; Pazarama için 0, 1, 10 veya 20 olmalıdır.");
            rates.Add((listing.ExternalBarcode!.Trim(), (int)offer.VatRate));
        }
        if (rates.Select(x => x.Barcode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rates.Count)
            return Fail("BARCODE_DUPLICATE", "Pazarama barkodları yineleniyor; KDV oranı güncellemesi durduruldu.");

        var variantPayload = PazaramaVatRateRules.Payload(rates);
        const string noItems = "{\"items\":[]}";
        var publication = new ProductUpdatePublication(productId, "APPROVED", Hash(variantPayload), "{}", noItems, variantPayload, noItems);
        var drafts = listings.Select(x => new PublicationVariantDraft(x.Id, x.Sku, x.ExternalBarcode!.Trim())).ToList();
        return ServiceResult<ProductUpdateDraft>.Ok(new(profile.Id, publication, drafts));
    }

    private static ServiceResult<ProductUpdateDraft> Fail(string code, string message) => ServiceResult<ProductUpdateDraft>.Fail(code, message, 422);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
