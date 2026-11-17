using System.Net;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ADAPTOR-TY-HB-9: Trendyol payloads (offline docs samples and hand-built ones on the documented schema) through the
// public mappers, compared with goldens under Fixtures/Trendyol/. Times: orderDate is a GMT+3 wall-clock epoch
// (TurkeyTime.FromTurkeyWallClockEpochMilliseconds, 3 h earlier in UTC); every other epoch is plain UTC.
public sealed class TrendyolContractTests
{
    [Fact]
    public void TheDocumentedShipmentPackageMapsToOneOrder()
    {
        var page = TrendyolJsonMapper.Orders(ContractFixture.Read("Trendyol/shipmentPackages-docs-sample.json"));

        ContractFixture.MatchesGolden("Trendyol/shipmentPackages-docs-sample.golden.json", page);
        var order = Assert.Single(page.Items);
        Assert.Equal("10654411111", order.ExternalOrderId);
        Assert.Equal("TRY", order.Currency);
        // orderDate 1762253333685 is 2025-11-04T10:48:53.685 "GMT+3": 07:48:53.685 UTC.
        Assert.Equal(new DateTimeOffset(2025, 11, 4, 7, 48, 53, 685, TimeSpan.Zero), order.OrderedAt);
        // lastModifiedDate is a plain UTC epoch.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1762865408581), order.LastModifiedAt);
        Assert.Equal((498.90m, 0m, 498.90m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        var package = Assert.Single(order.Packages);
        Assert.Equal("3330111111", package.ExternalPackageId);
        Assert.Equal("Delivered", package.RawStatus);
        Assert.Equal("NotInvoiced", package.Invoice?.RawStatus);
        var line = Assert.Single(order.Lines);
        Assert.Equal(("4765111111", 1m, 498.90m, 20m), (line.ExternalLineId, line.Quantity, line.UnitPrice, line.VatRate));
        Assert.Equal(0m, line.PlatformDiscount);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void APartlyCancelledOrderKeepsTheCancelledPackageAndTheDiscountedMultiQuantityLine()
    {
        var page = TrendyolJsonMapper.Orders(ContractFixture.Read("Trendyol/shipmentPackagesStream-partial-cancel.json"));
        var merged = TrendyolJsonMapper.MergeOrderPackages(page.Items, "10700000001");

        ContractFixture.MatchesGolden("Trendyol/shipmentPackagesStream-partial-cancel.golden.json", new { page, merged });
        Assert.Equal("cursor-fake-0002", page.NextCursor);
        Assert.True(page.HasMore);
        Assert.NotNull(merged);
        var cancelled = Assert.Single(merged.Packages, package => package.ExternalPackageId == "3330200001");
        Assert.Equal("Cancelled", cancelled.RawStatus);
        // The cancellation time is the Cancelled history step (plain UTC), not the order date.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1762311600000), cancelled.CancelledAt);
        var active = Assert.Single(merged.Packages, package => package.ExternalPackageId == "3330200002");
        Assert.Equal("3330200001", active.OriginExternalPackageId);
        Assert.Equal("cancel", active.CreatedBy);
        Assert.Equal((600m, 90m, 510m), (active.GrossAmount, active.DiscountAmount, active.NetAmount));
        var multi = Assert.Single(merged.Lines, line => line.ExternalLineId == "4765200001");
        Assert.Equal((3m, 170m, 10m), (multi.Quantity, multi.UnitPrice, multi.PlatformDiscount));
    }

    [Fact]
    public void AMicroExportPackageIsMappedWithoutAStorefrontOfItsOwn()
    {
        var page = TrendyolJsonMapper.Orders(ContractFixture.Read("Trendyol/shipmentPackages-micro-export-az.json"));

        ContractFixture.MatchesGolden("Trendyol/shipmentPackages-micro-export-az.golden.json", page);
        var order = Assert.Single(page.Items);
        // The storefront is stamped by the client from the storefront the page was read under (TrendyolHttpClient);
        // the mapper never derives it from the Istanbul depot address.
        Assert.Null(order.StoreFrontCode);
        Assert.Equal(0m, Assert.Single(order.Lines).VatRate);
        Assert.Equal(350m, order.NetAmount);
    }

    [Fact]
    public void BrokenPackagesAreRefusedWithIssuesAndNeverDatedOrPricedByGuess()
    {
        var page = TrendyolJsonMapper.Orders(ContractFixture.Read("Trendyol/shipmentPackages-robustness.json"));

        ContractFixture.MatchesGolden("Trendyol/shipmentPackages-robustness.golden.json", page);
        var order = Assert.Single(page.Items);
        Assert.Equal("10900000001", order.ExternalOrderId);
        // Numbers given as text are read as numbers. D12: lastModifiedDate given as text ("1762304400000") is the package's
        // change time (plain UTC epoch), not the order date.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1762304400000), order.LastModifiedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1762304400000), Assert.Single(order.Packages).OccurredAt);
        var textual = Assert.Single(order.Lines, line => line.ExternalLineId == "4765900001");
        Assert.Equal((2m, 100.25m, 20m), (textual.Quantity, textual.UnitPrice, textual.VatRate));
        // An unknown status is carried as sent, not translated.
        Assert.Equal("SomeFutureStatus", Assert.Single(order.Packages).RawStatus);
        Assert.Equal("SomeFutureLineStatus", textual.RawStatus);
        // No invoice field sent: the invoice state stays unknown (null), never "not invoiced".
        Assert.Null(order.Packages[0].Invoice?.RawStatus);
        Assert.False(order.Packages[0].Invoice?.ReferencesAreAuthoritative);
        // The line without vatRate takes the package's only rate and says so.
        Assert.Contains(page.Issues!, issue => issue.Code == "ORDER_PACKAGE_LINE_VAT_RATE_INFERRED");
        // Missing orderDate, out-of-range orderDate, missing currency and missing id: four refusals, no order.
        Assert.Equal(4, page.Issues!.Count(issue => issue.Code != "ORDER_PACKAGE_LINE_VAT_RATE_INFERRED"));
        Assert.Equal("p:1", page.NextCursor);
    }

    [Fact]
    public void TheDocumentedClaimPageMapsLinesDeadlinesAndRejectedPackageState()
    {
        var page = TrendyolJsonMapper.Returns(ContractFixture.Read("Trendyol/getClaims-docs-sample.json"));

        ContractFixture.MatchesGolden("Trendyol/getClaims-docs-sample.golden.json", page);
        Assert.Equal(2, page.Items.Count);
        var documented = page.Items[0];
        Assert.Equal(("f9da2317-876b-4b86-b8f7-0535c3b65731", "65745805"), (documented.ExternalClaimId, documented.ExternalOrderId));
        Assert.Equal(2, documented.Lines.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1525844162827), documented.RequestedAt);
        var waiting = page.Items[1];
        // No deadline field from Trendyol: the 48-hour rule is applied and marked as an estimate.
        Assert.True(waiting.ActionDueAtEstimated);
        // Two claim items of one order line are two return lines of one unit each.
        Assert.Equal(2, waiting.Lines.Count(line => line.Quantity == 1m && line.ExternalOrderLineId == "4765900101"));
    }

    [Fact]
    public void AClaimBuiltOrderOfAnUndeliveredPackageIsNotDelivered()
    {
        using var document = ContractFixture.Json("Trendyol/getClaims-docs-sample.json");
        var claim = document.RootElement.GetProperty("content")[1].GetRawText();

        var order = TrendyolJsonMapper.OrderFromReturnClaim(claim);

        ContractFixture.MatchesGolden("Trendyol/getClaims-undelivered-order.golden.json", order);
        Assert.NotNull(order);
        Assert.Equal("UnDelivered", Assert.Single(order.Packages).RawStatus);
        // getClaims has no currency field; TRY is the documented TR-core reading. The storefront stays unknown.
        Assert.Equal("TRY", order.Currency);
        Assert.Null(order.StoreFrontCode);
    }

    [Fact]
    public void ApprovedProductsMapEveryVariantAndItsPublicationState()
    {
        var json = ContractFixture.Read("Trendyol/products-approved.json");

        var products = TrendyolJsonMapper.Products(json);
        var catalog = TrendyolJsonMapper.CatalogProducts(json);
        var states = new[] { "0000000000051", "0000000000052", "0000000000053", "0000000000099" }
            .Select(barcode => new { barcode, status = TrendyolJsonMapper.ApprovedPublicationStatus(json, barcode) })
            .ToList();

        ContractFixture.MatchesGolden("Trendyol/products-approved.golden.json", new { products, catalog, states });
        Assert.Equal(3, products.Items.Count);
        Assert.Equal("p:1", products.NextCursor);
        Assert.Equal(150, products.TotalCount);
        Assert.Equal(["APPROVED", "LOCKED", "ARCHIVED"], states.Take(3).Select(state => state.status!.Status).ToArray());
        Assert.Null(states[3].status);
    }

    [Fact]
    public void UnapprovedProductsKeepTheirApprovalStateAndAMissingStatusStaysUnknown()
    {
        var json = ContractFixture.Read("Trendyol/products-unapproved.json");

        var catalog = TrendyolJsonMapper.PendingApprovalCatalogProducts(json);
        var states = new[] { "BARCODE-ANON-002", "BARCODE-ANON-003", "BARCODE-ANON-004" }
            .Select(barcode => TrendyolJsonMapper.UnapprovedPublicationStatus(json, barcode))
            .ToList();

        ContractFixture.MatchesGolden("Trendyol/products-unapproved.golden.json", new { catalog, states, counts = TrendyolJsonMapper.UnapprovedStatusCounts(json) });
        Assert.Equal(["REJECTED", "PENDING_APPROVAL", "(unknown)"], catalog.Items.Select(item => item.ApprovalStatus ?? "(unknown)").ToArray());
        Assert.Equal(["REJECTED", "PENDING_APPROVAL", "UNKNOWN"], states.Select(state => state!.Status).ToArray());
    }

    [Fact]
    public void ThePriceInventoryBatchIsCompleteOnlyWhenEveryAnnouncedItemAnswered()
    {
        var documented = TrendyolJsonMapper.Batch(ContractFixture.Read("Trendyol/batch-price-inventory-docs-sample.json"), "requested-id");
        var partial = TrendyolJsonMapper.Batch(ContractFixture.Read("Trendyol/batch-price-inventory-partial.json"), "requested-id");

        ContractFixture.MatchesGolden("Trendyol/batch-price-inventory.golden.json", new { documented, partial });
        Assert.Equal(("c57e3453-2c00-11f0-aa3a-be9298facace-1746718627", "COMPLETED"), (documented.ExternalOperationId, documented.Status));
        Assert.Equal("IN_PROGRESS", partial.Status);
        Assert.Equal(3, partial.ReportedItemCount);
        Assert.Equal([true, false], partial.Lines.Select(line => line.Succeeded).ToArray());
        Assert.Equal(2, partial.Lines[1].Reasons!.Count);
    }

    [Fact]
    public void QuestionsMapTheirConversationAndStatus()
    {
        var page = TrendyolQuestionMapper.Page(ContractFixture.Read("Trendyol/questions-docs-sample.json"), "PRODUCT");

        ContractFixture.MatchesGolden("Trendyol/questions-docs-sample.golden.json", page);
        Assert.Equal(5, page.Items.Count);
        Assert.Equal(["WAITING_FOR_ANSWER", "ANSWERED", "REJECTED"], page.Items.Take(3).Select(item => item.Status).ToArray());
        // An unknown status is carried as sent.
        Assert.Equal("SOME_FUTURE_STATUS", page.Items[4].Status);
        // D1: a question without any status stays unknown (CLAUDE.md: a missing field is never read as a state).
        Assert.Equal("UNKNOWN", page.Items[3].Status);
    }

    [Fact]
    public void SettlementRecordsReadTextNumbersAndSkipRecordsWithoutDateOrAmount()
    {
        var page = TrendyolFinanceMapper.Page(ContractFixture.Read("Trendyol/settlements-sale-docs-sample.json"), "settlements", "Sale", null);

        ContractFixture.MatchesGolden("Trendyol/settlements-sale-docs-sample.golden.json", page);
        Assert.Equal(["725041340", "725041335", "725099001"], page.Items.Select(item => item.ExternalId).ToArray());
        Assert.Equal((0m, 120.50m), (page.Items[2].Debt, page.Items[2].Credit));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1613397671561), page.Items[0].TransactionDate);
        Assert.Equal(2, page.Skipped!.Count);
    }

