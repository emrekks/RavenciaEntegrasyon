using MarketplaceHub.Infrastructure;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// R9: a connection removed while its job waited answers as an adapter failure, not as an exception.
[Collection("postgres-integration")]
public sealed class MarketplacePortRouterPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task AMissingConnectionIsAnAdapterFailure()
    {
        await using var db = fixture.CreateContext();
        // No adapter is reached for a connection that does not exist.
        var router = new MarketplacePortRouter(db, null!, null!, null!, null!, null!);
        var context = new AdapterContext(Guid.NewGuid(), Guid.NewGuid(), "router-missing-connection", "router-missing-connection", DateTimeOffset.UtcNow.AddMinutes(1));

        var order = await router.GetAsync(context, "ORDER-1", CancellationToken.None);
        var packages = await router.PollPackagesAsync(context, new PackagePollWindow(null, null), new AdapterPageRequest(null, 10), CancellationToken.None);

        Assert.False(order.IsSuccess);
        Assert.Equal(MarketplacePortRouter.ConnectionMissing, order.Error);
        Assert.Equal(MarketplacePortRouter.ConnectionMissing, packages.Error);
    }
}
