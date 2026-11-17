using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ADAPTOR-DIGER-10: n11 payloads (REST JSON and SOAP XML) through the public mappers, compared with goldens under
// Fixtures/N11/. n11's epoch-ms dates are documented as GMT+3 but read as plain UTC (TurkeyTime.cs, owner's choice).
public sealed class N11ContractTests
{
    [Fact]
    public void TheDocumentedShipmentPackageMapsToOneOrder()
    {
        var page = N11OrderMapper.Packages(ContractFixture.Read("N11/shipmentPackages-docs-sample.json"));

        ContractFixture.MatchesGolden("N11/shipmentPackages-docs-sample.golden.json", page);
        var order = Assert.Single(page.Items);
        Assert.Equal(("203872347637", "TRY"), (order.ExternalOrderId, order.Currency));
        Assert.Equal((1335.6m, 5.8m, 1329.8m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1724274054460), order.OrderedAt);
        Assert.Equal("Delivered", Assert.Single(order.Packages).RawStatus);
        Assert.Equal([2m, 3m], order.Lines.Select(line => line.Quantity).ToArray());
    }

    [Fact]
    public void ACancelledLineTextNumbersAndBrokenPackagesAreMappedWithoutGuessing()
    {
        var page = N11OrderMapper.Packages(ContractFixture.Read("N11/shipmentPackages-partial-cancel-robustness.json"));

        ContractFixture.MatchesGolden("N11/shipmentPackages-partial-cancel-robustness.golden.json", page);
        var partial = Assert.Single(page.Items, order => order.ExternalOrderId == "205400000001");
        Assert.Equal(["Picking", "Cancelled"], partial.Lines.Select(line => line.RawStatus).ToArray());
        Assert.Equal(2m, partial.Lines[0].Quantity);
        // n11's own discount (totalMallDiscountPrice) stays in the line snapshot for the reports; the line does not carry it.
        Assert.Null(partial.Lines[0].PlatformDiscount);
        var textual = Assert.Single(page.Items, order => order.ExternalOrderId == "205400000002");
        Assert.Equal("SomeFuturePackageStatus", Assert.Single(textual.Packages).RawStatus);
        // No order-date entry: refused, never dated by lastModifiedDate.
        Assert.DoesNotContain(page.Items, order => order.ExternalOrderId == "205400000003");
        Assert.Contains(page.Issues!, issue => issue.Identity.Contains("112999000000003", StringComparison.Ordinal) || issue.RereadOrderId == "205400000003");
        Assert.True(page.HasMore);
    }

    [Fact]
    public void ReturnClaimsMapStatesAndLeaveAnEmptyQuantityUnknown()
    {
        var page = N11ReturnMapper.Page(ContractFixture.Xml("N11/ClaimReturnList.xml"), 0);

        ContractFixture.MatchesGolden("N11/ClaimReturnList.golden.json", page);
        Assert.Equal(["98765", "98766", "98767"], page.Items.Select(claim => claim.ExternalClaimId).ToArray());
        Assert.Equal(2m, Assert.Single(page.Items[0].Lines).Quantity);
        Assert.Equal("YK123", page.Items[0].CargoTrackingNumber);
        // An empty quantity is carried as 0 (unknown): never 1; the claim sync reports it against the order line.
        Assert.Equal(0m, Assert.Single(page.Items[1].Lines).Quantity);
        // An undocumented state is carried as sent.
        Assert.Equal("SOME_FUTURE_STATE", page.Items[2].RawStatus);
        Assert.Equal("RETURN_CLAIM_INVALID", Assert.Single(page.Issues!).Code);
        Assert.True(page.HasMore);
        Assert.Equal("1", page.NextCursor);
    }

    [Fact]
    public void ProductQueryItemsMapAndAMissingSaleStatusStaysUnknown()
    {
        using var document = ContractFixture.Json("N11/product-query-docs-sample.json");

        var page = N11ProductMapper.Page(document.RootElement);

        ContractFixture.MatchesGolden("N11/product-query-docs-sample.golden.json", page);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("main:GrupKoduModellemeİçin", page.Items[0].ExternalProductId);
        var withoutSaleStatus = Assert.Single(page.Items[1].Variants);
        Assert.Null(withoutSaleStatus.SaleState);
    }

    [Fact]
    public void PriceStockTasksReportTheAsyncIdAndPartialSuccess()
    {
        using var document = ContractFixture.Json("N11/tasks.json");
        var root = document.RootElement;
        var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        var submitted = new
        {
            queued = N11TaskMapper.Submitted(root.GetProperty("submittedQueued"), at),
            rejected = N11TaskMapper.Submitted(root.GetProperty("submittedRejected"), at)
        };
        var statuses = new[] { "detailPartly", "detailQueued", "detailUnknown" }
            .ToDictionary(name => name, name => N11TaskMapper.Status(root.GetProperty(name), name));

        ContractFixture.MatchesGolden("N11/tasks.golden.json", new { submitted, statuses });
        Assert.Equal("1092", submitted.queued!.Reference!.ExternalOperationId);
        Assert.Equal("N11_TASK_REJECTED", submitted.rejected!.Error!.Code);
        Assert.Equal("COMPLETED", statuses["detailPartly"].Status);
        Assert.Equal([true, false], statuses["detailPartly"].Lines.Select(line => line.Succeeded).ToArray());
        Assert.Equal("IN_PROGRESS", statuses["detailQueued"].Status);
    }

