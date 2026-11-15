using System.Linq.Expressions;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-9: the Returns page's card columns of a return claim (ReturnClaim.TabGroup, CardSplit, ApprovedAt), so the
// database can count, filter and page the page's cards instead of the server reading every claim and line of the tenant.
// The rules stay the page's own C# rules (MarketplaceSalesService.ReturnTabGroup, ReturnStatusWire, MaySplitReturnClaim and
// the approval date ReturnSortDatesAsync reads); this class is the one place that writes the columns:
//   - every save (AppDbContext.SaveChanges) of a claim, of its lines or of its decisions sets them from the state being
//     saved, in the same save (ApplyToSaveAsync);
//   - the deploy (DatabaseMigrator) and the worker's periodic repair pass fill the rows written before the columns existed,
//     by the old code during a deploy, or by anything that bypassed the save (RepairAsync);
//   - the page uses them only while every claim of the tenant has them filled and equal to the rule (Stale), and otherwise
//     computes the page as before, so a missing or stale value never changes what the user sees.
internal static class ReturnCardColumns
{
    private const string ApproveAction = "APPROVE";
    private const string Succeeded = "SUCCEEDED";

    public static string TabGroup(ReturnClaimStatus status, IReadOnlyCollection<string?> lineRawStatuses) =>
        MarketplaceSalesService.ReturnTabGroup(MarketplaceSalesService.ReturnStatusWire(status, lineRawStatuses));

    public static bool CardSplit(IEnumerable<string?> lineRawStatuses) => MarketplaceSalesService.MaySplitReturnClaim(lineRawStatuses);

    // As the page sorts by approval: an approved or completed claim's last successful approval, else its last change.
    public static DateTimeOffset? ApprovedAt(ReturnClaimStatus status, DateTimeOffset? lastApproval, DateTimeOffset? lastRemoteModifiedAt) =>
        status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed ? (lastApproval ?? lastRemoteModifiedAt)?.ToUniversalTime() : null;

    public static (string TabGroup, bool CardSplit, DateTimeOffset? ApprovedAt) Values(ReturnClaimStatus status, DateTimeOffset? lastRemoteModifiedAt, IReadOnlyCollection<string?> lineRawStatuses, DateTimeOffset? lastApproval) =>
        (TabGroup(status, lineRawStatuses), CardSplit(lineRawStatuses), ApprovedAt(status, lastApproval, lastRemoteModifiedAt));

    // The save hook: each claim saved, or whose lines or decisions are saved, takes its columns from the state being saved
    // (the rows in the database overlaid with the tracked ones). Runs after the context has detected its changes.
    public static async Task ApplyToSaveAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var tracker = db.ChangeTracker;
        var autoDetect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            static bool Saved(EntityState state) => state is EntityState.Added or EntityState.Modified or EntityState.Deleted;
            var claimEntries = tracker.Entries<ReturnClaim>().ToList();
            var lineEntries = tracker.Entries<ReturnLine>().ToList();
            var decisionEntries = tracker.Entries<ReturnDecision>().ToList();
            var affected = claimEntries.Where(entry => entry.State is EntityState.Added or EntityState.Modified).Select(entry => (entry.Entity.TenantId, ClaimId: entry.Entity.Id))
                .Concat(lineEntries.Where(entry => Saved(entry.State)).Select(entry => (entry.Entity.TenantId, entry.Entity.ClaimId)))
                .Concat(decisionEntries.Where(entry => Saved(entry.State)).Select(entry => (entry.Entity.TenantId, entry.Entity.ClaimId)))
                .ToHashSet();
            if (affected.Count == 0) return;
            affected.ExceptWith(claimEntries.Where(entry => entry.State == EntityState.Deleted).Select(entry => (entry.Entity.TenantId, entry.Entity.Id)));
            if (affected.Count == 0) return;

            var claims = claimEntries.Where(entry => entry.State is not (EntityState.Deleted or EntityState.Detached) && affected.Contains((entry.Entity.TenantId, entry.Entity.Id)))
                .ToDictionary(entry => (entry.Entity.TenantId, entry.Entity.Id));
            var tenantIds = affected.Select(key => key.TenantId).Distinct().ToArray();
            var claimIds = affected.Select(key => key.ClaimId).Distinct().ToArray();
            var untracked = affected.Where(key => !claims.ContainsKey(key)).Select(key => key.ClaimId).ToArray();
            if (untracked.Length > 0)
                foreach (var claim in await db.ReturnClaims.Where(x => tenantIds.Contains(x.TenantId) && untracked.Contains(x.Id)).ToListAsync(cancellationToken))
                    if (affected.Contains((claim.TenantId, claim.Id))) claims[(claim.TenantId, claim.Id)] = db.Entry(claim);
            if (claims.Count == 0) return;

