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
        exception is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected }
        || exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected };

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
                // Model Kaydı Faz 2: the fields of models the editor has not saved yet, by the editor's own rule (C#, so not
                // a migration). Idempotent; a failure leaves the backfill's values and does not stop the deploy.
                try
                {
                    var recomputed = await ProductModelLinks.RecomputeUnsavedFieldsAsync(migrationContext, cancellationToken);
                    logger.LogInformation("Product model fields recomputed for {Count} models", recomputed);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Product model fields were not recomputed");
                }
                try
                {
                    var attributes = await ProductModelLinks.RecomputeUnsavedAttributesAsync(migrationContext, cancellationToken);
                    logger.LogInformation("Product model attributes recomputed for {Count} models", attributes);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Product model attributes were not recomputed");
                }
                // KATALOG-14 phase B backfill: every model takes its cards' shared values (the desi and the size chart
                // included) and the models whose cards disagree are reported. Same rule as every save's write-through;
                // idempotent, one short transaction per model.
                try
                {
                    var (models, written, differing) = await ProductModelValues.RefreshAllAsync(migrationContext, DateTimeOffset.UtcNow, cancellationToken);
                    logger.LogInformation("Product model values refreshed: {Models} models, {Written} written, {Differing} with cards that disagree", models, written, differing);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Product model values were not refreshed");
                }
                // SIPARIS-STOK-9: the Returns page's card columns of claims written before they existed (or by the previous
                // code while this deploy runs). The rule is C#, so not a migration; idempotent and batched. A failure only
                // keeps the page on its old computation until the worker's repair pass; it does not stop the deploy.
                try
                {
                    var (claims, written, lost) = await ReturnCardColumns.RepairAsync(migrationContext, cancellationToken);
                    logger.LogInformation("Return card columns: {Claims} claims read, {Written} written, {Lost} changed meanwhile", claims, written, lost);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Return card columns were not filled");
                }
                // SIPARIS-STOK-9: the orders' kept customer names (the Returns search), filled at deploy instead of at the
                // worker's first maintenance pass. Batched and idempotent; a failure leaves them to that pass.
                try
                {
                    var filled = await OrderCustomerSearchNames.BackfillAsync(migrationContext, cancellationToken);
                    logger.LogInformation("Order customer search names: {Filled} filled", filled);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Order customer search names were not filled");
                }
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
