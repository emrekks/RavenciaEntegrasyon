using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Garanti Süresi (owner, 15 Nov 2026: "0 ile boş farklı; boşsa doldurmasını istesin ama default 0 zaten"): 0 is "no
// warranty" and is stored as 0; null is "not filled", which only a draft may keep. Until then a user's 0 was stored as
// null, so migration 20261115170000_WarrantyZeroIsAValue turned every stored null into 0.
public static class ProductWarranty
{
    // An empty card warranty where a marketplace would receive the card's own: refused, never sent as 0.
    public const string RequiredCode = "WARRANTY_REQUIRED";
    public const string RequiredMessage = "Garanti Süresi boş; ürün kartında Garanti Süresi girin (0 = garanti yok).";

    // The migration runs while the previous API and Worker still serve (fast-deploy.sh migrates, then swaps the
    // containers), and the previous code writes null for a 0 it saves. The new Worker runs this shortly after it starts
    // (and daily after that): a card on sale or closed with an empty warranty can only be such a row, as the new code
    // never leaves one empty (the editor and the bulk opening refuse it), so it takes 0. A draft keeps its empty warranty:
    // it may be one saved empty on purpose, and the editor asks for the value before it opens for sale. A model record
    // takes 0 only when none of its cards is empty (the cards' majority is what the write-through gives it).
    // Idempotent, set-based (two statements; about a thousand cards on live), and it writes no Version, UpdatedAt or
    // change log, as the migration does not either.
    public static async Task<(int Cards, int Models)> RepairAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var cards = await db.Products
            .Where(x => x.WarrantyMonths == null && x.Status != ProductStatus.Draft)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.WarrantyMonths, 0), cancellationToken);
        var models = await db.ProductModels
            .Where(model => model.WarrantyMonths == null
                && !db.Products.Any(card => card.TenantId == model.TenantId && card.FamilyId == model.Id && card.WarrantyMonths == null))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.WarrantyMonths, 0), cancellationToken);
        return (cards, models);
    }
}
