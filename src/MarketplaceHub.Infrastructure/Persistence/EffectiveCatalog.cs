using System.Linq.Expressions;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-14 phase D: the one reader of a colour card's shared values (name, description, brand, category, VAT, warranty,
// delivery days). Every screen, export, report, readiness check and marketplace composer reads a card through it, so the
// place the values come from changes here and nowhere else.
// Rule: a colour's own value where it has one (TitleOverride / DescriptionOverride), else its model's.
// Phase D1 (this release): the "model's value" of a card is still the card's own column, the copy every writer keeps
// (the cards are today's source; the model record is rewritten from them). Nothing a screen shows or a marketplace
// receives changes; the overrides are empty. Phase D2 turns the writers to the model and fills the overrides, then
// reads the model here.
// Usable in SQL (Products: an IQueryable that EF translates, composable with Where/Join/OrderBy) and in memory (Of).
public static class EffectiveCatalog
{
    // The card as every reader should see it: the shared values resolved, every other column as stored. Untracked.
    public static readonly Expression<Func<Product, Product>> Resolve = p => new Product
    {
        Id = p.Id,
        TenantId = p.TenantId,
        Title = p.TitleOverride ?? p.Title,
        Description = p.DescriptionOverride ?? p.Description,
        BrandId = p.BrandId,
        CategoryId = p.CategoryId,
        DefaultListPrice = p.DefaultListPrice,
        DefaultSalePrice = p.DefaultSalePrice,
        WarrantyMonths = p.WarrantyMonths,
        DeliveryDays = p.DeliveryDays,
        TrendyolWebColorValueId = p.TrendyolWebColorValueId,
        VatRate = p.VatRate,
        VatInclusion = p.VatInclusion,
        Status = p.Status,
        SourcePolicyVersion = p.SourcePolicyVersion,
        ArchivedAt = p.ArchivedAt,
        FamilyId = p.FamilyId,
        TitleOverride = p.TitleOverride,
        DescriptionOverride = p.DescriptionOverride,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        Version = p.Version,
    };

    private static readonly Func<Product, Product> ResolveCompiled = Resolve.Compile();

    // The cards with their effective values, for queries (never tracked: a writer loads db.Products itself).
    public static IQueryable<Product> Products(AppDbContext db) => db.Products.Select(Resolve);

    // The same for a card already loaded (a detached copy; the loaded entity is not changed).
    public static Product Of(Product card) => ResolveCompiled(card);

    // Single values, for projections that read one field (SQL-translatable).
    public static readonly Expression<Func<Product, string>> Title = p => p.TitleOverride ?? p.Title;
    public static readonly Expression<Func<Product, string>> Description = p => p.DescriptionOverride ?? p.Description;
}