    public static TheoryData<string, CannedHttpCase> ErrorAnswers => new()
    {
        { "429 with Retry-After", new(() => CannedHttp.RateLimited(42), AdapterErrorClass.RateLimit, "REMOTE_RATE_LIMITED", 429, TimeSpan.FromSeconds(42)) },
        { "401 credentials", new(() => CannedHttp.Unauthorized(), AdapterErrorClass.Authentication, "REMOTE_AUTHENTICATION_FAILED", 401, null) },
        { "403 HTML gateway page", new(CannedHttp.GatewayBlocked, AdapterErrorClass.RateLimit, "TRENDYOL_GATEWAY_BLOCKED", 403, TimeSpan.FromMinutes(15)) },
        { "400 validation", new(() => CannedHttp.Fixture(HttpStatusCode.BadRequest, "Trendyol/error-validation-invalid-barcode.json"), AdapterErrorClass.Validation, "REMOTE_REQUEST_REJECTED", 400, null) },
        { "400 cargo change cooldown", new(() => CannedHttp.Fixture(HttpStatusCode.BadRequest, "Trendyol/error-cargo-change-cooldown.json"), AdapterErrorClass.Validation, "CARGO_PROVIDER_CHANGE_COOLDOWN", 400, TimeSpan.FromMinutes(5)) },
        { "503 server", new(() => new CannedHttp(HttpStatusCode.ServiceUnavailable, "{}"), AdapterErrorClass.Remote5xx, "REMOTE_SERVER_ERROR", 503, null) }
    };

