using System.Linq.Expressions;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-9: the Returns page's card columns of a return claim (ReturnClaim.TabGroup, CardSplit, ApprovedAt), so the
// database can count, filter and page the page's cards instead of the server reading every claim and line of the tenant.
// The rules stay the page's own C# rules (ReturnQueryService.ReturnTabGroup, ReturnStatusWire, MaySplitReturnClaim and
// the approval date ReturnSortDatesAsync reads); this class is the one place that writes the columns:
//   - every save (AppDbContext.SaveChanges) of a claim, of its lines or of its decisions: a claim the save itself adds or
//     changes takes them in the same save (BeforeSaveAsync); any other claim whose lines or decisions it saves is written
//     right after it, without the change tracker (AfterSaveAsync), so a save that leaves the claim alone never depends on
//     the claim's version (SIPARIS-STOK-5) and never starts tracking it;
//   - the deploy (DatabaseMigrator) and the worker's periodic repair pass fill the rows written before the columns existed,
//     by the old code during a deploy, or by anything that bypassed the save (RepairAsync);
//   - the page uses them only while every claim of the tenant has them filled and equal to the rule (Stale), and otherwise
//     computes the page as before, so a missing or stale value never changes what the user sees. That is also why a
//     failure of the hook outside a transaction is only logged: the claim stays stale and the page falls back.
public static class ReturnCardColumns
{
    private const string ApproveAction = "APPROVE";
    private const string Succeeded = "SUCCEEDED";

    public static string TabGroup(ReturnClaimStatus status, IReadOnlyCollection<string?> lineRawStatuses, bool exchangeMade = false) =>
        ReturnQueryService.ReturnTabGroup(ReturnQueryService.ReturnStatusWire(status, lineRawStatuses, exchangeMade));

    public static bool CardSplit(IEnumerable<string?> lineRawStatuses) => ReturnQueryService.MaySplitReturnClaim(lineRawStatuses);

    // As the page sorts by approval: an approved or completed claim's last successful approval, else its last change.
    public static DateTimeOffset? ApprovedAt(ReturnClaimStatus status, DateTimeOffset? lastApproval, DateTimeOffset? lastRemoteModifiedAt) =>
        status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed ? (lastApproval ?? lastRemoteModifiedAt)?.ToUniversalTime() : null;

    public static (string TabGroup, bool CardSplit, DateTimeOffset? ApprovedAt) Values(ReturnClaimStatus status, DateTimeOffset? lastRemoteModifiedAt, IReadOnlyCollection<string?> lineRawStatuses, DateTimeOffset? lastApproval, bool exchangeMade = false) =>
        (TabGroup(status, lineRawStatuses, exchangeMade), CardSplit(lineRawStatuses), ApprovedAt(status, lastApproval, lastRemoteModifiedAt));

    // Before the save (after the context detected its changes): the claims the save itself adds or changes take their
    // columns through the tracker, from the database rows overlaid with the lines and decisions this save adds, changes or
    // deletes (an unchanged tracked row counts as the database has it now). Returns the other claims the save touches
    // (their lines or decisions saved), for AfterSaveAsync. Never queries with tracking.
    public static async Task<IReadOnlyCollection<(Guid TenantId, Guid ClaimId)>> BeforeSaveAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var tracker = db.ChangeTracker;
        var autoDetect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            static bool Saved(EntityState state) => state is EntityState.Added or EntityState.Modified or EntityState.Deleted;
            var claimEntries = tracker.Entries<ReturnClaim>().ToList();
            var lineEntries = tracker.Entries<ReturnLine>().Where(entry => Saved(entry.State)).ToList();
            var decisionEntries = tracker.Entries<ReturnDecision>().Where(entry => Saved(entry.State)).ToList();
            var touched = lineEntries.Select(entry => (entry.Entity.TenantId, ClaimId: entry.Entity.ClaimId))
                .Concat(decisionEntries.Select(entry => (entry.Entity.TenantId, entry.Entity.ClaimId)))
                .ToHashSet();
            var claims = claimEntries.Where(entry => entry.State is EntityState.Added or EntityState.Modified)
                .ToDictionary(entry => (entry.Entity.TenantId, ClaimId: entry.Entity.Id));
            touched.ExceptWith(claimEntries.Where(entry => entry.State == EntityState.Deleted).Select(entry => (entry.Entity.TenantId, entry.Entity.Id)));
            touched.ExceptWith(claims.Keys);
            if (claims.Count == 0) return touched;

