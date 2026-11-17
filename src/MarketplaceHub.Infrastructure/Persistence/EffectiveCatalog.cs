using System.Linq.Expressions;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-14 phase D: the one reader of a colour card's shared values (name, description, brand, category, VAT, warranty,
// delivery days). Every screen, export, report, readiness check and marketplace composer reads a card through it, so the
// place the values come from changes here and nowhere else.
// Rule (phase D5b, owner 17 Nov 2026): the name, description, VAT, delivery days, warranty, brand, category and desi are
// the model's; no colour or variant holds its own. The brand, category, VAT, warranty and delivery days are read from
// catalog.product_models; the name, description and desi from the copies every writer mirrors from the model in the same
// transaction (ModelMirror; the name with the colour's word). A card linked to no model reads its own columns.
// Usable in SQL (Products: an IQueryable that EF translates, composable with Where/Join/OrderBy) and in memory (Of).
public static class EffectiveCatalog
{
    private sealed class CardWithModel
    {
        public required Product Card { get; init; }
        public ProductModel? Model { get; init; }
    }

    // The card as every reader should see it: the shared values resolved, every other column as stored. Untracked.
    private static readonly Expression<Func<CardWithModel, Product>> Resolve = x => new Product
    {
        Id = x.Card.Id,
        TenantId = x.Card.TenantId,
        // Phase D5b (owner, 17 Nov 2026): the VAT (rate and whether prices include it), category, warranty and delivery days
        // are always the model's; a colour cannot hold its own. The brand is the model's (part of its key). The card
        // copies are kept as a mirror of the model (ModelMirror) so a rollback to phase A reads the same values.
        // The name and the description are the model's too: every writer gives them to the model and mirrors them to
        // every colour in the same transaction (the name with the colour's word, TitleFor, which SQL cannot resolve), so
        // the card's columns are the model's values. The overrides are no longer read. Cards whose text still differs
        // from their model's (phase A's overrides, 85 live models) keep it until the owner approves the flattening.
        Title = x.Card.Title,
        Description = x.Card.Description,
        BrandId = x.Model == null ? x.Card.BrandId : x.Model.BrandId,
        CategoryId = x.Model == null ? x.Card.CategoryId : x.Model.CategoryId,
        DefaultListPrice = x.Card.DefaultListPrice,
        DefaultSalePrice = x.Card.DefaultSalePrice,
        WarrantyMonths = x.Model == null ? x.Card.WarrantyMonths : x.Model.WarrantyMonths,
        DeliveryDays = x.Model == null ? x.Card.DeliveryDays : x.Model.DeliveryDays,
        TrendyolWebColorValueId = x.Card.TrendyolWebColorValueId,
        VatRate = x.Model == null ? x.Card.VatRate : x.Model.VatRate,
        VatInclusion = x.Model == null ? x.Card.VatInclusion : x.Model.VatInclusion,
        Status = x.Card.Status,
        SourcePolicyVersion = x.Card.SourcePolicyVersion,
        ArchivedAt = x.Card.ArchivedAt,
        FamilyId = x.Card.FamilyId,
        TitleOverride = x.Card.TitleOverride,
        DescriptionOverride = x.Card.DescriptionOverride,
        CreatedAt = x.Card.CreatedAt,
        UpdatedAt = x.Card.UpdatedAt,
        Version = x.Card.Version,
    };

    private static readonly Func<CardWithModel, Product> ResolveCompiled = Resolve.Compile();

    // The cards with their effective values, for queries (never tracked: a writer loads db.Products itself).
    public static IQueryable<Product> Products(AppDbContext db) =>
        (from card in db.Products
         join model in db.ProductModels on new { card.TenantId, Id = card.FamilyId } equals new { model.TenantId, Id = (Guid?)model.Id } into models
         from model in models.DefaultIfEmpty()
         select new CardWithModel { Card = card, Model = model }).Select(Resolve);

    // The same for a card already loaded with its model (null: a card linked to no model). A detached copy.
    public static Product Of(Product card, ProductModel? model) => ResolveCompiled(new CardWithModel { Card = card, Model = model?.Id == card.FamilyId ? model : null });
}
