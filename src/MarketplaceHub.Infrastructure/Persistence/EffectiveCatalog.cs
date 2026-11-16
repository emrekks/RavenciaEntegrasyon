using System.Linq.Expressions;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-14 phase D: the one reader of a colour card's shared values (name, description, brand, category, VAT, warranty,
// delivery days). Every screen, export, report, readiness check and marketplace composer reads a card through it, so the
// place the values come from changes here and nowhere else.
// Rule: a colour's own value where it has one (TitleOverride / DescriptionOverride), else its model's.
// Phase D5 (contract, prepared on scan12/model-contract; ships only after the owner's re-import): the model's value is
// read from catalog.product_models for a linked card: description, brand, category, VAT, warranty and delivery days. A
// card linked to no model reads its own columns. The name is the card's own text (TitleOverride, else the card's Title,
// which the model refresh keeps as the model's name with the colour's word: TitleFor is C# and the colour comes from the
// variants, so it is not resolved in SQL).
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
        Title = x.Card.TitleOverride ?? x.Card.Title,
        Description = x.Card.DescriptionOverride ?? (x.Model == null ? x.Card.Description : x.Model.Description),
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
