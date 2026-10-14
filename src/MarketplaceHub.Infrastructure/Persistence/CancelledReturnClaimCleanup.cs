using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class CancelledReturnClaimCleanup
{
    public static async Task RemoveAsync(AppDbContext db, Guid tenantId, Guid claimId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.ReturnStockDispositions.Where(x => x.TenantId == tenantId && x.ClaimId == claimId).ExecuteDeleteAsync(cancellationToken);
        await db.ReturnEvidence.Where(x => x.TenantId == tenantId && x.ClaimId == claimId).ExecuteDeleteAsync(cancellationToken);
        await db.ReturnDecisions.Where(x => x.TenantId == tenantId && x.ClaimId == claimId).ExecuteDeleteAsync(cancellationToken);
        await db.ReturnLines.Where(x => x.TenantId == tenantId && x.ClaimId == claimId).ExecuteDeleteAsync(cancellationToken);
        await db.ReturnClaims.Where(x => x.TenantId == tenantId && x.Id == claimId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
