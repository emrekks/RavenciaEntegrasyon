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
    internal sealed record Held(List<(EntityEntry Entry, string Property, object? Value)> Restore, List<(PropertyEntry Property, object? Original)> Kept);

    private static readonly string[] CardFields = [nameof(Product.VatRate), nameof(Product.VatInclusion), nameof(Product.CategoryId), nameof(Product.WarrantyMonths), nameof(Product.DeliveryDays)];

    // Null when the copies are written (nothing to hold back). Then a copy column a rebase found stale (it differs from the
    // model, e.g. a row edited while the switch was off) is healed on a row this save writes anyway: the column is written
    // with the model's value (its original and current value already), so ProductModelLinks.Capture, Ürün Hareketleri, the
    // outbox and the marketplace links (all read before this point, and none sees a changed value) see no change and
    // queue nothing. A row the save does not write is left as it is (no extra write, no automatic backfill).
    public static Held? Before(AppDbContext db)
    {
        var tracker = db.ChangeTracker;
        if (ModelFieldCopies.Written)
        {
            foreach (var entry in tracker.Entries().Where(x => x.State == EntityState.Modified && x.Entity is Product or ProductVariant))
                if (db.RebasedRows.TryGetValue(entry.Entity, out var stale) && stale.Count > 0)
                {
                    foreach (var name in stale)
                        if (entry.Property(name) is { IsModified: false } property && Equals(property.OriginalValue, property.CurrentValue)) property.IsModified = true;
                    stale.Clear();
                }
            return null;
        }
        var restore = new List<(EntityEntry, string, object?)>();
        var kept = new List<(PropertyEntry, object?)>();
        foreach (var entry in tracker.Entries<Product>())
        {
            if (entry.State == EntityState.Modified)
            {
                foreach (var name in CardFields.Append(nameof(Product.Description)))
                    if (entry.Property(name) is { IsModified: true } property) Keep(property, kept);
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
            if (entry.State == EntityState.Modified && property.IsModified) Keep(property, kept);
            else if (entry.State == EntityState.Added && property.CurrentValue is not null)
            {
                restore.Add((entry, nameof(ProductVariant.Desi), property.CurrentValue));
                property.CurrentValue = null;
            }
        }
        return new Held(restore, kept);
    }

    // The new value stays in memory and is not written: EF's own change detection before the write compares the current
    // value with the original one, so the original takes the new value too (Ürün Hareketleri and ProductModelLinks.Capture
    // have read the old one already). The old original is kept: a save that fails puts it back (Undo).
    private static void Keep(PropertyEntry property, List<(PropertyEntry, object?)> kept)
    {
        kept.Add((property, property.OriginalValue));
        property.OriginalValue = property.CurrentValue;
        property.IsModified = false;
    }

    // A save that failed (EF's write, the model links, the commit): the changed columns get their old original back and are
    // marked changed again, so a retry in the same context (or ProductModelLinks.Capture) sees the change once more
    // (review 2, 27 Nov 2026: the owner's edit was otherwise lost with the switch off).
    public static void Undo(Held? held)
    {
        if (held is null) return;
        foreach (var (property, original) in held.Kept)
        {
            if (property.EntityEntry.State == EntityState.Detached) continue;
            property.OriginalValue = original;
            property.IsModified = !Equals(original, property.CurrentValue);
        }
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
