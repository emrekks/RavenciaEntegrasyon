using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// An order line keeps its variant's cost price and desi as they were when it was first tied to the variant (stored
// with a match, or matched later), so the reports cost a past sale at what it cost then: a new purchase price entered
// today does not move last month's profit. A value of 0 or below is not kept (a cost of 0 is a missing one to the
// reports), so the line follows the variant, as one whose variant had no value then does (ReportsService.LineRowsAsync:
// the line's value, else the variant's current one). The values stay on the line for good: when its variant is deleted
// (VariantId set to null) the sale keeps its cost, and a line matched again later keeps what it took the first time.
// Lines tied to a variant before these columns existed took the variant's values of the migration day
// (AddOrderLineCostSnapshot).
internal static class OrderLineCostSnapshot
{
    public static decimal? Entered(decimal? value) => value is > 0m ? value : null;

    // Lines just tied to their variant that hold neither value yet take the variant's, read once for all of them.
    public static async Task TakeAsync(AppDbContext db, Guid tenantId, IEnumerable<OrderLine> lines, CancellationToken cancellationToken)
    {
        var pending = lines.Where(line => line.VariantId is not null && line.UnitCostSnapshot is null && line.DesiSnapshot is null).DistinctBy(line => line.Id).ToList();
        if (pending.Count == 0) return;
        var variantIds = pending.Select(line => line.VariantId!.Value).Distinct().ToArray();
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId && variantIds.Contains(variant.Id))
            .Select(variant => new { variant.Id, variant.CostPrice, variant.Desi })
            .ToDictionaryAsync(variant => variant.Id, cancellationToken);
        foreach (var line in pending)
        {
            if (!variants.TryGetValue(line.VariantId!.Value, out var variant)) continue;
            line.UnitCostSnapshot = Entered(variant.CostPrice);
            line.DesiSnapshot = Entered(variant.Desi);
        }
    }
}