            var tenantIds = claims.Keys.Select(key => key.TenantId).Distinct().ToArray();
            var claimIds = claims.Keys.Select(key => key.ClaimId).ToArray();
            var lines = (await db.ReturnLines.AsNoTracking().Where(x => tenantIds.Contains(x.TenantId) && claimIds.Contains(x.ClaimId))
                    .Select(x => new { x.Id, x.TenantId, x.ClaimId, x.RawStatus }).ToListAsync(cancellationToken))
                .ToDictionary(x => x.Id, x => (x.TenantId, x.ClaimId, x.RawStatus));
            foreach (var entry in lineEntries)
            {
                lines.Remove(entry.Entity.Id);
                if (entry.State != EntityState.Deleted) lines[entry.Entity.Id] = (entry.Entity.TenantId, entry.Entity.ClaimId, entry.Entity.RawStatus);
            }
            var approvals = (await db.ReturnDecisions.AsNoTracking().Where(x => tenantIds.Contains(x.TenantId) && claimIds.Contains(x.ClaimId) && x.Action == ApproveAction && x.Status == Succeeded)
                    .Select(x => new { x.Id, x.TenantId, x.ClaimId, x.CompletedAt }).ToListAsync(cancellationToken))
                .ToDictionary(x => x.Id, x => (x.TenantId, x.ClaimId, x.CompletedAt));
            foreach (var entry in decisionEntries)
            {
                approvals.Remove(entry.Entity.Id);
                if (entry.State != EntityState.Deleted && entry.Entity.Action == ApproveAction && entry.Entity.Status == Succeeded)
                    approvals[entry.Entity.Id] = (entry.Entity.TenantId, entry.Entity.ClaimId, entry.Entity.CompletedAt);
            }
            var linesByClaim = lines.Values.ToLookup(x => (x.TenantId, x.ClaimId), x => x.RawStatus);
            var approvalsByClaim = approvals.Values.ToLookup(x => (x.TenantId, x.ClaimId), x => x.CompletedAt);

            foreach (var (key, entry) in claims)
            {
                var claim = entry.Entity;
                var values = Values(claim.Status, claim.LastRemoteModifiedAt, linesByClaim[key].ToList(), approvalsByClaim[key].Max(), ReturnCustomerRequestPolicy.ExchangeMade(claim.CustomerRequest, claim.ResolvedWith));
                Set(entry, x => x.TabGroup, values.TabGroup);
                Set(entry, x => x.CardSplit, values.CardSplit);
                Set(entry, x => x.ApprovedAt, values.ApprovedAt);
            }
            return touched;
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    // After a successful save: the other claims it touched are written from the database as it now stands, by the repair
    // pass's conditional update (no tracker, no version check).
    public static async Task AfterSaveAsync(AppDbContext db, IReadOnlyCollection<(Guid TenantId, Guid ClaimId)> claims, CancellationToken cancellationToken)
    {
        if (claims.Count == 0) return;
        var tenantIds = claims.Select(key => key.TenantId).Distinct().ToArray();
        var claimIds = claims.Select(key => key.ClaimId).Distinct().ToArray();
        await RefreshAsync(db, db.ReturnClaims.AsNoTracking().Where(x => tenantIds.Contains(x.TenantId) && claimIds.Contains(x.Id)), cancellationToken);
    }

