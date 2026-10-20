using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Jobs;

// Write conflicts are counted on the job (IntegrationJob.ConflictCount) and any other outcome resets the count.
// - A version conflict (another job changed the same rows) is deferred without an attempt for the first few
//   consecutive times; after that it retries normally, so a job that conflicts every time spends its attempts
//   instead of looping for a day.
// - A duplicate insert (a webhook and a poll inserting the same row) is retried a couple of times: the next run
//   finds the row the other job inserted. Only a duplicate that keeps coming back is sent for review.
public static class JobConflictPolicy
{
    public const string ConcurrentUpdate = "CONCURRENT_UPDATE";
    public const string DuplicateInsert = "DATABASE_DUPLICATE_KEY";
    public const int DeferredVersionConflicts = 3;
    public const int DuplicateInsertRetries = 2;

    public static bool IsConflict(JobExecutionResult result) =>
        result.Kind == JobCompletionKind.Retry && result.ErrorCode is ConcurrentUpdate or DuplicateInsert;

    public static (JobExecutionResult Result, int ConflictCount) Apply(JobExecutionResult result, int previousConflicts)
    {
        if (!IsConflict(result)) return (result, 0);
        var conflicts = Math.Max(0, previousConflicts) + 1;
        if (result.ErrorCode == ConcurrentUpdate)
            return (conflicts <= DeferredVersionConflicts ? result : result with { RefundAttempt = false }, conflicts);
        return (conflicts <= DuplicateInsertRetries
            ? result
            : JobExecutionResult.ManualReview(DuplicateInsert, result.ErrorSummary), conflicts);
    }
}
