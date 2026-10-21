using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// Runs the one-shot `migrate` command. The previous API and Worker keep serving while it runs, so a schema change
// that needs an exclusive table lock must not queue behind their transactions for long: every write to that table
// would wait behind the queued ALTER. The migration session therefore gets its own lock_timeout (a lock it cannot get
// in time fails the statement; the whole run is retried a few times) and statement_timeout (a runaway statement ends
// before fast-deploy.sh's migration wait does). Only this context's connections carry the settings; the application's
// pools are untouched.
public static class DatabaseMigrator
{
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan StatementTimeout = TimeSpan.FromMinutes(20);
    internal const int LockAttempts = 4;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(20);

    internal static string ForMigrations(string connection)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var settings = FormattableString.Invariant($"-c lock_timeout={(long)LockTimeout.TotalMilliseconds} -c statement_timeout={(long)StatementTimeout.TotalMilliseconds}");
        builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? settings : $"{builder.Options} {settings}";
        return builder.ConnectionString;
    }

    internal static bool IsLockTimeout(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }
        || exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable };

    public static async Task MigrateAsync(AppDbContext applicationContext, ILogger logger, CancellationToken cancellationToken)
    {
        var connection = applicationContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The database connection string is not configured.");
        // The client-side timeout is a little longer, so PostgreSQL's statement_timeout error is what gets reported.
        var commandTimeoutSeconds = (int)(StatementTimeout + TimeSpan.FromMinutes(1)).TotalSeconds;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ForMigrations(connection), npgsql => npgsql.CommandTimeout(commandTimeoutSeconds))
            .Options;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var migrationContext = new AppDbContext(options);
                await migrationContext.Database.MigrateAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (IsLockTimeout(exception) && attempt < LockAttempts)
            {
                logger.LogWarning(exception, "Migration attempt {Attempt} of {Attempts} could not take a table lock within {LockTimeout}; retrying in {Delay}", attempt, LockAttempts, LockTimeout, LockRetryDelay);
                await Task.Delay(LockRetryDelay, cancellationToken);
            }
        }
    }
}
