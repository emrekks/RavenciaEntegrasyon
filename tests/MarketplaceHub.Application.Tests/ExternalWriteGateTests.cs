using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// One table for the two write rules the adapters use (see ExternalWriteGate). The deliberate difference is written out:
// a VERIFIED production connection writes under the n11/Pazarama/PTT AVM rule and does not under the runtime rule; a
// manual write to an operational STAGE connection passes the runtime rule only.
public sealed class ExternalWriteGateTests
{
    private const string On = """{"ExternalWritesEnabled":true}""";
    private const string Off = """{"ExternalWritesEnabled":false}""";

    [Theory]
    //          environment   status      global  settings  manual  production  runtime
    [InlineData("PRODUCTION", "ACTIVE", true, On, false, true, true)]
    [InlineData("PRODUCTION", "ACTIVE", true, On, true, true, true)]
    [InlineData("PRODUCTION", "VERIFIED", true, On, false, true, false)]
    [InlineData("PRODUCTION", "VERIFIED", true, On, true, true, false)]
    [InlineData("PRODUCTION", "ACTIVE", false, On, false, false, false)]
    [InlineData("PRODUCTION", "ACTIVE", true, Off, false, false, false)]
    [InlineData("PRODUCTION", "ACTIVE", true, "bozuk", false, false, false)]
    [InlineData("PRODUCTION", "DRAFT", true, On, true, false, false)]
    [InlineData("STAGE", "VERIFIED", false, Off, true, false, true)]
    [InlineData("STAGE", "VERIFIED", true, On, false, false, false)]
    public void BothRulesAnswerAsTheAdaptersDid(string environment, string status, bool global, string settings, bool manual, bool production, bool runtime)
    {
        var connection = new PlatformConnection { Id = Guid.NewGuid(), Environment = environment, Status = status, SettingsJson = settings, PlatformCode = "X", DisplayName = "X", ExternalStoreId = "1", ApiVersion = "V1" };
        var context = new AdapterContext(Guid.NewGuid(), connection.Id, "c", "k", DateTimeOffset.UtcNow.AddMinutes(1), Operation: manual ? IntegrationOperation.Manual : IntegrationOperation.Automatic);

        Assert.Equal(production, ExternalWriteGate.AllowsProductionWrite(connection, global));
        Assert.Equal(runtime, ExternalWriteGate.AllowsRuntimeWrite(connection, context, global));
    }

    [Fact]
    public void TheSwitchesAreReadStrictly()
    {
        Assert.True(ExternalWriteGate.ConnectionWritesEnabled(On));
        Assert.False(ExternalWriteGate.ConnectionWritesEnabled("""{"ExternalWritesEnabled":"true"}"""));
        Assert.False(ExternalWriteGate.ConnectionWritesEnabled("{}"));
        Assert.False(ExternalWriteGate.GlobalWritesEnabled(new ConfigurationBuilder().Build()));
        Assert.True(ExternalWriteGate.GlobalWritesEnabled(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ExternalWriteGate.GlobalFlag] = "true" }).Build()));
    }
}
