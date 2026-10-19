using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Finds marketplace actions whose outcome is still open: those that carry their context get a
// verification job; old records written before effects carried context are closed as unresolved
// and reported, so they no longer block anything.
public sealed class ExternalEffectSweep(AppDbContext db, TimeProvider timeProvider)
{
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var verifyBefore = now - ExternalEffectReconcilePolicy.MinimumAgeForNotApplied;
        // Only records with work left are taken: one that already has its verification job, or an old
        // record not yet due to be closed, is skipped, so a backlog of those cannot keep newer
        // records from being verified.
        var candidates = await db.ExternalEffectRecords.AsNoTracking()
            .Where(x => x.CompletedAt == null && x.CreatedAt <= verifyBefore)
            .Select(x => new { x.Id, x.TenantId, x.CreatedAt, Verifiable = x.ConnectionId != null && x.Action != null })
            .ToListAsync(cancellationToken);
        var verificationKeys = candidates.Where(x => x.Verifiable).Select(x => ExternalEffectReconcilePolicy.JobDedupKey(x.Id)).ToArray();
        var queued = (await db.IntegrationJobs.AsNoTracking()
                .Where(x => verificationKeys.Contains(x.JobDedupKey))
                .Select(x => new { x.TenantId, x.JobDedupKey })
                .ToListAsync(cancellationToken))
            .Select(x => (x.TenantId, x.JobDedupKey))
            .ToHashSet();
        var dueIds = candidates
            .Where(x => x.Verifiable
                ? !queued.Contains((x.TenantId, ExternalEffectReconcilePolicy.JobDedupKey(x.Id)))
                : now - x.CreatedAt >= ExternalEffectReconcilePolicy.LegacyCloseAfter)
            .OrderBy(x => x.CreatedAt)
            .Take(100)
            .Select(x => x.Id)
            .ToArray();
        if (dueIds.Length == 0) return 0;
        var open = await db.ExternalEffectRecords.Where(x => dueIds.Contains(x.Id)).OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var effect in open)
        {
            if (effect.ConnectionId is not null && effect.Action is not null)
            {
                db.IntegrationJobs.Add(ExternalEffectReconcilePolicy.NewJob(effect, now, now));
                changed++;
            }
            else if (now - effect.CreatedAt >= ExternalEffectReconcilePolicy.LegacyCloseAfter)
            {
                effect.CompletedAt = now;
                effect.Outcome = "UNRESOLVED";
                db.OperationalIssues.Add(new OperationalIssue
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = effect.TenantId,
                    DedupeKey = $"external-effect:{effect.Id}",
                    Code = "EXTERNAL_EFFECT_UNRESOLVED",
                    Summary = $"{effect.EffectType} işleminin sonucu kesinleşmeden kaldı ve kapatıldı; pazaryeri panelinden kontrol edin.",
                    Status = IssueStatus.Open,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    OccurrenceCount = 1
                });
                changed++;
            }
        }
        if (changed > 0) await db.SaveChangesAsync(cancellationToken);
        return changed;
    }
}
