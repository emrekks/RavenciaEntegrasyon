using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarketplaceHub.Infrastructure.Persistence;

// Ürün Hareketleri › Ürün Bilgisi: the information fields (ProductChangeFields) a panel save changed on the product
// cards and variants it holds, written as change rows into the same save. Called right before SaveChanges by each panel
// write path (the editor's save with its model family, Toplu Değer Ata, Toplu KDV Ata, the product file import), it
// compares each tracked card's and variant's loaded value with the one about to be written, so whatever the path set,
// only a real change (old ≠ new, as the page shows it) is recorded. New and deleted rows record nothing, and nothing
// a marketplace import writes comes through here.
public static class ProductFieldChangeRecorder
{
    private sealed record Pending(Guid ProductId, Guid? VariantId, string Field, object? Previous, object? Next);

    public static async Task<int> RecordAsync(AppDbContext db, Guid tenantId, string source, Guid? actorUserId, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = new List<Pending>();
        foreach (var entry in db.ChangeTracker.Entries<Product>().Where(x => x.State == EntityState.Modified && x.Entity.TenantId == tenantId))
        {
            var product = entry.Entity;
            void Card<T>(string field, PropertyEntry<Product, T> property) => Add(pending, product.Id, null, field, property.OriginalValue, property.CurrentValue);
            Card(ProductChangeFields.Title, entry.Property(x => x.Title));
            Card(ProductChangeFields.Description, entry.Property(x => x.Description));
            Card(ProductChangeFields.Brand, entry.Property(x => x.BrandId));
            Card(ProductChangeFields.Category, entry.Property(x => x.CategoryId));
            Card(ProductChangeFields.VatRate, entry.Property(x => x.VatRate));
            Card(ProductChangeFields.VatInclusion, entry.Property(x => x.VatInclusion));
            Card(ProductChangeFields.Warranty, entry.Property(x => x.WarrantyMonths));
            Card(ProductChangeFields.DeliveryDays, entry.Property(x => x.DeliveryDays));
        }
        foreach (var entry in db.ChangeTracker.Entries<ProductVariant>().Where(x => x.State == EntityState.Modified && x.Entity.TenantId == tenantId))
        {
            var variant = entry.Entity;
            void Row<T>(string field, PropertyEntry<ProductVariant, T> property) => Add(pending, variant.ProductId, variant.Id, field, property.OriginalValue, property.CurrentValue);
            Row(ProductChangeFields.Cost, entry.Property(x => x.CostPrice));
            Row(ProductChangeFields.Desi, entry.Property(x => x.Desi));
            Row(ProductChangeFields.Sku, entry.Property(x => x.Sku));
            Row(ProductChangeFields.Barcode, entry.Property(x => x.Barcode));
            Row(ProductChangeFields.ModelCode, entry.Property(x => x.ModelCode));
        }
        if (pending.Count == 0) return 0;

        var brandIds = Ids(pending, ProductChangeFields.Brand);
        var brands = brandIds.Length == 0 ? [] : await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && brandIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var categoryIds = Ids(pending, ProductChangeFields.Category);
        var categories = categoryIds.Length == 0 ? [] : await db.Categories.AsNoTracking().Where(x => x.TenantId == tenantId && categoryIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Path, cancellationToken);
        string? Name(IReadOnlyDictionary<Guid, string> names, object? id) => id is Guid key ? names.GetValueOrDefault(key) ?? "Silinmiş kayıt" : null;

        var written = 0;
        foreach (var change in pending)
        {
            var (previous, next) = change.Field switch
            {
                ProductChangeFields.Brand => (Name(brands, change.Previous), Name(brands, change.Next)),
                ProductChangeFields.Category => (Name(categories, change.Previous), Name(categories, change.Next)),
                _ => (Display(change.Field, change.Previous), Display(change.Field, change.Next))
            };
            // A description whose shown start stays the same changed further on: the row says so instead of vanishing.
            var note = change.Field == ProductChangeFields.Description && previous == next ? "Açıklamanın devamı değişti." : null;
            if (ProductChangeLog.Field(tenantId, change.ProductId, change.VariantId, change.Field, previous, next, source, actorUserId, correlationId, now, note) is { } row)
            {
                db.ProductChangeEntries.Add(row);
                written++;
            }
        }
        return written;
    }

    // A real change only: the same value (a description differing in surrounding spaces, 1.0 against 1.00) is none.
    private static void Add(List<Pending> pending, Guid productId, Guid? variantId, string field, object? previous, object? next)
    {
        if (Same(previous, next)) return;
        pending.Add(new(productId, variantId, field, previous, next));
    }

    private static bool Same(object? previous, object? next) => (previous, next) switch
    {
        (null, null) => true,
        (string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal),
        (string a, null) => string.IsNullOrWhiteSpace(a),
        (null, string b) => string.IsNullOrWhiteSpace(b),
        _ => Equals(previous, next)
    };

    private static Guid[] Ids(IEnumerable<Pending> pending, string field) =>
        pending.Where(x => x.Field == field).SelectMany(x => new[] { x.Previous, x.Next }).OfType<Guid>().Distinct().ToArray();

    // The value as the page shows it (ProductChangeFields' formats).
    public static string? Display(string field, object? value) => field switch
    {
        ProductChangeFields.Cost => ProductChangeFields.Money(value as decimal?),
        ProductChangeFields.Desi => ProductChangeFields.Number(value as decimal?),
        ProductChangeFields.VatRate => ProductChangeFields.Percent(value as decimal?),
        ProductChangeFields.VatInclusion => ProductChangeFields.Inclusion(value as string),
        ProductChangeFields.Warranty => ProductChangeFields.Months(value as int?),
        ProductChangeFields.DeliveryDays => ProductChangeFields.Days(value as int?),
        ProductChangeFields.Description => ProductChangeFields.DescriptionExcerpt(value as string),
        _ => ProductChangeFields.Text(value?.ToString())
    };
}