    [Theory]
    [MemberData(nameof(ErrorAnswers))]
    public async Task EachErrorAnswerIsClassifiedThroughTheClientsSendPath(string name, CannedHttpCase answer)
    {
        var http = answer.Http();
        var client = new TrendyolHttpClient(http, null!, new ConfigurationBuilder().Build(), Options.Create(new TrendyolOptions()), TimeProvider.System, NullLogger<TrendyolHttpClient>.Instance);
        var end = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        var scan = await client.ScanPackageStreamAsync(Account(), "3330000001", null, null, end.AddDays(-7), end, "TR", CancellationToken.None);

        Assert.Equal(1, http.Requests);
        answer.AssertMatches(name, scan.StreamError);
    }

    [Fact]
    public async Task TheStreamPageFixtureIsFoundThroughTheClient()
    {
        var http = CannedHttp.Fixture(HttpStatusCode.OK, "Trendyol/shipmentPackagesStream-partial-cancel.json");
        var client = new TrendyolHttpClient(http, null!, new ConfigurationBuilder().Build(), Options.Create(new TrendyolOptions()), TimeProvider.System, NullLogger<TrendyolHttpClient>.Instance);
        var end = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        var scan = await client.ScanPackageStreamAsync(Account(), "3330200002", null, null, end.AddDays(-7), end, "TR", CancellationToken.None);

        Assert.Null(scan.StreamError);
        var package = scan.Result!.Value!;
        Assert.Equal(("10700000001", "Created"), (package.ExternalOrderId, package.Package.RawStatus));
        Assert.Equal("7280000000000002", package.Package.CargoTrackingNumber);
    }

    private static TrendyolRequestContext Account() => new(
        new PlatformConnection { PlatformCode = "TRENDYOL", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Trendyol", ExternalStoreId = "99999999", ApiVersion = "V2" },
        new Uri("https://apigw.example.test/"), "fake-key", "fake-secret", "99999999 - SelfIntegration", false);
}

// An error answer and the classification every adapter's send path must give it.
public sealed record CannedHttpCase(Func<CannedHttp> HttpFactory, AdapterErrorClass Class, string Code, int? HttpStatus, TimeSpan? RetryAfter)
{
    internal CannedHttp Http() => HttpFactory();

    public void AssertMatches(string name, AdapterError? error)
    {
        Assert.True(error is not null, $"{name}: no error was returned");
        Assert.Equal((Class, Code, HttpStatus, RetryAfter), (error.Class, error.Code, error.HttpStatus, error.RetryAfter));
    }

    public override string ToString() => Code;
}
