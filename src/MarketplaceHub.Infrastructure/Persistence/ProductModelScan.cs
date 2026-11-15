using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-6 (rest): the model link is written after each save (ProductModelLinks.AfterSaveAsync) and a link that failed
// there waited for the next save of the same model. The Worker runs this pass daily, tenant by tenant: the tenant's
// model-code keys are reconciled (cards whose link failed are linked, models left without cards go, the
// PRODUCT_MODEL_AMBIGUOUS warnings follow) and every model takes its cards' shared values with the
// PRODUCT_MODEL_VALUES_DIFFER warning updated (ProductModelValues, which also covers the deploy-time recomputes of the
// fields and attributes of models the editor never saved). Nothing is merged, deleted or re-branded beyond what a save's
// reconcile does; the cards themselves are never written.
public static class ProductModelScan
{
    public sealed record Result(int Tenants, int UnlinkedCodedBefore, int UnlinkedCodedAfter, int ModelsWritten, int ModelsDiffering);

    public static async Task<Result> RunAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenants = await db.Products.AsNoTracking().Select(x => x.TenantId)
            .Union(db.ProductModels.AsNoTracking().Select(x => x.TenantId)).ToListAsync(cancellationToken);
        var total = new Result(0, 0, 0, 0, 0);
        foreach (var tenantId in tenants)
        {
            var result = await RunTenantAsync(db, tenantId, now, cancellationToken);
            total = new(total.Tenants + 1, total.UnlinkedCodedBefore + result.UnlinkedCodedBefore, total.UnlinkedCodedAfter + result.UnlinkedCodedAfter,
                total.ModelsWritten + result.ModelsWritten, total.ModelsDiffering + result.ModelsDiffering);
        }
        return total;
    }

    public static async Task<Result> RunTenantAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var before = await UnlinkedCodedCardsAsync(db, tenantId, cancellationToken);
        await ProductModelLinks.ReconcileTenantAsync(db, tenantId, now, cancellationToken);
        var values = await ProductModelValues.RefreshTenantAsync(db, tenantId, now, cancellationToken);
        return new(1, before, await UnlinkedCodedCardsAsync(db, tenantId, cancellationToken), values.Written, values.Differing.Count);
    }

    // Cards that hold a model code but are linked to no model: the ambiguous groups (KENE01) stay here by design.
    private static Task<int> UnlinkedCodedCardsAsync(AppDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        db.Products.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.FamilyId == null
            && db.ProductVariants.Any(v => v.TenantId == tenantId && v.ProductId == x.Id && v.ModelCode != null && v.ModelCode.Trim() != ""), cancellationToken);
}