    [Fact]
    public void SettlementDaysAndItemsMapTheirAmountsAndDays()
    {
        var days = N11SettlementMapper.Days(ContractFixture.Xml("N11/GetSettlementList.xml"), 0);
        var items = N11SettlementMapper.Items(ContractFixture.Xml("N11/GetSettlementDetail.xml"), 0);

        ContractFixture.MatchesGolden("N11/settlements.golden.json", new { days, items });
        Assert.Equal(2, days.Items.Count);
        Assert.Equal(new DateOnly(2026, 11, 5), days.Items[0].SettlementDate);
        Assert.Equal(new DateOnly(2026, 10, 29), days.Items[1].SettlementDate);
        Assert.Equal((1200.50m, 1000.40m), (items.Items[0].PaymentAmount, items.Items[0].SettlementAmount));
        // D6: an item that leaves paymentAmount out is skipped as unreadable (named in Skipped), as the
        // Trendyol and Hepsiburada finance mappers do, instead of being stored with a made-up 0.
        Assert.Single(items.Items);
        Assert.Contains("tutarı", Assert.Single(items.Skipped!).Description);
    }

    [Fact]
    public void ProductQuestionsMapOpenAndAnsweredQuestions()
    {
        var details = ContractFixture.Xml("N11/productQuestions.xml").Elements().ToList();

        var questions = details.Select((detail, index) => N11QuestionMapper.Question($"Q{index + 1}", null, detail)).ToList();

        ContractFixture.MatchesGolden("N11/productQuestions.golden.json", questions);
        Assert.Equal("WAITING_FOR_ANSWER", questions[0]!.Status);
        Assert.Equal("ANSWERED", questions[1]!.Status);
        Assert.Equal("Göğüs 100 cm.", Assert.Single(questions[1]!.Conversations).Text);
        // D5: a question detail without any status (and without an answer) stays unknown. One that the list returned for
        // the OPEN filter is open by that answer, not by a guess.
        Assert.Equal("UNKNOWN", questions[2]!.Status);
        Assert.Equal("WAITING_FOR_ANSWER", N11QuestionMapper.Question("Q3", null, details[2], listedStatus: "OPEN")!.Status);
    }

    public static TheoryData<string, CannedHttpCase> ErrorAnswers => new()
    {
        { "429 with Retry-After", new(() => CannedHttp.RateLimited(23), AdapterErrorClass.RateLimit, "N11_RATE_LIMITED", 429, TimeSpan.FromSeconds(23)) },
        { "403 HTML gateway page", new(CannedHttp.GatewayBlocked, AdapterErrorClass.RateLimit, "N11_GATEWAY_BLOCKED", 403, TimeSpan.FromMinutes(15)) },
        { "400 validation", new(() => CannedHttp.Fixture(HttpStatusCode.BadRequest, "N11/error-validation.json"), AdapterErrorClass.Validation, "N11_REQUEST_REJECTED", 400, null) },
        { "500 server", new(() => new CannedHttp(HttpStatusCode.InternalServerError, "{}"), AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR", 500, TimeSpan.FromSeconds(30)) }
    };

    [Theory]
    [MemberData(nameof(ErrorAnswers))]
    public async Task EachErrorAnswerIsClassifiedThroughTheClientsSendPath(string name, CannedHttpCase answer)
    {
        var http = answer.Http();

        var result = await Client(http).TestAsync(Account(), CancellationToken.None);

        Assert.Equal(1, http.Requests);
        answer.AssertMatches(name, result.Error);
    }

    // A REST 401 is followed by one read-only SOAP call that tells a wrong key pair from REST access not enabled.
    [Fact]
    public async Task ARejectedKeyIsAnAuthenticationFailure()
    {
        var http = CannedHttp.Unauthorized();

        var result = await Client(http).TestAsync(Account(), CancellationToken.None);

        Assert.Equal(AdapterErrorClass.Authentication, result.Error?.Class);
        Assert.StartsWith("N11_", result.Error!.Code, StringComparison.Ordinal);
        Assert.Equal(401, result.Error.HttpStatus);
    }

    private static N11HttpClient Client(CannedHttp http) =>
        new(http, null!, Options.Create(new N11Options()), TimeProvider.System, NullLogger<N11HttpClient>.Instance, new ConfigurationBuilder().Build());

    private static N11RequestContext Account() => new(
        new PlatformConnection { PlatformCode = "N11", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "N11", ExternalStoreId = "fakeMagaza", ApiVersion = "v1" },
        "fake-app-key", "fake-app-secret");
}
