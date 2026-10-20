using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Finds marketplace actions whose outcome is still open: those that carry their context get a
// verification job; old records written before effects carried context are closed as unresolved
// and reported, so they no longer block anything.
// A verification job that ended without closing its record (dead, blocked, cancelled, sent to review)
// no longer counts as queued: the record gets a new job under a fresh dedup suffix, at most
// MaximumVerificationJobs in all, and after that it is closed as unresolved and reported.
public sealed class ExternalEffectSweep(AppDbContext db, TimeProvider timeProvider)
{
    public const int MaximumVerificationJobs = 3;

    // The dedup keys a record's verification jobs use, in order: the original key, then ":2", ":3", ...
    internal static IReadOnlyList<string> VerificationKeys(Guid effectId)
    {
        var first = ExternalEffectReconcilePolicy.JobDedupKey(effectId);
        return [first, .. Enumerable.Range(2, MaximumVerificationJobs - 1).Select(index => $"{first}:{index}")];
    }

    // What the sweep does with a verifiable open record, given the statuses of its verification jobs:
    // nothing while one is still queued or running, the next unused key while the cap allows, or close.
    internal static (bool Close, string? NextKey) Decide(Guid effectId, IReadOnlyDictionary<string, JobStatus> jobs)
    {
        if (jobs.Values.Any(status => status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled)) return (false, null);
        var next = VerificationKeys(effectId).FirstOrDefault(key => !jobs.ContainsKey(key));
        return next is null ? (true, null) : (false, next);
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var verifyBefore = now - ExternalEffectReconcilePolicy.MinimumAgeForNotApplied;
        // Only records with work left are taken: one whose verification job is still queued or running, or
        // an old record not yet due to be closed, is skipped, so a backlog of those cannot keep newer
        // records from being verified.
        var candidates = await db.ExternalEffectRecords.AsNoTracking()
            .Where(x => x.CompletedAt == null && x.CreatedAt <= verifyBefore)
            .Select(x => new { x.Id, x.TenantId, x.CreatedAt, Verifiable = x.ConnectionId != null && x.Action != null })
            .ToListAsync(cancellationToken);
        var verificationKeys = candidates.Where(x => x.Verifiable).SelectMany(x => VerificationKeys(x.Id)).ToArray();
        var jobs = (await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.JobType == MarketplaceJobTypes.ExternalEffectReconcile && verificationKeys.Contains(x.JobDedupKey))
                .Select(x => new { x.TenantId, x.JobDedupKey, x.Status })
                .ToListAsync(cancellationToken))
            .ToLookup(x => x.TenantId);
        var decisions = new Dictionary<Guid, (bool Close, string? NextKey)>();
        foreach (var candidate in candidates.Where(x => x.Verifiable))
        {
            var keys = VerificationKeys(candidate.Id);
            var statuses = jobs[candidate.TenantId].Where(job => keys.Contains(job.JobDedupKey))
                .ToDictionary(job => job.JobDedupKey, job => job.Status, StringComparer.Ordinal);
            decisions[candidate.Id] = Decide(candidate.Id, statuses);
        }
        var dueIds = candidates
            .Where(x => x.Verifiable
                ? decisions[x.Id] is { Close: true } or { NextKey: not null }
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
                var decision = decisions[effect.Id];
                if (decision.NextKey is { } key)
                {
                    var job = ExternalEffectReconcilePolicy.NewJob(effect, now, now);
                    job.JobDedupKey = key;
                    job.EffectIdempotencyKey = key;
                    db.IntegrationJobs.Add(job);
                    changed++;
                }
                else if (decision.Close)
                {
                    await CloseUnresolvedAsync(effect, now, $"{effect.EffectType} işleminin sonucu {MaximumVerificationJobs} doğrulama denemesinde kesinleşmedi ve kapatıldı; pazaryeri panelinden kontrol edin.", cancellationToken);
                    changed++;
                }
            }
            else if (now - effect.CreatedAt >= ExternalEffectReconcilePolicy.LegacyCloseAfter)
            {
                await CloseUnresolvedAsync(effect, now, $"{effect.EffectType} işleminin sonucu kesinleşmeden kaldı ve kapatıldı; pazaryeri panelinden kontrol edin.", cancellationToken);
                changed++;
            }
        }
        if (changed > 0) await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    private async Task CloseUnresolvedAsync(ExternalEffectRecord effect, DateTimeOffset now, string summary, CancellationToken cancellationToken)
    {
        effect.CompletedAt = now;
        effect.Outcome = "UNRESOLVED";
        var dedupeKey = $"external-effect:{effect.Id}";
        var issue = await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == effect.TenantId && x.DedupeKey == dedupeKey, cancellationToken);
        if (issue is null)
        {
            db.OperationalIssues.Add(new OperationalIssue
            {
                Id = Guid.CreateVersion7(),
                TenantId = effect.TenantId,
                DedupeKey = dedupeKey,
                Code = "EXTERNAL_EFFECT_UNRESOLVED",
                Summary = summary,
                Status = IssueStatus.Open,
                FirstSeenAt = now,
                LastSeenAt = now,
                OccurrenceCount = 1
            });
            return;
        }
        issue.Code = "EXTERNAL_EFFECT_UNRESOLVED";
        issue.Summary = summary;
        issue.Status = IssueStatus.Open;
        issue.LastSeenAt = now;
        issue.OccurrenceCount++;
    }
}
