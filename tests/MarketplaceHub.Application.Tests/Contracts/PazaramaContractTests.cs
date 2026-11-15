using System.Net;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ADAPTOR-DIGER-10: Pazarama payloads (offline docs samples, personal fields faked) through the public mappers, compared
// with goldens under Fixtures/Pazarama/. Pazarama writes Istanbul local times without an offset and money as
// {currency: "TL", value}.
public sealed class PazaramaContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheDocumentedOrderMapsToOneOrderWithItsReturnedItem()
    {
        using var document = ContractFixture.Json("Pazarama/orders-docs-sample.json");

        var page = PazaramaOrderMapper.Page(document.RootElement, 100);

        ContractFixture.MatchesGolden("Pazarama/orders-docs-sample.golden.json", page);
        var order = Assert.Single(page.Items);
        Assert.Equal(("283534307", "TRY"), (order.ExternalOrderId, order.Currency));
        // "2026-02-17 17:23" Istanbul local is 14:23 UTC.
        Assert.Equal(new DateTimeOffset(2026, 2, 17, 14, 23, 0, TimeSpan.Zero), order.OrderedAt);
        Assert.Equal((7m, 0m, 7m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        var line = Assert.Single(order.Lines);
        Assert.Equal(("Returned", "212", 1m), (line.RawStatus, line.Sku, line.VatRate));
        Assert.Equal(("order:283534307", "PZ00250056371"), (order.Packages[0].ExternalPackageId, order.Packages[0].CargoTrackingNumber));
        Assert.False(page.HasMore);
    }

    [Fact]
    public void APartlyCancelledOrderCountsOnlyItsLiveItemAndBrokenOrdersAreRefused()
    {
        using var document = ContractFixture.Json("Pazarama/orders-partial-cancel-robustness.json");

        var page = PazaramaOrderMapper.Page(document.RootElement, 100);

        ContractFixture.MatchesGolden("Pazarama/orders-partial-cancel-robustness.golden.json", page);
        var partial = Assert.Single(page.Items, order => order.ExternalOrderId == "300000001");
        // The cancelled item is no part of the order value while another item is live (audit S21).
        Assert.Equal((300m, 15m, 285m), (partial.GrossAmount, partial.DiscountAmount, partial.NetAmount));
        var cancelled = Assert.Single(partial.Lines, line => line.RawStatus == "Cancelled");
        Assert.Equal(1m, cancelled.CancelledQuantity);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 9, 30, 0, TimeSpan.Zero), cancelled.CancelledAt);
        Assert.Equal("Picking", partial.Packages[0].RawStatus);
        // An unknown item status code stays unknown and holds the package for review.
        var unknown = Assert.Single(page.Items, order => order.ExternalOrderId == "300000002");
        Assert.Equal("Unknown99", unknown.Packages[0].RawStatus);
        Assert.Equal(2m, unknown.Lines[0].Quantity);
        Assert.DoesNotContain(page.Items, order => order.ExternalOrderId == "300000003");
        // D8: an order in currency "XYZ" is refused with an issue naming it (Pazarama sells in lira only), never imported
        // in XYZ nor stored as lira.
        Assert.DoesNotContain(page.Items, order => order.ExternalOrderId == "300000004");
        Assert.Contains(page.Issues!, issue => issue.Code == "ORDER_PACKAGE_INVALID" && issue.RereadOrderId == "300000004");
        Assert.Equal(3, page.Issues!.Count(issue => issue.Code == "ORDER_PACKAGE_INVALID"));
    }

    [Fact]
    public void TheDocumentedRefundListMapsTurkishMonthDatesAndKeepsAnUnknownStatus()
    {
        using var document = ContractFixture.Json("Pazarama/refunds-docs-sample.json");

        var page = PazaramaReturnMapper.Page(document.RootElement, 1, Now);

        ContractFixture.MatchesGolden("Pazarama/refunds-docs-sample.golden.json", page);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(("818589784", "ef2affaf-fdfc-4128-b5bc-3a4c1129e662"), (page.Items[0].ExternalOrderId, page.Items[0].ExternalClaimId));
        // "6 Ekim 2021 19:46" and "14:53" are Istanbul local, Turkish month text.
        Assert.Equal(new DateTimeOffset(2021, 10, 6, 16, 46, 0, TimeSpan.Zero), page.Items[0].RequestedAt);
        Assert.Equal(new DateTimeOffset(2021, 10, 6, 11, 53, 0, TimeSpan.Zero), page.Items[0].OrderCreatedAt);
        Assert.Equal("WaitingInAction", page.Items[0].RawStatus);
        // An undocumented refundStatus stays unknown; a refund without dates has none.
        Assert.Equal("Unknown_77", page.Items[1].RawStatus);
        Assert.Null(page.Items[1].RequestedAt);
    }

    [Fact]
    public void TheDocumentedFinanceAnswerMapsTheOrdersTransactions()
    {
        using var document = ContractFixture.Json("Pazarama/finance-docs-sample.json");

        var finance = PazaramaFinanceMapper.Finance(document.RootElement, 541235638);

        ContractFixture.MatchesGolden("Pazarama/finance-docs-sample.golden.json", finance);
    }

    [Fact]
    public void ThePriceStockDataIdAnswerSeparatesSucceededPendingAndFailedRows()
    {
        using var document = ContractFixture.Json("Pazarama/price-stock-dataid-docs-sample.json");

        var page = PazaramaBatchMapper.Page(document.RootElement);

        ContractFixture.MatchesGolden("Pazarama/price-stock-dataid-docs-sample.golden.json", page);
        Assert.Equal(5, page.Items.Count);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(PazaramaBatchMapper.BatchState.Succeeded, page.Items[0].State);
        Assert.Equal(PazaramaBatchMapper.BatchState.Failed, page.Items[3].State);
        // D9: the documented rows whose operationStatusText is "Onaya gönderildi" carry price.status 0 with "Ürün fiyat
        // onayına gönderildi" (pazarama-api-son.md ~905-935): they wait for Pazarama's approval, not done.
        Assert.Equal(PazaramaBatchMapper.BatchState.PendingApproval, page.Items[1].State);
        // DEVIATION: a row with no price or stock result and no status text reads as Failed (the text switch's default),
        // not as unknown/still processing.
        Assert.Equal(PazaramaBatchMapper.BatchState.Failed, page.Items[4].State);
    }

    [Fact]
    public void QuestionsMapFromTheListAndTheDetail()
    {
        using var document = ContractFixture.Json("Pazarama/questions-docs-sample.json");
        var root = document.RootElement;

        var page = PazaramaQuestionMapper.Page(root.GetProperty("list"), 1);
        var withoutText = PazaramaQuestionMapper.Page(root.GetProperty("listWithoutText"), 1);
        var detail = PazaramaQuestionMapper.Detail(root.GetProperty("detail").GetProperty("data"));

        ContractFixture.MatchesGolden("Pazarama/questions-docs-sample.golden.json", new { page, withoutText, detail });
        Assert.Equal(3, page.Items.Count);
        Assert.Equal("56643287-ad2c-40be-9088-08dc076bf0d7", page.Items[0].Id);
        Assert.Equal(new DateTimeOffset(2023, 12, 28, 6, 12, 16, 333, TimeSpan.Zero), page.Items[0].CreatedAt);
        // Status 2: an answer waiting for Pazarama's approval is not shown as answered.
        Assert.Equal(("WAITING_FOR_APPROVE", "Merchant"), (page.Items[0].Status, Assert.Single(page.Items[0].Conversations).Author));
        Assert.Equal("WAITING_FOR_ANSWER", page.Items[1].Status);
        // D7: a row without questionStatus stays unknown (CLAUDE.md: a missing field is never read as a state).
        Assert.Equal("UNKNOWN", page.Items[2].Status);
        // The GET list's rows carry no question text: none is made up, the rows are left out.
        Assert.Empty(withoutText.Items);
        Assert.Equal(("a82a1cf6-d113-4e73-0c48-08dde351ae8d", "567800436", "ORDER"), (detail!.Id, detail.OrderNumber, detail.Kind));
    }

    [Fact]
    public void ApprovedProductsMapAndAMissingProductStatusLeavesTheSaleStateUnknown()
    {
        using var document = ContractFixture.Json("Pazarama/products-approved-docs-sample.json");

        var page = PazaramaProductMapper.Page(document.RootElement, pending: false);

        ContractFixture.MatchesGolden("Pazarama/products-approved-docs-sample.golden.json", page);
    }

    public static TheoryData<string, CannedHttpCase> ErrorAnswers => new()
    {
        { "429 with Retry-After", new(() => CannedHttp.RateLimited(31), AdapterErrorClass.RateLimit, "PAZARAMA_RATE_LIMITED", 429, TimeSpan.FromSeconds(31)) },
        { "401 credentials (after one fresh token)", new(() => CannedHttp.Unauthorized(), AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIALS_REJECTED", 401, null) },
        { "403 HTML gateway page", new(CannedHttp.GatewayBlocked, AdapterErrorClass.RateLimit, "PAZARAMA_GATEWAY_BLOCKED", 403, TimeSpan.FromMinutes(15)) },
        { "403 JSON access denied", new(() => new CannedHttp(HttpStatusCode.Forbidden, """{"success":false,"message":"Erişim yetkiniz yok."}"""), AdapterErrorClass.Authentication, "PAZARAMA_ACCESS_DENIED", 403, TimeSpan.FromMinutes(15)) },
        { "400 validation", new(() => CannedHttp.Fixture(HttpStatusCode.BadRequest, "Pazarama/error-validation.json"), AdapterErrorClass.Validation, "PAZARAMA_REQUEST_REJECTED", 400, null) },
        { "500 server", new(() => new CannedHttp(HttpStatusCode.InternalServerError, "{}"), AdapterErrorClass.Remote5xx, "PAZARAMA_REMOTE_ERROR", 500, TimeSpan.FromSeconds(30)) }
    };

    [Theory]
    [MemberData(nameof(ErrorAnswers))]
    public async Task EachErrorAnswerIsClassifiedThroughTheClientsSendPath(string name, CannedHttpCase answer)
    {
        var http = answer.Http();
        var options = new PazaramaOptions();
        http.Prelude = request => request.RequestUri == options.TokenAddress
            ? CannedHttp.Json("""{"data":{"accessToken":"fake-access-token","expiresIn":3600},"success":true}""")
            : null;
        var client = new PazaramaHttpClient(http, null!, new PazaramaTokenCache(TimeProvider.System), Options.Create(options), TimeProvider.System, NullLogger<PazaramaHttpClient>.Instance, new ConfigurationBuilder().Build());

        var result = await client.TestAsync(Account(), CancellationToken.None);

        Assert.InRange(http.Requests, 1, 2);
        answer.AssertMatches(name, result.Error);
    }

    private static PazaramaRequestContext Account() => new(
        new PlatformConnection { PlatformCode = "PAZARAMA", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Pazarama", ExternalStoreId = "fake-seller", ApiVersion = "v1" },
        Guid.Parse("00000000-0000-4000-8000-000000000999"), "fake-client-id", "fake-client-secret");
}
