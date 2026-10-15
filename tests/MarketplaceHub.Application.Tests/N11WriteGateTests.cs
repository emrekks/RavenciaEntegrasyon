using System.Net;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11WriteGateTests
{
    private static N11HttpClient Client(bool globalWrites) => new(
        new NoHttpFactory(), null!, Options.Create(new N11Options()), TimeProvider.System, NullLogger<N11HttpClient>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = globalWrites ? "true" : "false" }).Build());

    private static N11RequestContext Account(string environment, string status, bool connectionWrites) => new(
        new PlatformConnection
        {
            PlatformCode = "N11", Environment = environment, Status = status, DisplayName = "N11", ExternalStoreId = "Ravencia", ApiVersion = "V1",
            SettingsJson = connectionWrites ? """{"ExternalWritesEnabled":true}""" : """{"ExternalWritesEnabled":false}"""
        },
        "key", "secret");

    [Theory]
    [InlineData("PRODUCTION", "VERIFIED")]
    [InlineData("PRODUCTION", "ACTIVE")]
    public void WritesPassOnlyWhenEverySwitchIsOn(string environment, string status) =>
        Assert.Null(Client(true).WriteBlocked<bool>(Account(environment, status, true), "test"));

    [Theory]
    [InlineData(false, "PRODUCTION", "VERIFIED", true)]
    [InlineData(true, "PRODUCTION", "VERIFIED", false)]
    [InlineData(true, "STAGE", "VERIFIED", true)]
    [InlineData(true, "PRODUCTION", "DRAFT", true)]
    [InlineData(true, "PRODUCTION", "DISABLED", true)]
    public void AnyClosedSwitchBlocksTheWrite(bool global, string environment, string status, bool connectionWrites)
    {
        var blocked = Client(global).WriteBlocked<bool>(Account(environment, status, connectionWrites), "soru cevabı");

        Assert.NotNull(blocked);
        Assert.False(blocked.IsSuccess);
        Assert.Equal("N11_EXTERNAL_WRITES_DISABLED", blocked.Error!.Code);
        Assert.Contains("soru cevabı gönderilmedi", blocked.Error.SafeMessage);
    }

    [Fact]
    public void AMissingSettingsBlockIsTreatedAsClosed() =>
        Assert.NotNull(Client(true).WriteBlocked<bool>(new(new PlatformConnection { PlatformCode = "N11", Environment = "PRODUCTION", Status = "ACTIVE", SettingsJson = "{}", DisplayName = "N11", ExternalStoreId = "x", ApiVersion = "V1" }, "k", "s"), "test"));

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("The write gate must stop before any request.");
    }
}