            var lines = (await db.ReturnLines.AsNoTracking().Where(x => tenantIds.Contains(x.TenantId) && claimIds.Contains(x.ClaimId))
                    .Select(x => new { x.Id, x.TenantId, x.ClaimId, x.RawStatus }).ToListAsync(cancellationToken))
                .ToDictionary(x => x.Id, x => (x.TenantId, x.ClaimId, x.RawStatus));
            foreach (var entry in lineEntries.Where(entry => affected.Contains((entry.Entity.TenantId, entry.Entity.ClaimId))))
            {
                lines.Remove(entry.Entity.Id);
                if (entry.State is not (EntityState.Deleted or EntityState.Detached)) lines[entry.Entity.Id] = (entry.Entity.TenantId, entry.Entity.ClaimId, entry.Entity.RawStatus);
            }
            var approvals = (await db.ReturnDecisions.AsNoTracking().Where(x => tenantIds.Contains(x.TenantId) && claimIds.Contains(x.ClaimId) && x.Action == ApproveAction && x.Status == Succeeded)
                    .Select(x => new { x.Id, x.TenantId, x.ClaimId, x.CompletedAt }).ToListAsync(cancellationToken))
                .ToDictionary(x => x.Id, x => (x.TenantId, x.ClaimId, x.CompletedAt));
            foreach (var entry in decisionEntries.Where(entry => affected.Contains((entry.Entity.TenantId, entry.Entity.ClaimId))))
            {
                approvals.Remove(entry.Entity.Id);
                if (entry.State is not (EntityState.Deleted or EntityState.Detached) && entry.Entity.Action == ApproveAction && entry.Entity.Status == Succeeded)
                    approvals[entry.Entity.Id] = (entry.Entity.TenantId, entry.Entity.ClaimId, entry.Entity.CompletedAt);
            }
            var linesByClaim = lines.Values.ToLookup(x => (x.TenantId, x.ClaimId), x => x.RawStatus);
            var approvalsByClaim = approvals.Values.ToLookup(x => (x.TenantId, x.ClaimId), x => x.CompletedAt);

            foreach (var (key, entry) in claims)
            {
                var claim = entry.Entity;
                var values = Values(claim.Status, claim.LastRemoteModifiedAt, linesByClaim[key].ToList(), approvalsByClaim[key].Max());
                Set(entry, x => x.TabGroup, values.TabGroup);
                Set(entry, x => x.CardSplit, values.CardSplit);
                Set(entry, x => x.ApprovedAt, values.ApprovedAt);
            }
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    // Through the entry, so the change is marked whether or not the context detects changes on its own.
    private static void Set<T>(EntityEntry<ReturnClaim> entry, Expression<Func<ReturnClaim, T>> property, T value)
    {
        var current = entry.Property(property);
        if (!EqualityComparer<T>.Default.Equals(current.CurrentValue, value)) current.CurrentValue = value;
    }

