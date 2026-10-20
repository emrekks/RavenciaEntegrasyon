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
}
