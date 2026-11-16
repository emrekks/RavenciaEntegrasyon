using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-9: the order's customer name as the screens show it (OrderSnapshotReader.ResolveCustomerName), kept in
// Order.CustomerSearchName so the Returns search is decided by the database. Every tracked save of a new order, or of one
// whose snapshots changed (or that has no name yet), sets it with a new stamp (AppDbContext). A snapshot write outside the
// tracker brings no new stamp, and the database trigger of migration CustomerSearchName empties the name then, so a name
// is never older than the snapshots it was read from; BackfillAsync fills the empty ones in the maintenance pass.
internal static class OrderCustomerSearchNames
{
    internal const int BackfillBatchSize = 500;
    internal const int BackfillMaxBatchesPerPass = 40;

    private static readonly string[] SnapshotColumns = [nameof(Order.CustomerSnapshotJson), nameof(Order.InvoiceAddressSnapshotJson), nameof(Order.ShipmentAddressSnapshotJson)];

    public static string Resolve(string? customerSnapshotJson, string? invoiceAddressSnapshotJson, string? shipmentAddressSnapshotJson) =>
        OrderSnapshotReader.ResolveCustomerName(customerSnapshotJson ?? "", invoiceAddressSnapshotJson ?? "", shipmentAddressSnapshotJson ?? "");

    public static void Apply(ChangeTracker changeTracker)
    {
        foreach (var entry in changeTracker.Entries<Order>())
        {
            if (entry.State == EntityState.Added
                || entry.State == EntityState.Modified && (entry.Entity.CustomerSearchName is null || SnapshotColumns.Any(column => entry.Property(column).IsModified)))
            {
                entry.Entity.CustomerSearchName = Resolve(entry.Entity.CustomerSnapshotJson, entry.Entity.InvoiceAddressSnapshotJson, entry.Entity.ShipmentAddressSnapshotJson);
                entry.Entity.CustomerSearchStamp = Guid.CreateVersion7();
            }
        }
    }

    // Orders without a name (saved before the column, or whose snapshots were written outside the tracker): read in
    // batches, decided in code, and written only where the snapshots are still the ones read (the name stays empty
    // otherwise, for the next pass). Idempotent; returns the number of orders filled in.
    public static async Task<int> BackfillAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var filled = 0;
        for (var batch = 0; batch < BackfillMaxBatchesPerPass; batch++)
        {
            var rows = await db.Orders.AsNoTracking()
                .Where(x => x.CustomerSearchName == null)
                .OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.CustomerSnapshotJson, x.InvoiceAddressSnapshotJson, x.ShipmentAddressSnapshotJson })
                .Take(BackfillBatchSize)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var name = Resolve(row.CustomerSnapshotJson, row.InvoiceAddressSnapshotJson, row.ShipmentAddressSnapshotJson);
                var stamp = Guid.CreateVersion7();
                filled += await db.Orders.Where(x => x.Id == row.Id && x.CustomerSearchName == null
                        && x.CustomerSnapshotJson == row.CustomerSnapshotJson && x.InvoiceAddressSnapshotJson == row.InvoiceAddressSnapshotJson && x.ShipmentAddressSnapshotJson == row.ShipmentAddressSnapshotJson)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CustomerSearchName, name).SetProperty(x => x.CustomerSearchStamp, stamp), cancellationToken);
            }
            if (rows.Count < BackfillBatchSize) break;
        }
        return filled;
    }
}
