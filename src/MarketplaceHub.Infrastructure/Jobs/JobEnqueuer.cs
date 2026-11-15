using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Jobs;

// The job and whether this call added it (false: an existing job with the same key was returned; Job is then null).
public sealed record EnqueueResult(Guid JobId, bool Created, IntegrationJob? Job);

// KUYRUK-8: adds a new job to the caller's unit of work (it is saved with the caller's own changes, in the caller's
// transaction) after the request's dedup policy. Nothing is saved here.
public static class JobEnqueuer
{
    public static async Task<EnqueueResult> EnqueueAsync(AppDbContext db, IntegrationJobRequest request, JobDedupPolicy policy, CancellationToken cancellationToken)
    {
        if (policy == JobDedupPolicy.ReuseAnyWithKey)
        {
            var existing = await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == request.TenantId && x.JobType == request.JobType && x.JobDedupKey == request.DedupKey)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is { } id) return new EnqueueResult(id, false, null);
        }
        else if (policy == JobDedupPolicy.ReuseHeldElseRetire)
        {
            // Read tracked: a job that no longer holds its key gives it up in the caller's save, with the new job.
            var existing = await db.IntegrationJobs
                .SingleOrDefaultAsync(x => x.TenantId == request.TenantId && x.JobType == request.JobType && x.JobDedupKey == request.DedupKey, cancellationToken);
            if (existing is not null)
            {
                if (PriceInventoryOutboxPolicy.HoldsDedupKey(existing.Status)) return new EnqueueResult(existing.Id, false, null);
                existing.JobDedupKey = PriceInventoryOutboxPolicy.RetiredDedupKey(request.DedupKey, existing.Id);
                existing.Version++;
            }
        }
        var job = Add(db, request);
        return new EnqueueResult(job.Id, true, job);
    }

    // Whether a job of the type already has the key, in any status: the first check of a site that runs a check of its
    // own before it adds the job (CallerChecked), in that order.
    public static Task<bool> KeyTakenAsync(AppDbContext db, Guid tenantId, string jobType, string dedupKey, CancellationToken cancellationToken) =>
        db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobType == jobType && x.JobDedupKey == dedupKey, cancellationToken);

    // Adds the job without a lookup (JobDedupPolicy.CallerChecked).
    public static IntegrationJob Add(AppDbContext db, IntegrationJobRequest request)
    {
        var job = IntegrationJobFactory.New(request);
        db.IntegrationJobs.Add(job);
        return job;
    }
}
