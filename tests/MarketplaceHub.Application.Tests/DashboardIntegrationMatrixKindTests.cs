using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

public class DashboardIntegrationMatrixKindTests
{
    [Theory]
    [InlineData("products")]
    [InlineData("stock")]
    [InlineData("price")]
    public void Kinds_that_run_on_demand_have_no_age_rule(string key) =>
        Assert.Null(DashboardIntegrationMatrix.Kinds.Single(kind => kind.Key == key).StaleAfter);

    [Theory]
    [InlineData("orders")]
    [InlineData("returns")]
    [InlineData("invoices")]
    public void Scheduled_reads_still_turn_late(string key) =>
        Assert.NotNull(DashboardIntegrationMatrix.Kinds.Single(kind => kind.Key == key).StaleAfter);
}
