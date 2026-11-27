using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-14 D5 step B2b: with Catalog:MirrorModelFields off (ModelFieldCopies), a save writes no copy column of the
// model's shared fields. It runs after ProductModelLinks.Capture (which read what the save gives each card: the model takes
// it, ModelMirror) and after GiveNewCardsModels (which opened a new card's own model from its given values), right before
// EF writes the rows:
// - a changed card or variant keeps its new value in memory but the column is left out of the UPDATE;
// - a new card is inserted with no VAT rate, VAT inclusion, category, warranty or delivery days (NULL), a new variant with
//   no desi; its description is written as given, because the column is NOT NULL (the one value a constraint requires).
//   After the save the new rows show their given values again in memory (the caller may read them), as if written.
// The name (each colour card's own) and the brand (the card's link to its model) are written as before.
internal static class ModelFieldCopyHold
{
    internal sealed record Held(List<(EntityEntry Entry, string Property, object? Value)> Restore);

    private static readonly string[] CardFields = [nameof(Product.VatRate), nameof(Product.VatInclusion), nameof(Product.CategoryId), nameof(Product.WarrantyMonths), nameof(Product.DeliveryDays)];

    // Null when the copies are written (nothing to hold back).
    public static Held? Before(ChangeTracker tracker)
    {
        if (ModelFieldCopies.Written) return null;
        var restore = new List<(EntityEntry, string, object?)>();
        foreach (var entry in tracker.Entries<Product>())
        {
            if (entry.State == EntityState.Modified)
            {
                foreach (var name in CardFields.Append(nameof(Product.Description)))
                    if (entry.Property(name) is { IsModified: true } property) Keep(property);
            }
            else if (entry.State == EntityState.Added)
            {
                foreach (var name in CardFields)
                {
                    var property = entry.Property(name);
                    if (property.CurrentValue is null) continue;
                    restore.Add((entry, name, property.CurrentValue));
                    property.CurrentValue = null;
                }
            }
        }
        foreach (var entry in tracker.Entries<ProductVariant>())
        {
            var property = entry.Property(x => x.Desi);
            if (entry.State == EntityState.Modified && property.IsModified) Keep(property);
            else if (entry.State == EntityState.Added && property.CurrentValue is not null)
            {
                restore.Add((entry, nameof(ProductVariant.Desi), property.CurrentValue));
                property.CurrentValue = null;
            }
        }
        return new Held(restore);
    }

    // The new value stays in memory and is not written: EF's own change detection before the write compares the current
    // value with the original one, so the original takes the new value too (Ürün Hareketleri and ProductModelLinks.Capture
    // have read the old one already).
    private static void Keep(PropertyEntry property)
    {
        property.OriginalValue = property.CurrentValue;
        property.IsModified = false;
    }

    // After the save (or its failure): the new rows' given values back in memory, never marked as a change.
    public static void After(Held? held)
    {
        if (held is null) return;
        foreach (var (entry, name, value) in held.Restore)
        {
            var property = entry.Property(name);
            property.CurrentValue = value;
            if (entry.State is EntityState.Added or EntityState.Detached) continue;
            property.OriginalValue = value;
            property.IsModified = false;
        }
    }
}
