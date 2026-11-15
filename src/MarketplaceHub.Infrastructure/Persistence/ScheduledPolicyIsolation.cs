using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// KUYRUK-10 (16 Nov 2026): one sync policy of a scheduler pass. A policy that threw used to end the whole pass, so no
// connection of any tenant got its jobs until the fault went away. Now its failure is reported (onFailure), the jobs it
// had added to the pass's unit of work are taken out again, and the pass goes on with the next policy (null: failed).
// A cancellation and a lost or timed-out database connection still end the pass: every other policy would fail the same
// way, and the scheduler loop logs and retries the pass as before.
internal static class ScheduledPolicyIsolation
{
    public static async Task<int?> RunAsync(DbContext db, Func<Task<int>> schedule, Action<Exception> onFailure)
    {
        var addedBefore = AddedEntities(db).ToHashSet(ReferenceEqualityComparer.Instance);
        try
        {
            return await schedule();
        }
        catch (Exception exception) when (Isolated(exception))
        {
            foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added && !addedBefore.Contains(entry.Entity)).ToList())
                entry.State = EntityState.Detached;
            onFailure(exception);
            return null;
        }
    }

    private static IEnumerable<object> AddedEntities(DbContext db) =>
        db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity);

    // A failure of this policy alone: not a cancellation and not the database connection itself.
    internal static bool Isolated(Exception exception) => exception is not OperationCanceledException && !ConnectionFault(exception);

    // The database could not be reached or stopped answering (an error the server itself reported is the policy's own).
    internal static bool ConnectionFault(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException) return false;
            if (current is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException or IOException) return true;
        }
        return false;
    }
}
