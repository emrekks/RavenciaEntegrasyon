using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// K6 (audit 2026-10-24): a manual stock write runs in a SERIALIZABLE transaction and reads the stock row the worker
// changes for orders and reservations. PostgreSQL then aborts one side with 40001 (serialization_failure) or 40P01
// (deadlock), and a write of the row in between can also surface as a version conflict. Such an attempt wrote nothing:
// it is run again on fresh values a few times, and when it keeps colliding the caller answers with its own 409
// instead of a 500.
internal static class SerializableWriteRetry
{
    public const int MaxAttempts = 3;

    public static bool IsRetryable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException) return true;
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }) return true;
        }
        return false;
    }

    // attempt opens, commits and disposes its own transaction. What a failed attempt loaded or added is forgotten before
    // the next one; what the request tracked before (the idempotency record) is kept.
    public static async Task<T> RunAsync<T>(AppDbContext db, Func<CancellationToken, Task<T>> attempt, Func<T> exhausted, CancellationToken cancellationToken)
    {
        for (var number = 1; ; number++)
        {
            var trackedBefore = new HashSet<object>(db.ChangeTracker.Entries().Select(entry => entry.Entity), ReferenceEqualityComparer.Instance);
            try
            {
                return await attempt(cancellationToken);
            }
            catch (Exception exception) when (IsRetryable(exception))
            {
                foreach (var entry in db.ChangeTracker.Entries().Where(entry => !trackedBefore.Contains(entry.Entity)).ToList())
                    entry.State = EntityState.Detached;
                if (number >= MaxAttempts) return exhausted();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * number), cancellationToken);
            }
        }
    }
}