    // The deploy's backfill and the worker's repair pass: every claim whose columns are missing or differ from the rule is
    // written, in batches by claim id. A claim changed meanwhile (its status, last change or columns no longer as read) is
    // left to its own save, which writes the columns itself. Idempotent: a second run writes nothing.
    public static async Task<(int Claims, int Written)> RepairAsync(AppDbContext db, CancellationToken cancellationToken, int batchSize = 500)
    {
        var (seen, written) = (0, 0);
        Guid? after = null;
        while (true)
        {
            var query = db.ReturnClaims.AsNoTracking();
            if (after is { } last) query = query.Where(x => x.Id.CompareTo(last) > 0);
            var batch = await query.OrderBy(x => x.Id).Take(batchSize)
                .Select(x => new { x.Id, x.TenantId, x.Status, x.LastRemoteModifiedAt, x.TabGroup, x.CardSplit, x.ApprovedAt })
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) return (seen, written);
            seen += batch.Count;
            after = batch[^1].Id;
            var tenantIds = batch.Select(x => x.TenantId).Distinct().ToArray();
            var claimIds = batch.Select(x => x.Id).ToArray();
            var lines = (await db.ReturnLines.AsNoTracking().Where(x => tenantIds.Contains(x.TenantId) && claimIds.Contains(x.ClaimId))
                    .Select(x => new { x.ClaimId, x.RawStatus }).ToListAsync(cancellationToken))
                .ToLookup(x => x.ClaimId, x => x.RawStatus);
            var approvals = await db.ReturnDecisions.AsNoTracking()
                .Where(x => tenantIds.Contains(x.TenantId) && claimIds.Contains(x.ClaimId) && x.Action == ApproveAction && x.Status == Succeeded)
                .GroupBy(x => x.ClaimId).Select(group => new { ClaimId = group.Key, CompletedAt = group.Max(x => x.CompletedAt) })
                .ToDictionaryAsync(x => x.ClaimId, x => x.CompletedAt, cancellationToken);
            foreach (var row in batch)
            {
                var (tabGroup, cardSplit, approvedAt) = Values(row.Status, row.LastRemoteModifiedAt, lines[row.Id].ToList(), approvals.GetValueOrDefault(row.Id));
                if (row.TabGroup == tabGroup && row.CardSplit == cardSplit && row.ApprovedAt == approvedAt) continue;
                var (status, remoteModifiedAt, oldTab, oldSplit, oldApproved) = (row.Status, row.LastRemoteModifiedAt, row.TabGroup, row.CardSplit, row.ApprovedAt);
                written += await db.ReturnClaims
                    .Where(x => x.TenantId == row.TenantId && x.Id == row.Id && x.Status == status && x.LastRemoteModifiedAt == remoteModifiedAt
                        && x.TabGroup == oldTab && x.CardSplit == oldSplit && x.ApprovedAt == oldApproved)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.TabGroup, tabGroup).SetProperty(x => x.CardSplit, cardSplit).SetProperty(x => x.ApprovedAt, approvedAt), cancellationToken);
            }
        }
    }

    // The claims of the tenant whose columns are missing or differ from the rule, as the database computes the rule:
    // TabGroup follows the claim's status alone (ReturnStatusWire only turns an approved or completed claim into
    // PARTIALLY_APPROVED, the same tab), CardSplit is "a line with an accepted raw status beside one waiting or without
    // a status" (the tenant's raw statuses sorted into accepted and waiting by the C# rule), ApprovedAt as above.
    public static IQueryable<ReturnClaim> Stale(AppDbContext db, Guid tenantId, string?[] acceptedRawStatuses, string?[] waitingRawStatuses)
    {
        Expression<Func<ReturnClaim, bool>> others = x => x.CardSplit == null
            || x.CardSplit != (db.ReturnLines.Any(line => line.TenantId == tenantId && line.ClaimId == x.Id && acceptedRawStatuses.Contains(line.RawStatus))
                && db.ReturnLines.Any(line => line.TenantId == tenantId && line.ClaimId == x.Id && (line.RawStatus == null || waitingRawStatuses.Contains(line.RawStatus))))
            || x.ApprovedAt != (x.Status == ReturnClaimStatus.Approved || x.Status == ReturnClaimStatus.Completed
                ? db.ReturnDecisions.Where(decision => decision.TenantId == tenantId && decision.ClaimId == x.Id && decision.Action == ApproveAction && decision.Status == Succeeded)
                    .Max(decision => decision.CompletedAt) ?? x.LastRemoteModifiedAt
                : null);
        var claim = others.Parameters[0];
        var stale = Expression.OrElse(TabGroupDiffers(claim), others.Body);
        return db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId).Where(Expression.Lambda<Func<ReturnClaim, bool>>(stale, claim));
    }

    // x.TabGroup IS NULL OR x.TabGroup <> CASE x.Status WHEN … THEN ReturnTabGroup(status) … END, built from the enum so a new
    // status is never missed.
    private static Expression TabGroupDiffers(ParameterExpression claim)
    {
        var statuses = Enum.GetValues<ReturnClaimStatus>();
        var status = Expression.Property(claim, nameof(ReturnClaim.Status));
        Expression expected = Expression.Constant(TabGroup(statuses[^1], []));
        foreach (var value in statuses[..^1].Reverse())
            expected = Expression.Condition(Expression.Equal(status, Expression.Constant(value)), Expression.Constant(TabGroup(value, [])), expected);
        var tabGroup = Expression.Property(claim, nameof(ReturnClaim.TabGroup));
        return Expression.OrElse(Expression.Equal(tabGroup, Expression.Constant(null, typeof(string))), Expression.NotEqual(tabGroup, expected));
    }
}
