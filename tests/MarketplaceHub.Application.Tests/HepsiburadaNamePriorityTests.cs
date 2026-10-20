using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaNamePriorityTests
{
    [Fact]
    public void TheFirstListedNameWinsWhateverOrderTheJsonUses()
    {
        using var json = JsonDocument.Parse("""{"id":"internal-17","packageNumber":"PKG-17"}""");

        Assert.Equal("PKG-17", HepsiburadaJsonMapper.PackageIdentity(json.RootElement));
    }

    [Fact]
    public void ADeliveredRowIsDatedByItsDeliveryNotItsShipment()
    {
        using var json = JsonDocument.Parse("""{"orderNumber":"HB-1","packageNumber":"PKG-1","ShippedDate":"2026-10-01T10:00:00Z","DeliveredDate":"2026-10-03T15:00:00Z"}""");

        var package = HepsiburadaJsonMapper.OrderStatusPackage(json.RootElement, "delivered");

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero), package.Package.OccurredAt);
    }

    [Fact]
    public void AnEmptyEventDateFallsThroughToTheNextOne()
    {
        using var json = JsonDocument.Parse("""{"orderNumber":"HB-1","packageNumber":"PKG-1","DeliveredDate":null,"lastStatusUpdateDate":"2026-10-03T15:00:00Z"}""");

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero), HepsiburadaJsonMapper.OrderStatusPackage(json.RootElement, "delivered").Package.OccurredAt);
    }

    [Fact]
    public void APackageItemNamingTheLineByAnotherKeyIsStillAllocated()
    {
        using var json = JsonDocument.Parse("""
        {
          "orderNumber": "HB-ALIAS-1",
          "orderDate": "2026-09-28T12:15:00Z",
          "items": [{ "id": "line-A", "lineItemId": "item-B", "merchantSku": "SKU-1", "name": "Ürün", "quantity": 2, "price": 10.0, "status": "Open" }],
          "packages": [{ "packageNumber": "PKG-A", "status": "Open", "items": [{ "lineItemId": "item-B", "quantity": 2 }] }]
        }
        """);

        var order = HepsiburadaJsonMapper.Order(json.RootElement, "HB-ALIAS-1");

        var allocation = Assert.Single(Assert.Single(order.Packages).Allocations);
        Assert.Equal("line-A", allocation.ExternalLineId);
        Assert.Equal("line-A", Assert.Single(order.Lines).ExternalLineId);
    }

    [Fact]
    public async Task ThePackagePollReadsTrackingForMovingPackagesAndForFinishedOnesWithoutACarrier()
    {
        var handler = new Routes();
        var client = new HepsiburadaHttpClient(new Factory(handler), null!, Options.Create(new HepsiburadaOptions { AuthenticationMode = "BASIC" }), TimeProvider.System, new ConfigurationBuilder().Build(), NullLogger<HepsiburadaHttpClient>.Instance);
        var account = new HepsiburadaRequestContext(
            new PlatformConnection { PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "fixture", ExternalStoreId = "m-1", Status = "ACTIVE", ApiVersion = "V1.0" },
            new Uri("https://oms.example/"), new Uri("https://listing.example/"), "integrator", "fixture-key")
        { IntegratorName = "ravencia_tests/1.0" };
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        var result = await client.PollPackagesAsync(account, new PackagePollWindow(now.AddHours(-2), now), new("0", 10), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(2, result.Value!.Items.Count);
        // G13: a package first seen delivered has no carrier yet, so its tracking read still runs once.
        var tracking = handler.Paths.Where(path => path.Contains("/packagenumber/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, tracking.Length);
        Assert.EndsWith("/packagenumber/PKG-DONE", tracking[0]);
        Assert.EndsWith("/packagenumber/PKG-OPEN", tracking[1]);
        Assert.Equal("InTransit", result.Value.Items.Single(item => item.Package.ExternalPackageId == "PKG-OPEN").Package.RawStatus);
        var done = result.Value.Items.Single(item => item.Package.ExternalPackageId == "PKG-DONE").Package;
        Assert.Equal("Delivered", done.RawStatus);
        Assert.Equal("Aras", done.CargoProviderExternalId);
    }

    [Theory]
    [InlineData("Delivered", "Aras", "T-1", true)]
    [InlineData("Delivered", null, "T-1", false)]
    [InlineData("Undelivered", "Aras", null, false)]
    [InlineData("Shipped", "Aras", "T-1", false)]
    public void TheTrackingReadIsSkippedOnlyForAFinishedPackageWithCarrierAndTrackingKnown(string rawStatus, string? provider, string? tracking, bool skipped) =>
        Assert.Equal(skipped, HepsiburadaHttpClient.SkipsTrackingRead(new("HB-1", new RemotePackage("PKG", null, rawStatus, DateTimeOffset.UnixEpoch, provider, tracking, []))));

    [Fact]
    public void ThePackageListsKeepTheLowerCaseOffsetProvenLive()
    {
        var window = new PackagePollWindow(null, null);
        Assert.Equal("offset=20&limit=10", HepsiburadaHttpClient.PackageListQuery(20, 10, window));
    }

    [Theory]
    [InlineData("Delivered", true)]
    [InlineData("Undelivered", true)]
    [InlineData("Cancelled", true)]
    [InlineData("Returned", true)]
    [InlineData("Shipped", false)]
    [InlineData("Open", false)]
    [InlineData("SomethingNew", false)]
    public void TerminalListStatuses(string rawStatus, bool terminal) =>
        Assert.Equal(terminal, HepsiburadaHttpClient.HasTerminalListStatus(new("HB-1", new RemotePackage("PKG", null, rawStatus, DateTimeOffset.UnixEpoch, null, null, []))));

    private sealed class Routes : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add(path);
            var body = path switch
            {
                _ when path.EndsWith("/packagenumber/PKG-DONE", StringComparison.Ordinal) => """[{"packageNumber":"PKG-DONE","status":"Delivered","cargoCompany":"Aras","trackingInfoCode":"T-2"}]""",
                _ when path.Contains("/packagenumber/", StringComparison.Ordinal) => """[{"packageNumber":"PKG-OPEN","status":"InTransit","cargoCompany":"HepsiJet","trackingInfoCode":"T-1"}]""",
                _ when path.EndsWith("/delivered", StringComparison.Ordinal) => """{"items":[{"orderNumber":"HB-DONE","packageNumber":"PKG-DONE","DeliveredDate":"2026-10-20T10:00:00"}],"totalCount":1}""",
                _ when path.EndsWith("/shipped", StringComparison.Ordinal) || path.EndsWith("/undelivered", StringComparison.Ordinal) => """{"items":[],"totalCount":0}""",
                _ => """{"items":[{"orderNumber":"HB-OPEN","packageNumber":"PKG-OPEN","status":"Open","orderDate":"2026-10-20T09:00:00Z","lineItems":[{"lineItemId":"line-1","merchantSku":"SKU-1","name":"Ürün","quantity":1}]}],"totalCount":1}"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
