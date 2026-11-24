using System.Net;
using System.Xml.Linq;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ADAPTOR-DIGER-10: PttAVM SOAP answers through the client's own send-and-parse path and the public mappers, compared
// with goldens under Fixtures/PttAvm/. PttAVM writes Istanbul local times without an offset.
public sealed class PttAvmContractTests
{
    [Fact]
    public async Task TheOrderListMapsReturnedCancelledPartlyShippedAndNilFieldOrders()
    {
        var http = new CannedHttp(HttpStatusCode.OK, ContractFixture.Read("PttAvm/SiparisKontrolListesiV2.xml"), "text/xml");

        var result = await Client(http).ReadOrdersAsync(Account(), new DateTime(2024, 10, 1), new DateTime(2024, 11, 5), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        var page = result.Value!;
        var returns = page.Items.Select(order => new { order.ExternalOrderId, claim = PttAvmHttpClient.ReturnClaim(order) }).ToList();
        ContractFixture.MatchesGolden("PttAvm/SiparisKontrolListesiV2.golden.json", new { page, returns });
        Assert.Equal(["PTT-TEST00001-071024", "PTT-TEST00003-211024", "PTT-OPEN12345-211026", "PTT-NILFIELDS1-051126"], page.Items.Select(order => order.ExternalOrderId).ToArray());
        var first = page.Items[0];
        // 17:38:29 Istanbul local is 14:38:29 UTC.
        Assert.Equal(new DateTimeOffset(2024, 10, 7, 14, 38, 29, TimeSpan.Zero), first.OrderedAt);
        Assert.Equal("TRY", first.Currency);
        Assert.Equal(["Returned", "Cancelled"], first.Lines.Select(line => line.RawStatus).ToArray());
        Assert.Equal("Cancelled", Assert.Single(page.Items[1].Packages).RawStatus);
        var partial = page.Items[2];
        Assert.Equal((2m, 1m), (partial.Lines[0].Quantity, partial.Lines[1].Quantity));
        // KdvDahilToplamTutar 190 after a 10 discount for two units: the unit price is before discount, 100.
        Assert.Equal(100m, partial.Lines[0].UnitPrice);
        var nil = page.Items[3];
        // A line with nil discount and VAT fields and a decimal-comma amount is not guessed: it is left out as
        // unreadable (counted on the package, reported, order read again); the undocumented status stays unknown.
        Assert.Empty(nil.Lines);
        Assert.Equal(1, nil.Packages[0].UnreadableLineCount);
        Assert.Equal("Unknown:bilinmeyen_yeni_durum", nil.Packages[0].RawStatus);
        Assert.Contains(page.Issues!, issue => issue.Code == "ORDER_PACKAGE_LINE_INVALID");
        Assert.NotNull(returns[0].claim);
    }

    [Fact]
    public void ThePriceStockAnswersGiveAnAsyncTrackingIdOrARefusal()
    {
        var answers = ContractFixture.Xml("PttAvm/UpdateProductsStockPriceResult.xml").Elements().ToList();
        var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        var results = answers.Select(answer => PttAvmHttpClient.StockPriceAnswer(answer, at, null)).ToList();

        ContractFixture.MatchesGolden("PttAvm/UpdateProductsStockPriceResult.golden.json", results);
        Assert.Equal("t:671a178183f9c51388bbb169", results[0].Value!.ExternalOperationId);
        Assert.Equal("PTTAVM_PRICE_STOCK_REJECTED", results[1].Error!.Code);
        Assert.Equal(PttAvmProductPayloadRules.RepeatRequestWaitCode, results[2].Error!.Code);
    }

    [Fact]
    public void TheTrackingResultGivesOneLinePerBarcodeWithPartialSuccess()
    {
        var status = PttAvmPriceInventoryRules.Tracking("t:671a178183f9c51388bbb169", ContractFixture.Xml("PttAvm/GetProductsTrackingResultResult.xml"));

        ContractFixture.MatchesGolden("PttAvm/GetProductsTrackingResultResult.golden.json", status);
        Assert.NotNull(status);
        Assert.Equal("COMPLETED", status.Status);
        Assert.Equal(4, status.Lines.Count);
        Assert.False(status.Lines.Single(line => line.ExternalKey == "tshirt-1071").Succeeded);
        Assert.True(status.Lines.Single(line => line.ExternalKey == "tshirt-1076").Succeeded);
    }

    public static TheoryData<string, CannedHttpCase> ErrorAnswers => new()
    {
        { "429 with Retry-After", new(() => CannedHttp.RateLimited(37, ""), AdapterErrorClass.RateLimit, "PTTAVM_RATE_LIMITED", 429, TimeSpan.FromSeconds(37)) },
        { "500 security fault", new(() => new CannedHttp(HttpStatusCode.InternalServerError, ContractFixture.Read("PttAvm/fault-security.xml"), "text/xml"), AdapterErrorClass.Authentication, "PTTAVM_CREDENTIALS_REJECTED", 500, null) },
        { "403 HTML gateway page", new(CannedHttp.GatewayBlocked, AdapterErrorClass.RateLimit, "PTTAVM_GATEWAY_BLOCKED", 403, TimeSpan.FromMinutes(15)) },
        { "500 validation fault", new(() => new CannedHttp(HttpStatusCode.InternalServerError, ContractFixture.Read("PttAvm/fault-validation.xml"), "text/xml"), AdapterErrorClass.Validation, "PTTAVM_REQUEST_REJECTED", 500, null) },
        { "500 fault without a cause on a read", new(() => new CannedHttp(HttpStatusCode.InternalServerError, ContractFixture.Read("PttAvm/fault-unexplained.xml"), "text/xml"), AdapterErrorClass.Remote5xx, "PTTAVM_REMOTE_FAULT", 500, TimeSpan.FromSeconds(60)) },
        { "503 without a body", new(() => new CannedHttp(HttpStatusCode.ServiceUnavailable, ""), AdapterErrorClass.Remote5xx, "PTTAVM_REMOTE_ERROR", 503, TimeSpan.FromSeconds(30)) }
    };

    [Theory]
    [MemberData(nameof(ErrorAnswers))]
    public async Task EachErrorAnswerIsClassifiedThroughTheClientsSendPath(string name, CannedHttpCase answer)
    {
        var http = answer.Http();

        var result = await Client(http).ReadOrdersAsync(Account(), new DateTime(2026, 11, 1), new DateTime(2026, 11, 5), CancellationToken.None);

        Assert.Equal(1, http.Requests);
        answer.AssertMatches(name, result.Error);
    }

    private static PttAvmHttpClient Client(CannedHttp http) =>
        new(http, null!, Options.Create(new PttAvmOptions()), TimeProvider.System, NullLogger<PttAvmHttpClient>.Instance, new ConfigurationBuilder().Build());

    private static PttAvmRequestContext Account() => new(
        new PlatformConnection { Id = Guid.Parse("00000000-0000-4000-8000-000000000a01"), PlatformCode = "PTTAVM", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "PttAVM", ExternalStoreId = "900001", ApiVersion = "v1" },
        "fake-user", "fake-password");
}
