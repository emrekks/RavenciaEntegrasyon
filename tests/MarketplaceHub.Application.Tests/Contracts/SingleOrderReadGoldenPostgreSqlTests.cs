using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-12 (16 Nov 2026): the single-order reads of Trendyol and Hepsiburada (an order sync whose payload names one
// order, with or without its package), pinned before their read branches move onto the shared ReadSingleOrderAsync where
// the logic is the same: what is asked (the hint, the package fallback, the package status), what is stored, the result.
[Collection("postgres-integration")]
public sealed class SingleOrderReadGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    public static TheoryData<string, string> Scenarios => new()
    {
        { "HEPSIBURADA", "found" }, { "HEPSIBURADA", "found-with-package" }, { "HEPSIBURADA", "package-refused" }, { "HEPSIBURADA", "refused" },
        { "TRENDYOL", "found" }, { "TRENDYOL", "not-found-package-fallback" }, { "TRENDYOL", "refused" }
    };

    [PostgreSqlTheory]
    [MemberData(nameof(Scenarios))]
    public async Task ASingleOrderReadAsksAndStoresTheSameThings(string platform, string scenario)
    {
        var run = new GoldenRun(fixture, platform);
        await run.SeedAsync();
        RemoteOrder remote;
        if (platform == "HEPSIBURADA")
        {
            using var document = ContractFixture.Json("Hepsiburada/orders-paid-list.json");
            var (items, _) = HepsiburadaJsonMapper.OrderPage(document.RootElement);
            remote = items.Select(item => HepsiburadaJsonMapper.PaidOrderLine(item, out _)).First(order => order is not null)!;
        }
        else remote = TrendyolJsonMapper.Orders(ContractFixture.Read("Trendyol/shipmentPackages-docs-sample.json")).Items[0];

        var port = new GoldenMarketplace();
        var refused = AdapterResult<RemoteOrder>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503));
        var notFound = AdapterResult<RemoteOrder>.Failure(GoldenMarketplace.Error(AdapterErrorClass.NotFound, "REMOTE_ORDER_NOT_FOUND", 404));
        port.Answers["orders.Get"] = _ => scenario == "refused" ? refused : AdapterResult<RemoteOrder>.Success(remote);
        port.Answers["orders.GetHinted"] = _ => scenario switch { "refused" => refused, "not-found-package-fallback" => notFound, _ => AdapterResult<RemoteOrder>.Success(remote) };
        port.Answers["packages.Tracking"] = _ => scenario == "package-refused"
            ? AdapterResult<PackageTrackingStatusSnapshot>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503))
            : AdapterResult<PackageTrackingStatusSnapshot>.Success(new("PKG-1", "Delivered", "HepsiJet", "TRK1", remote.OrderNumber));
        var withPackage = scenario is "found-with-package" or "package-refused" or "not-found-package-fallback";
        var payload = JsonSerializer.Serialize(new { connectionId = run.Connection.Id, externalOrderId = remote.ExternalOrderId, packageNumber = withPackage ? "PKG-1" : null });
        var jobType = MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.OrderSync);
        await run.RunAsync("single", jobType, port, payload, run.NewId());
        await run.MatchesAsync($"SingleOrderReads/{platform.ToLowerInvariant()}-{scenario}.golden.json");
    }
}
