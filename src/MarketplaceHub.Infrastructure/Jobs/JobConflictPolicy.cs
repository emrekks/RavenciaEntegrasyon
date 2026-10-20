using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Jobs;

// Write conflicts are counted on the job (IntegrationJob.ConflictCount) and any other outcome resets the count.
// The two kinds keep separate runs in the one column: a positive count is a run of version conflicts, a negative
// one a run of duplicate inserts, and a conflict of the other kind starts its own run from one. So a duplicate
// insert after a few version conflicts still gets its retries instead of going straight to review.
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
        if (result.ErrorCode == ConcurrentUpdate)
        {
            var versionConflicts = Math.Max(0, previousConflicts) + 1;
            return (versionConflicts <= DeferredVersionConflicts ? result : result with { RefundAttempt = false }, versionConflicts);
        }
        var duplicates = Math.Max(0, -previousConflicts) + 1;
        return (duplicates <= DuplicateInsertRetries
            ? result
            : JobExecutionResult.ManualReview(DuplicateInsert, result.ErrorSummary), -duplicates);
    }
}
