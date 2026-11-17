using System.Linq.Expressions;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-14 phase D: the one reader of a colour card's shared values (name, description, brand, category, VAT, warranty,
// delivery days). Every screen, export, report, readiness check and marketplace composer reads a card through it, so the
// place the values come from changes here and nowhere else.
// Rule: a colour's own value where it has one (TitleOverride / DescriptionOverride), else its model's
// (catalog.product_models: description, brand, category, VAT, warranty, delivery days). Phase D5a keeps the card's own
// value wherever it differs from the model (see Resolve). A card linked to no model reads its own columns. The name is
// the colour's override, else the card's Title, which the model refresh keeps as the model's name with the colour's
// word (TitleFor is C# and the colour comes from the variants, so it is not resolved in SQL).
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
        // Phase D5a (review, 17 Nov 2026): while the writers keep the card copies the card stays the source. Each value is
        // the model's where the card holds the same, and the card's own where it differs (a colour whose VAT, category,
        // warranty or delivery days an Excel row, a bulk edit or a race left apart from its model; a description without
        // an override that differs). So phase A changes no output; whether a colour may hold its own VAT is asked before
        // phase B, which reads the model alone.
        Title = x.Card.TitleOverride ?? x.Card.Title,
        Description = x.Card.DescriptionOverride ?? (x.Model == null || x.Card.Description != x.Model.Description ? x.Card.Description : x.Model.Description),
        BrandId = x.Model == null || x.Card.BrandId != x.Model.BrandId ? x.Card.BrandId : x.Model.BrandId,
        CategoryId = x.Model == null || x.Card.CategoryId != x.Model.CategoryId ? x.Card.CategoryId : x.Model.CategoryId,
        DefaultListPrice = x.Card.DefaultListPrice,
        DefaultSalePrice = x.Card.DefaultSalePrice,
        WarrantyMonths = x.Model == null || x.Card.WarrantyMonths != x.Model.WarrantyMonths ? x.Card.WarrantyMonths : x.Model.WarrantyMonths,
        DeliveryDays = x.Model == null || x.Card.DeliveryDays != x.Model.DeliveryDays ? x.Card.DeliveryDays : x.Model.DeliveryDays,
        TrendyolWebColorValueId = x.Card.TrendyolWebColorValueId,
        // The rate and whether prices include it go together: the card's pair where either differs.
        VatRate = x.Model == null || x.Card.VatRate != x.Model.VatRate || x.Card.VatInclusion != x.Model.VatInclusion ? x.Card.VatRate : x.Model.VatRate,
        VatInclusion = x.Model == null || x.Card.VatRate != x.Model.VatRate || x.Card.VatInclusion != x.Model.VatInclusion ? x.Card.VatInclusion : x.Model.VatInclusion,
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
