using MarketplaceHub.Infrastructure;
using Npgsql;
using Xunit;

public class DatabaseConnectionOptionsTests
{
    [Fact]
    public void Connection_turns_jit_off_and_keeps_the_rest()
    {
        var connection = new NpgsqlConnectionStringBuilder(DependencyInjection.WithoutJit("Host=db;Database=hub;Username=app;Password=secret;Maximum Pool Size=25"));

        Assert.Equal("-c jit=off", connection.Options);
        Assert.Equal("db", connection.Host);
        Assert.Equal(25, connection.MaxPoolSize);
    }

    [Fact]
    public void Existing_options_are_kept_and_a_chosen_jit_setting_wins()
    {
        Assert.Equal("-c statement_timeout=0 -c jit=off", new NpgsqlConnectionStringBuilder(DependencyInjection.WithoutJit("Host=db;Options=-c statement_timeout=0")).Options);
        Assert.Equal("-c jit=on", new NpgsqlConnectionStringBuilder(DependencyInjection.WithoutJit("Host=db;Options=-c jit=on")).Options);
    }

    [Fact]
    public void Configured_pool_size_replaces_the_shared_files_value()
    {
        var connection = new NpgsqlConnectionStringBuilder(DependencyInjection.WithMaxPoolSize("Host=db;Password=secret;Maximum Pool Size=25", "45"));

        Assert.Equal(45, connection.MaxPoolSize);
        Assert.Equal("db", connection.Host);
        Assert.Equal("secret", connection.Password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void Missing_or_invalid_pool_size_keeps_the_connection_unchanged(string? maxPoolSize)
    {
        const string connection = "Host=db;Maximum Pool Size=25";

        Assert.Equal(connection, DependencyInjection.WithMaxPoolSize(connection, maxPoolSize));
    }

    [Fact]
    public void Shared_rate_limiter_gets_a_small_pool_of_its_own()
    {
        var connection = new NpgsqlConnectionStringBuilder(DependencyInjection.ForSharedRateLimiter("Host=db;Minimum Pool Size=10;Maximum Pool Size=45"));

        Assert.Equal(DependencyInjection.SharedRateLimiterMaxPoolSize, connection.MaxPoolSize);
        Assert.True(connection.MinPoolSize <= connection.MaxPoolSize);
        Assert.Equal("db", connection.Host);
    }

    [Fact]
    public void Worker_pool_covers_every_default_lane_slot()
    {
        var totalSlots = MarketplaceHub.Infrastructure.Jobs.WorkerLanes.All().Sum(lane => lane.DefaultSlots);

        Assert.True(MarketplaceHub.Infrastructure.Jobs.WorkerLanes.RequiredPoolSize(totalSlots) <= 45);
    }
}
