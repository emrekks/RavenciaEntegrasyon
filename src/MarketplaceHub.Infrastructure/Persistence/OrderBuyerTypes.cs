using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarketplaceHub.Infrastructure.Persistence;

// The order's stored buyer kind (Order.BuyerType), always the invoice's own rule
// (TrendyolEFaturamCanonicalPayload.ResolveBuyerType) applied to the order's customer and invoice address snapshots.
// The order list's "Fatura Türü" filter reads the column and the row label reads the same value, so the two never
// disagree. Every tracked save of a new order or of a changed snapshot sets it (AppDbContext), the order-snapshot
// write outside the change tracker sets it too (MarketplaceJobProcessor), and older orders are filled in by
// BackfillAsync with the hourly maintenance pass. A change to the rule must clear the column (a migration
// `UPDATE sales.orders SET "BuyerType" = NULL`) so the backfill recomputes every order by the new rule.
internal static class OrderBuyerTypes
{
    internal const int BackfillBatchSize = 500;
    // At most this many orders per pass (startup and hourly); a larger backlog finishes on the next passes.
    internal const int BackfillMaxBatchesPerPass = 40;

    private static readonly string[] SnapshotColumns = [nameof(Order.CustomerSnapshotJson), nameof(Order.InvoiceAddressSnapshotJson)];

    public static string Resolve(string? customerSnapshotJson, string? invoiceAddressSnapshotJson) =>
        TrendyolEFaturamCanonicalPayload.ResolveBuyerType(customerSnapshotJson, invoiceAddressSnapshotJson).Kind;

    // Before a save: a new order, or one whose customer or invoice address snapshot changed (or that has no kind yet),
    // gets its kind from the snapshots it is saved with.
    public static void Apply(ChangeTracker changeTracker)
    {
        foreach (var entry in changeTracker.Entries<Order>())
        {
            if (entry.State == EntityState.Added
                || entry.State == EntityState.Modified && (entry.Entity.BuyerType is null || SnapshotColumns.Any(column => entry.Property(column).IsModified)))
            {
                var kind = Resolve(entry.Entity.CustomerSnapshotJson, entry.Entity.InvoiceAddressSnapshotJson);
                if (!string.Equals(entry.Entity.BuyerType, kind, StringComparison.Ordinal)) entry.Entity.BuyerType = kind;
            }
        }
    }

    // Orders saved before the column existed: read in batches, decided in code by the same rule, and written only where
    // the kind is still empty, so a save that set it meanwhile (from newer snapshots) wins. Idempotent; returns the
    // number of orders filled in.
    public static async Task<int> BackfillAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var filled = 0;
        for (var batch = 0; batch < BackfillMaxBatchesPerPass; batch++)
        {
            var rows = await db.Orders.AsNoTracking()
                .Where(x => x.BuyerType == null)
                .OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.CustomerSnapshotJson, x.InvoiceAddressSnapshotJson })
                .Take(BackfillBatchSize)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var group in rows.GroupBy(row => Resolve(row.CustomerSnapshotJson, row.InvoiceAddressSnapshotJson)))
            {
                var kind = group.Key;
                var ids = group.Select(row => row.Id).ToArray();
                filled += await db.Orders.Where(x => ids.Contains(x.Id) && x.BuyerType == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.BuyerType, kind), cancellationToken);
            }
            if (rows.Count < BackfillBatchSize) break;
        }
        return filled;
    }

    // The label shown on the order (list and detail): the stored kind when there is one, so it is exactly what the
    // filter matched; the reason comes from the rule when it gives the same kind. An order not yet backfilled shows the
    // rule's own answer, the value the backfill will store.
    public static OrderBuyerTypeView Label(string? storedKind, string? customerSnapshotJson, string? invoiceAddressSnapshotJson)
    {
        var computed = TrendyolEFaturamCanonicalPayload.ResolveBuyerType(customerSnapshotJson, invoiceAddressSnapshotJson);
        if (storedKind is null || string.Equals(storedKind, computed.Kind, StringComparison.Ordinal)) return computed;
        return new(storedKind, storedKind switch
        {
            OrderBuyerTypeView.Corporate => "Kurumsal olarak kayıtlı",
            OrderBuyerTypeView.CorporateTaxIdMissing => "Kurumsal olarak kayıtlı, vergi numarası yok — fatura kesilirken sorulacak",
            _ => "Bireysel olarak kayıtlı"
        });
    }
}
