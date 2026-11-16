using System.Linq.Expressions;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-14 phase D: the one reader of a colour card's shared values (name, description, brand, category, VAT, warranty,
// delivery days). Every screen, export, report, readiness check and marketplace composer reads a card through it, so the
// place the values come from changes here and nowhere else.
// Target rule (phase D5): a colour's own value where it has one (TitleOverride / DescriptionOverride), else its model's.
// Phases D1-D4 (dual write): every value is the card's own column, the copy every writer keeps and the one source until
// the contract release; the overrides and the model record are kept beside it but read by no screen or marketplace.
// Usable in SQL (Products: an IQueryable that EF translates, composable with Where/Join/OrderBy) and in memory (Of).
public static class EffectiveCatalog
{
    // The card as every reader should see it: the shared values resolved, every other column as stored. Untracked.
    public static readonly Expression<Func<Product, Product>> Resolve = p => new Product
    {
        Id = p.Id,
        TenantId = p.TenantId,
        // Phases D1-D4 (dual write): the card's own text is the one source; the overrides are a record kept by the model
        // refresh and are read only from phase D5 on. Reading them now could send a text an older release changed on the
        // card after the deploy's refresh wrote the override (review, 17 Nov 2026).
        Title = p.Title,
        Description = p.Description,
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
    public static readonly Expression<Func<Product, string>> Title = p => p.Title;
    public static readonly Expression<Func<Product, string>> Description = p => p.Description;
}
