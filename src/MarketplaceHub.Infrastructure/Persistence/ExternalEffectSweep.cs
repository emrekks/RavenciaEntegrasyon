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
// A job that was only blocked because the connection was passive or its writes were off did not verify
// anything: it does not count toward the cap. It is queued again once the connection is operational, and
// while it is not, the record waits (closed only after LegacyCloseAfter, like a record without context).
// Closing a return decision's record settles the decision the same way the verification job does.
public sealed class ExternalEffectSweep(AppDbContext db, TimeProvider timeProvider)
{
    public const int MaximumVerificationJobs = 3;

    // Codes of a verification job that stopped before reading anything because of the connection's state.
    internal static readonly string[] ConnectionBlockCodes = ["CONNECTION_INACTIVE", "CONNECTION_OUT_OF_SCOPE", "EXTERNAL_WRITES_DISABLED"];

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
        var decision = Decide(effectId, jobs.ToDictionary(job => job.Key, job => new VerificationJob(job.Value, null, 0, int.MaxValue), StringComparer.Ordinal), connectionOperational: true);
        return (decision.Close, decision.NextKey);
    }

    internal sealed record VerificationJob(JobStatus Status, string? ErrorCode, int AttemptCount, int MaxAttempts);

    // Revive: the key of a job blocked only by the connection's state, to be queued again (it does not count
    // toward the cap). Wait: such a job exists but the connection is still not operational.
    internal sealed record VerificationDecision(bool Close, string? NextKey, string? ReviveKey, bool Wait);

    internal static VerificationDecision Decide(Guid effectId, IReadOnlyDictionary<string, VerificationJob> jobs, bool connectionOperational)
    {
        if (jobs.Values.Any(job => job.Status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled)) return new(false, null, null, false);
        var keys = VerificationKeys(effectId);
        var connectionBlocked = keys.FirstOrDefault(key => jobs.TryGetValue(key, out var job) && IsConnectionBlock(job));
        if (connectionBlocked is not null)
            return connectionOperational ? new(false, null, connectionBlocked, false) : new(false, null, null, true);
        var next = keys.FirstOrDefault(key => !jobs.ContainsKey(key));
        return next is null ? new(true, null, null, false) : new(false, next, null, false);
    }

    private static bool IsConnectionBlock(VerificationJob job) =>
        job.Status == JobStatus.Blocked && job.ErrorCode is { } code && ConnectionBlockCodes.Contains(code) && job.AttemptCount < job.MaxAttempts;

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var verifyBefore = now - ExternalEffectReconcilePolicy.MinimumAgeForNotApplied;
        // Only records with work left are taken: one whose verification job is still queued or running, or
        // an old record not yet due to be closed, is skipped, so a backlog of those cannot keep newer
        // records from being verified.
        var candidates = await db.ExternalEffectRecords.AsNoTracking()
            .Where(x => x.CompletedAt == null && x.CreatedAt <= verifyBefore)
            .Select(x => new { x.Id, x.TenantId, x.CreatedAt, x.ConnectionId, Verifiable = x.ConnectionId != null && x.Action != null })
            .ToListAsync(cancellationToken);
        var verificationKeys = candidates.Where(x => x.Verifiable).SelectMany(x => VerificationKeys(x.Id)).ToArray();
        var jobs = (await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.JobType == MarketplaceJobTypes.ExternalEffectReconcile && verificationKeys.Contains(x.JobDedupKey))
                .Select(x => new { x.TenantId, x.JobDedupKey, x.Status, x.LastErrorCode, x.AttemptCount, x.MaxAttempts })
                .ToListAsync(cancellationToken))
            .ToLookup(x => x.TenantId);
        var connectionIds = candidates.Where(x => x.ConnectionId is not null).Select(x => x.ConnectionId!.Value).Distinct().ToArray();
        var operational = (await db.PlatformConnections.AsNoTracking()
                .Where(x => connectionIds.Contains(x.Id) && (x.Status == "ACTIVE" || x.Status == "VERIFIED"))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var decisions = new Dictionary<Guid, VerificationDecision>();
        foreach (var candidate in candidates.Where(x => x.Verifiable))
        {
            var keys = VerificationKeys(candidate.Id);
            var statuses = jobs[candidate.TenantId].Where(job => keys.Contains(job.JobDedupKey))
                .ToDictionary(job => job.JobDedupKey, job => new VerificationJob(job.Status, job.LastErrorCode, job.AttemptCount, job.MaxAttempts), StringComparer.Ordinal);
            decisions[candidate.Id] = Decide(candidate.Id, statuses, operational.Contains(candidate.ConnectionId!.Value));
        }
        bool LegacyDue(DateTimeOffset createdAt) => now - createdAt >= ExternalEffectReconcilePolicy.LegacyCloseAfter;
        var dueIds = candidates
            .Where(x => x.Verifiable
                ? decisions[x.Id] is { Close: true } or { NextKey: not null } or { ReviveKey: not null } || decisions[x.Id].Wait && LegacyDue(x.CreatedAt)
                : LegacyDue(x.CreatedAt))
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
                else if (decision.ReviveKey is { } reviveKey)
                {
                    if (await ReviveAsync(effect, reviveKey, now, cancellationToken)) changed++;
                }
                else if (decision.Close)
                {
                    await CloseUnresolvedAsync(effect, now, $"{effect.EffectType} işleminin sonucu {MaximumVerificationJobs} doğrulama denemesinde kesinleşmedi ve kapatıldı; pazaryeri panelinden kontrol edin.", cancellationToken);
                    changed++;
                }
                else if (decision.Wait)
                {
                    await CloseUnresolvedAsync(effect, now, $"{effect.EffectType} işleminin sonucu bağlantı uzun süre pasif kaldığı için doğrulanamadı ve kapatıldı; pazaryeri panelinden kontrol edin.", cancellationToken);
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

    // The verification job blocked by the connection runs again under its own key, keeping its attempts.
    private async Task<bool> ReviveAsync(ExternalEffectRecord effect, string key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == effect.TenantId && x.JobType == MarketplaceJobTypes.ExternalEffectReconcile && x.JobDedupKey == key, cancellationToken);
        if (job is null || job.Status != JobStatus.Blocked || job.AttemptCount >= job.MaxAttempts) return false;
        job.Status = JobStatus.Pending;
        job.AvailableAt = now;
        job.CompletedAt = null;
        job.LastErrorCode = null;
        job.LastErrorSummary = null;
        job.LockGroup ??= ExternalEffectReconcilePolicy.LockGroup(effect.Action);
        job.Version++;
        return true;
    }

    private async Task CloseUnresolvedAsync(ExternalEffectRecord effect, DateTimeOffset now, string summary, CancellationToken cancellationToken)
    {
        effect.CompletedAt = now;
        effect.Outcome = "UNRESOLVED";
        // A return action's decision must not stay "in progress" forever: it fails with the code the next claim
        // read can still upgrade to success, as when the verification job closes the record.
        if (effect.Action?.StartsWith("RETURN_", StringComparison.Ordinal) == true)
        {
            var decision = await db.ReturnDecisions
                .Where(x => x.TenantId == effect.TenantId && x.IdempotencyKey == effect.IdempotencyKey)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (decision is not null) { decision.Status = "FAILED"; decision.ErrorCode = "EXTERNAL_EFFECT_UNRESOLVED"; decision.CompletedAt = now; }
        }
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
