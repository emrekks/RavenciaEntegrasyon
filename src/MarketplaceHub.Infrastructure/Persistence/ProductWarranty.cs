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

    public sealed record PassResult(int CardsWritten, int ModelsWritten, int EmptyCardsNotDraft);

    // The migration runs while the previous API and Worker still serve (fast-deploy.sh migrates, then swaps the
    // containers), and the previous code writes null for a 0 it saves. So the new Worker's first pass after it starts
    // gives a card on sale or closed with an empty warranty its 0 (that is what the previous code meant), and a model
    // record 0 when none of its cards is empty. A draft keeps its empty warranty: it may be one saved empty on purpose.
    // The current code is meant never to leave a card that is not a draft empty (the editor, the bulk opening and the
    // family write-through refuse or skip it), but that is a rule, not a guarantee; so every later pass writes nothing and
    // only counts such cards, for the Worker to warn about instead of silently turning them into 0.
    // Idempotent, set-based (two statements; about a thousand cards on live); no Version, UpdatedAt or change log.
    public static async Task<PassResult> RunPassAsync(AppDbContext db, bool firstPassAfterStart, CancellationToken cancellationToken)
    {
        var emptyNotDraft = db.Products.Where(x => x.WarrantyMonths == null && x.Status != ProductStatus.Draft);
        if (!firstPassAfterStart) return new(0, 0, await emptyNotDraft.CountAsync(cancellationToken));
        var cards = await emptyNotDraft.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.WarrantyMonths, 0), cancellationToken);
        var models = await db.ProductModels
            .Where(model => model.WarrantyMonths == null
                && !db.Products.Any(card => card.TenantId == model.TenantId && card.FamilyId == model.Id && card.WarrantyMonths == null))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.WarrantyMonths, 0), cancellationToken);
        return new(cards, models, 0);
    }
}
