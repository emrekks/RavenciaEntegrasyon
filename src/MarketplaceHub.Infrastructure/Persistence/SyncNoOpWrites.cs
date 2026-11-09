using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarketplaceHub.Infrastructure.Persistence;

// A marketplace read that brings a record again unchanged (the overlap window, a status scan, the invoice check) must not
// rewrite it (live 10 Nov 2026: ~220 orders rewritten an hour, 6 of them changed on the marketplace; 25k order and 5.6k
// question data-change events a day). Two kinds of columns are bookkeeping, not a change:
// - write stamps (UpdatedAt, Version): set by the merge on every fresh read; alone they are dropped, nothing is written;
// - check stamps (a package's MarketplaceInvoiceObservedAt, a question's LastSyncedAt): times a rule reads ("last
//   checked": the invoice check skips a package seen lately, the answer reconcile waits five minutes). They are still
//   written, without moving the version, and alone they send no data-change event (AppDbContext outbox).
// A real change anywhere in the order (a column, a new line, package, allocation or status row) keeps every bump as before.
internal static class SyncNoOpWrites
{
    private static readonly HashSet<string> WriteStamps = new(StringComparer.Ordinal) { "UpdatedAt", "Version" };
    private static readonly HashSet<string> PackageCheckStamps = new(StringComparer.Ordinal) { nameof(ShipmentPackage.MarketplaceInvoiceObservedAt) };
    private static readonly HashSet<string> QuestionCheckStamps = new(StringComparer.Ordinal) { nameof(MarketplaceQuestion.LastSyncedAt) };
    private static readonly HashSet<string> NoCheckStamps = new(StringComparer.Ordinal);

    internal static IReadOnlySet<string> CheckStamps(Type type) =>
        type == typeof(ShipmentPackage) ? PackageCheckStamps : type == typeof(MarketplaceQuestion) ? QuestionCheckStamps : NoCheckStamps;

    internal static bool IsBookkeeping(Type type, string property) => WriteStamps.Contains(property) || CheckStamps(type).Contains(property);

    // The properties of a modified entry that really changed. A property set to an equal value (a JSON snapshot with
    // another key order or spacing included) is put back to the stored value and no longer counts as modified.
    internal static IReadOnlyList<string> RealChanges(EntityEntry entry)
    {
        if (entry.State != EntityState.Modified) return [];
        var changed = new List<string>();
        foreach (var property in entry.Properties)
        {
            if (!property.IsModified) continue;
            var original = property.OriginalValue;
            var current = property.CurrentValue;
            var comparer = property.Metadata.GetValueComparer();
            if (comparer.Equals(original, current)
                || (property.Metadata.Name.EndsWith("Json", StringComparison.Ordinal) && original is string before && current is string after && SameJson(before, after)))
            {
                property.CurrentValue = original;
                property.IsModified = false;
                continue;
            }
            changed.Add(property.Metadata.Name);
        }
        if (changed.Count == 0) entry.State = EntityState.Unchanged;
        return changed;
    }

    // Modified only in check stamps (and write stamps): still written, but not a change the screens need to hear of.
    internal static bool IsCheckOnly(EntityEntry entry)
    {
        if (entry.State != EntityState.Modified) return false;
        var checkStamps = CheckStamps(entry.Metadata.ClrType);
        if (checkStamps.Count == 0) return false;
        var modified = entry.Properties.Where(property => property.IsModified).Select(property => property.Metadata.Name).ToList();
        return modified.Any(checkStamps.Contains) && modified.All(name => WriteStamps.Contains(name) || checkStamps.Contains(name));
    }

    // After an order merge: when nothing of the order really changed, its write stamps (and its lines' and packages')
    // are put back, so no row is rewritten for them and no event is sent. Returns whether anything really changed.
    internal static bool DropUnchangedOrder(DbContext db, Order order, IEnumerable<OrderLine> lines, IEnumerable<ShipmentPackage> packages)
    {
        var lineList = lines.Distinct().ToList();
        var packageList = packages.Distinct().ToList();
        var entries = new List<EntityEntry> { db.Entry(order) };
        entries.AddRange(lineList.Select(line => (EntityEntry)db.Entry(line)));
        entries.AddRange(packageList.Select(package => (EntityEntry)db.Entry(package)));
        if (entries.Any(entry => entry.State is EntityState.Added or EntityState.Deleted)) return true;
        var packageIds = packageList.Select(package => package.Id).ToHashSet();
        if (db.ChangeTracker.Entries<PackageLineAllocation>().Any(entry => entry.State is EntityState.Added or EntityState.Deleted or EntityState.Modified
                && packageIds.Contains(entry.Entity.PackageId)
                && (entry.State != EntityState.Modified || RealChanges(entry).Count > 0))
            || db.ChangeTracker.Entries<OrderStatusHistory>().Any(entry => entry.State == EntityState.Added && entry.Entity.OrderId == order.Id))
            return true;
        var real = false;
        foreach (var entry in entries)
            if (RealChanges(entry).Any(name => !IsBookkeeping(entry.Metadata.ClrType, name))) real = true;
        if (real) return true;
        foreach (var entry in entries.Where(entry => entry.State == EntityState.Modified))
        {
            foreach (var stamp in WriteStamps)
            {
                var property = entry.Properties.FirstOrDefault(p => p.Metadata.Name == stamp);
                if (property is null || !property.IsModified) continue;
                property.CurrentValue = property.OriginalValue;
                property.IsModified = false;
            }
            if (!entry.Properties.Any(property => property.IsModified)) entry.State = EntityState.Unchanged;
        }
        return false;
    }

    internal static bool SameJson(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
