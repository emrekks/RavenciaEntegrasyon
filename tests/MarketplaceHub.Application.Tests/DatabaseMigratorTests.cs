using MarketplaceHub.Infrastructure.Persistence;
using Npgsql;
using Xunit;

public class DatabaseMigratorTests
{
    [Fact]
    public void Migration_session_gets_lock_and_statement_timeouts_and_keeps_the_rest()
    {
        var connection = new NpgsqlConnectionStringBuilder(DatabaseMigrator.ForMigrations("Host=db;Password=secret;Maximum Pool Size=25;Options=-c jit=off"));

        Assert.Equal("-c jit=off -c lock_timeout=15000 -c statement_timeout=1200000", connection.Options);
        Assert.Equal("db", connection.Host);
        Assert.Equal("secret", connection.Password);
        Assert.Equal(25, connection.MaxPoolSize);
    }

    [Fact]
    public void Only_a_lock_timeout_is_retried()
    {
        Assert.True(DatabaseMigrator.IsLockTimeout(new PostgresException("canceling statement due to lock timeout", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable)));
        Assert.True(DatabaseMigrator.IsLockTimeout(new InvalidOperationException("wrapped", new PostgresException("lock timeout", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable))));
        Assert.False(DatabaseMigrator.IsLockTimeout(new PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled)));
        Assert.False(DatabaseMigrator.IsLockTimeout(new InvalidOperationException("other")));
    }
}
