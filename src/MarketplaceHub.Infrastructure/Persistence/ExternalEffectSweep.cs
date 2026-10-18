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
        var open = await db.ExternalEffectRecords
            .Where(x => x.CompletedAt == null && x.CreatedAt <= verifyBefore)
            .OrderBy(x => x.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var effect in open)
        {
            if (effect.ConnectionId is { } connectionId && effect.Action is not null)
            {
                var dedup = ExternalEffectReconcilePolicy.JobDedupKey(effect.Id);
                if (await db.IntegrationJobs.AnyAsync(x => x.TenantId == effect.TenantId && x.JobDedupKey == dedup, cancellationToken)) continue;
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