    // AppDbContext's guard around both steps: inside the caller's transaction a failure fails the save (it is rolled back
    // with it); outside one it is logged and the claim stays stale until the repair pass (the page falls back meanwhile).
    public static async Task<T> GuardAsync<T>(AppDbContext db, Func<Task<T>> step, T fallback)
    {
        if (db.Database.CurrentTransaction is not null) return await step();
        try
        {
            return await step();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var logger = (db.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger(typeof(ReturnCardColumns));
            logger.LogWarning(exception, "Return card columns were not written with this save; the repair pass writes them later");
            return fallback;
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
    // left to its own save, which writes the columns itself (counted as Lost). Idempotent: a second run writes nothing.
    public static async Task<(int Claims, int Written, int Lost)> RepairAsync(AppDbContext db, CancellationToken cancellationToken, int batchSize = 500)
    {
        var (seen, written, lost) = (0, 0, 0);
        Guid? after = null;
        while (true)
        {
            var query = db.ReturnClaims.AsNoTracking();
            if (after is { } last) query = query.Where(x => x.Id.CompareTo(last) > 0);
            var batch = await RefreshAsync(db, query.OrderBy(x => x.Id).Take(batchSize), cancellationToken);
            if (batch.Seen == 0) return (seen, written, lost);
            (seen, written, lost, after) = (seen + batch.Seen, written + batch.Written, lost + batch.Lost, batch.Last);
        }
    }

    // Reads these claims (no tracking), their lines and approvals, and writes each claim whose columns differ from the rule,
    // on condition that its status, last change and columns are still as read.
    private static async Task<(int Seen, int Written, int Lost, Guid? Last)> RefreshAsync(AppDbContext db, IQueryable<ReturnClaim> claims, CancellationToken cancellationToken)
    {
        var batch = await claims.Select(x => new { x.Id, x.TenantId, x.Status, x.LastRemoteModifiedAt, x.TabGroup, x.CardSplit, x.ApprovedAt, x.CustomerRequest, x.ResolvedWith }).ToListAsync(cancellationToken);
        if (batch.Count == 0) return (0, 0, 0, null);
        var tenantIds = batch.Select(x => x.TenantId).Distinct().ToArray();
        var claimIds = batch.Select(x => x.Id).ToArray();
        var lines = (await db.ReturnLines.AsNoTracking().Where(x => tenantIds.Contains(x.TenantId) && claimIds.Contains(x.ClaimId))
                .Select(x => new { x.ClaimId, x.RawStatus }).ToListAsync(cancellationToken))
            .ToLookup(x => x.ClaimId, x => x.RawStatus);
        var approvals = await db.ReturnDecisions.AsNoTracking()
            .Where(x => tenantIds.Contains(x.TenantId) && claimIds.Contains(x.ClaimId) && x.Action == ApproveAction && x.Status == Succeeded)
            .GroupBy(x => x.ClaimId).Select(group => new { ClaimId = group.Key, CompletedAt = group.Max(x => x.CompletedAt) })
            .ToDictionaryAsync(x => x.ClaimId, x => x.CompletedAt, cancellationToken);
        var (written, lost) = (0, 0);
        foreach (var row in batch)
        {
            var (tabGroup, cardSplit, approvedAt) = Values(row.Status, row.LastRemoteModifiedAt, lines[row.Id].ToList(), approvals.GetValueOrDefault(row.Id), ReturnCustomerRequestPolicy.ExchangeMade(row.CustomerRequest, row.ResolvedWith));
            if (row.TabGroup == tabGroup && row.CardSplit == cardSplit && row.ApprovedAt == approvedAt) continue;
            var (status, remoteModifiedAt, oldTab, oldSplit, oldApproved, request, resolved) = (row.Status, row.LastRemoteModifiedAt, row.TabGroup, row.CardSplit, row.ApprovedAt, row.CustomerRequest, row.ResolvedWith);
            var updated = await db.ReturnClaims
                .Where(x => x.TenantId == row.TenantId && x.Id == row.Id && x.Status == status && x.LastRemoteModifiedAt == remoteModifiedAt
                    && x.TabGroup == oldTab && x.CardSplit == oldSplit && x.ApprovedAt == oldApproved && x.CustomerRequest == request && x.ResolvedWith == resolved)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.TabGroup, tabGroup).SetProperty(x => x.CardSplit, cardSplit).SetProperty(x => x.ApprovedAt, approvedAt), cancellationToken);
            if (updated > 0) written += updated; else lost++;
        }
        return (batch.Count, written, lost, batch[^1].Id);
    }

    // The tenants that still have a claim whose columns are missing or stale (Stale), so the worker can say which tenants'
    // Returns pages are computed the slow way.
    public static async Task<List<Guid>> StaleTenantsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var stale = new List<Guid>();
        foreach (var tenantId in await db.ReturnClaims.AsNoTracking().Select(x => x.TenantId).Distinct().ToListAsync(cancellationToken))
        {
            var rawStatuses = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.RawStatus).Distinct().ToListAsync(cancellationToken);
            var accepted = rawStatuses.Where(ReturnQueryService.IsAcceptedSplitLineStatus).ToArray();
            var waiting = rawStatuses.Where(raw => raw is not null && ReturnQueryService.IsWaitingSplitLineStatus(raw)).ToArray();
            if (await Stale(db, tenantId, accepted, waiting).AnyAsync(cancellationToken)) stale.Add(tenantId);
        }
        return stale;
    }

    // The claims of the tenant whose columns are missing or differ from the rule, as the database computes the rule:
    // TabGroup follows the claim's status, and a rejected exchange made goes to the approved tab (ReturnStatusWire only turns an approved or completed claim into
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
        // An exchange made that the marketplace answered Rejected goes to the approved tab (ReturnStatusWire, owner 18 Nov
        // 2026): CASE WHEN status = Rejected AND customer_request = CHANGE AND resolved_with = CHANGE THEN … ELSE … END.
        var change = Expression.Constant(ReturnCustomerRequestPolicy.Change, typeof(string));
        var exchangeMade = Expression.AndAlso(Expression.Equal(status, Expression.Constant(ReturnClaimStatus.Rejected)),
            Expression.AndAlso(Expression.Equal(Expression.Property(claim, nameof(ReturnClaim.CustomerRequest)), change),
                Expression.Equal(Expression.Property(claim, nameof(ReturnClaim.ResolvedWith)), change)));
        expected = Expression.Condition(exchangeMade, Expression.Constant(TabGroup(ReturnClaimStatus.Rejected, [], exchangeMade: true)), expected);
        var tabGroup = Expression.Property(claim, nameof(ReturnClaim.TabGroup));
        return Expression.OrElse(Expression.Equal(tabGroup, Expression.Constant(null, typeof(string))), Expression.NotEqual(tabGroup, expected));
    }
}
