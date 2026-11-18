using MarketplaceHub.Domain;
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
    public sealed record Result(int Tenants, int UnlinkedCodedBefore, int UnlinkedCodedAfter, int ModelsWritten, int ModelsDiffering, int ModelsAttributesSpread = 0);

    public static async Task<Result> RunAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenants = await TenantsAsync(db, cancellationToken);
        var total = new Result(0, 0, 0, 0, 0);
        foreach (var tenantId in tenants)
        {
            var result = await RunTenantAsync(db, tenantId, now, cancellationToken);
            total = new(total.Tenants + 1, total.UnlinkedCodedBefore + result.UnlinkedCodedBefore, total.UnlinkedCodedAfter + result.UnlinkedCodedAfter,
                total.ModelsWritten + result.ModelsWritten, total.ModelsDiffering + result.ModelsDiffering, total.ModelsAttributesSpread + result.ModelsAttributesSpread);
        }
        return total;
    }

    // The tenants with cards or models, and those with an open model warning but nothing left (their warning is resolved).
    public static Task<List<Guid>> TenantsAsync(AppDbContext db, CancellationToken cancellationToken) =>
        db.Products.AsNoTracking().Select(x => x.TenantId)
            .Union(db.ProductModels.AsNoTracking().Select(x => x.TenantId))
            .Union(db.OperationalIssues.AsNoTracking()
                .Where(x => (x.Code == ProductModelValues.DifferCode || x.Code == ProductModelLinks.AmbiguousCode) && x.Status != IssueStatus.Resolved && x.TenantId != null)
                .Select(x => x.TenantId!.Value))
            .ToListAsync(cancellationToken);

    public static async Task<Result> RunTenantAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var before = await UnlinkedCodedCardsAsync(db, tenantId, cancellationToken);
        await ProductModelLinks.ReconcileTenantAsync(db, tenantId, now, cancellationToken);
        var values = await ProductModelValues.RefreshTenantAsync(db, tenantId, now, cancellationToken);
        return new(1, before, await UnlinkedCodedCardsAsync(db, tenantId, cancellationToken), values.Written, values.Differing.Count, values.AttributesSpread);
    }

    // Cards that hold a model code but are linked to no model of a code: no model (before KATALOG-14 D5 B1) or a model of
    // their own ("#<card id>", since products.FamilyId is NOT NULL).
    private static Task<int> UnlinkedCodedCardsAsync(AppDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        db.Products.AsNoTracking().CountAsync(x => x.TenantId == tenantId
            && (x.FamilyId == null || db.ProductModels.Any(m => m.TenantId == tenantId && m.Id == x.FamilyId && m.ModelCodeKey.StartsWith(ProductModelLinks.OwnModelKeyPrefix)))
            && db.ProductVariants.Any(v => v.TenantId == tenantId && v.ProductId == x.Id && v.ModelCode != null && v.ModelCode.Trim() != ""), cancellationToken);
}
