using System.Linq.Expressions;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-14 phase D: the one reader of a colour card's shared values (name, description, brand, category, VAT, warranty,
// delivery days). Every screen, export, report, readiness check and marketplace composer reads a card through it, so the
// place the values come from changes here and nowhere else.
// Rule (phase D5b, owner 17 Nov 2026): the name, description, VAT, delivery days, warranty, brand, category and desi are
// the model's; no colour or variant holds its own. KATALOG-14 D5 step B2a (27 Nov 2026): the brand, category, VAT (rate
// and inclusion), warranty, delivery days, description and desi are read from catalog.product_models; no reader reads the
// card or variant copies of them (ModelMirror still writes them while Catalog:MirrorModelFields is on). The name stays the
// colour card's own (each colour keeps its name, as the marketplaces carry it); the model's name is never put over it. A
// card linked to no model reads its own columns (none on live: FamilyId is NOT NULL since step B1).
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
        // The description is the model's (step B2a). The name is the colour card's own (owner, 27 Nov 2026: each colour
        // keeps its name). The overrides are no longer read.
        Title = x.Card.Title,
        Description = x.Model == null ? x.Card.Description : x.Model.Description,
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

    private sealed class VariantWithModel
    {
        public required ProductVariant Variant { get; init; }
        public ProductModel? Model { get; init; }
    }

    // A variant as every reader should see it (step B2a): the desi is its model's (owner, 17 Nov 2026: one desi per model,
    // no per-size value); a model that holds none yet (no variant had one when it opened) leaves the variant's own, as a
    // card linked to no model does. Every other column as stored. Untracked.
    private static readonly Expression<Func<VariantWithModel, ProductVariant>> ResolveVariant = x => new ProductVariant
    {
        Id = x.Variant.Id,
        TenantId = x.Variant.TenantId,
        ProductId = x.Variant.ProductId,
        SortOrder = x.Variant.SortOrder,
        Sku = x.Variant.Sku,
        SkuNormalized = x.Variant.SkuNormalized,
        Barcode = x.Variant.Barcode,
        BarcodeNormalized = x.Variant.BarcodeNormalized,
        ModelCode = x.Variant.ModelCode,
        OptionSignature = x.Variant.OptionSignature,
        Status = x.Variant.Status,
        Weight = x.Variant.Weight,
        Width = x.Variant.Width,
        Height = x.Variant.Height,
        Length = x.Variant.Length,
        Desi = x.Model == null || x.Model.Desi == null ? x.Variant.Desi : x.Model.Desi,
        CostPrice = x.Variant.CostPrice,
        DefaultListPrice = x.Variant.DefaultListPrice,
        DefaultSalePrice = x.Variant.DefaultSalePrice,
        CreatedAt = x.Variant.CreatedAt,
        UpdatedAt = x.Variant.UpdatedAt,
        Version = x.Variant.Version,
    };

    private static readonly Func<VariantWithModel, ProductVariant> ResolveVariantCompiled = ResolveVariant.Compile();

    // The variants with their effective values, for queries (never tracked: a writer loads db.ProductVariants itself).
    public static IQueryable<ProductVariant> Variants(AppDbContext db) =>
        (from variant in db.ProductVariants
         join card in db.Products on new { variant.TenantId, Id = variant.ProductId } equals new { card.TenantId, card.Id }
         join model in db.ProductModels on new { card.TenantId, Id = card.FamilyId } equals new { model.TenantId, Id = (Guid?)model.Id } into models
         from model in models.DefaultIfEmpty()
         select new VariantWithModel { Variant = variant, Model = model }).Select(ResolveVariant);

    // The same for a variant already loaded with its card's model (null: a card linked to no model). A detached copy.
    public static ProductVariant Of(ProductVariant variant, ProductModel? model) => ResolveVariantCompiled(new VariantWithModel { Variant = variant, Model = model });

    // Variants a reader already loaded, as every reader should see them: detached copies with their model's desi (Variants'
    // rule), in the given order. The given rows are never changed (they may be tracked).
    public static async Task<List<ProductVariant>> WithModelDesiAsync(AppDbContext db, Guid tenantId, IEnumerable<ProductVariant> variants, CancellationToken cancellationToken)
    {
        var rows = variants.ToList();
        var desiOfCard = new Dictionary<Guid, decimal?>();
        foreach (var chunk in rows.Select(x => x.ProductId).Distinct().Chunk(1000))
            foreach (var row in await (from card in db.Products.AsNoTracking()
                                       join model in db.ProductModels.AsNoTracking() on new { card.TenantId, Id = card.FamilyId } equals new { model.TenantId, Id = (Guid?)model.Id }
                                       where card.TenantId == tenantId && chunk.Contains(card.Id)
                                       select new { card.Id, model.Desi }).ToListAsync(cancellationToken))
                desiOfCard[row.Id] = row.Desi;
        return [.. rows.Select(variant => ResolveVariantCompiled(new VariantWithModel
        {
            Variant = variant,
            Model = desiOfCard.GetValueOrDefault(variant.ProductId) is { } desi ? new ProductModel { ModelCode = "", ModelCodeKey = "", Title = "", Description = "", Desi = desi } : null,
        }))];
    }

    // The effective desi of the given variants (their model's, else their own), for a writer that holds tracked rows.
    public static async Task<Dictionary<Guid, decimal?>> DesiOfAsync(AppDbContext db, Guid tenantId, IEnumerable<Guid> variantIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, decimal?>();
        foreach (var chunk in variantIds.Distinct().Chunk(1000))
            foreach (var row in await Variants(db).Where(x => x.TenantId == tenantId && chunk.Contains(x.Id)).Select(x => new { x.Id, x.Desi }).ToListAsync(cancellationToken))
                result[row.Id] = row.Desi;
        return result;
    }

    // KATALOG-14 phase D5b: the model code a card's marketplace payloads carry (Trendyol productMainId, Hepsiburada
    // VaryantGroupID, n11 productMainId, Pazarama groupCode, PTT AVM's parent barcode, Shopify's family): its model's, the
    // one place the code lives. The variants' codes (the copies every writer keeps equal to it) are the answer when the
    // card has no coded model, or when a copy holds another key (a mix the composers refuse as before).
    public static async Task<List<string?>> CardModelCodesAsync(AppDbContext db, Guid tenantId, Guid productId, IEnumerable<string?> variantCodes, CancellationToken cancellationToken)
    {
        var copies = variantCodes.Select(code => code?.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var model = await (from product in db.Products.AsNoTracking()
                           join record in db.ProductModels.AsNoTracking() on product.FamilyId equals record.Id
                           where product.TenantId == tenantId && product.Id == productId
                           select new { record.ModelCode, record.ModelCodeKey }).FirstOrDefaultAsync(cancellationToken);
        if (model is null || string.IsNullOrWhiteSpace(model.ModelCode) || copies.Count == 0
            || copies.Any(code => Application.ProductFamilyKeyPolicy.ModelCodeKey(code) != model.ModelCodeKey))
            return copies;
        return [model.ModelCode.Trim()];
    }
}
