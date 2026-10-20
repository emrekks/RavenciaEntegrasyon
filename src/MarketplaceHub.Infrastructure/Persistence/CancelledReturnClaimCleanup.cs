using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class CancelledReturnClaimCleanup
{
    public const string RetainedIssueCode = "RETURN_CLAIM_CANCELLED_WITH_HISTORY";

    // A claim the marketplace cancelled is dropped only while nothing was done
    // with it. Once it has a decision, evidence or a stock disposition, that
    // history (and the idempotency keys it holds) must survive: the claim is
    // kept as Cancelled and an operational issue asks for the stock to be
    // checked. Returns true when the claim was kept.
    public static async Task<bool> RemoveOrRetainAsync(AppDbContext db, Guid tenantId, Guid claimId, string remoteRawStatus, DateTimeOffset remoteModifiedAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await HasHistoryAsync(db, tenantId, claimId, cancellationToken))
        {
            await RemoveAsync(db, tenantId, claimId, cancellationToken);
            return false;
        }

        var claim = db.ReturnClaims.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Id == claimId)
            ?? await db.ReturnClaims.SingleAsync(x => x.TenantId == tenantId && x.Id == claimId, cancellationToken);
        if (claim.Status != ReturnClaimStatus.Cancelled)
        {
            claim.Status = ReturnClaimStatus.Cancelled;
            claim.RawStatus = remoteRawStatus;
            if (remoteModifiedAt > claim.LastRemoteModifiedAt) claim.LastRemoteModifiedAt = remoteModifiedAt;
            claim.UpdatedAt = now;
            claim.Version++;
        }

        var key = $"return-cancelled-with-history:{claimId:N}";
        var summary = $"{claim.ExternalClaimId} numaralı iade pazaryerinde iptal edildi; verilen karar, kanıt ve stok hareketleri korunarak iade iptal olarak saklandı. Stoğa eklenen adetleri kontrol edin.";
        var issue = db.OperationalIssues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.DedupeKey == key)
            ?? await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
        if (issue is null)
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = RetainedIssueCode, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
        else if (issue.Status != IssueStatus.Open)
        {
            issue.Status = IssueStatus.Open;
            issue.LastSeenAt = now;
            issue.OccurrenceCount++;
        }
        return true;
    }

    public static async Task<bool> HasHistoryAsync(AppDbContext db, Guid tenantId, Guid claimId, CancellationToken cancellationToken) =>
        await db.ReturnDecisions.AnyAsync(x => x.TenantId == tenantId && x.ClaimId == claimId, cancellationToken)
        || await db.ReturnEvidence.AnyAsync(x => x.TenantId == tenantId && x.ClaimId == claimId, cancellationToken)
        || await db.ReturnStockDispositions.AnyAsync(x => x.TenantId == tenantId && x.ClaimId == claimId, cancellationToken);

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
