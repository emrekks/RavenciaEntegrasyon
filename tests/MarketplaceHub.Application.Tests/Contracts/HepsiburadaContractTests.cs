using System.Net;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ADAPTOR-TY-HB-9: Hepsiburada payloads through the public mappers, compared with goldens under Fixtures/Hepsiburada/.
// Hepsiburada writes Istanbul local times: a time without an offset is UTC+3 (TurkeyTime), one with Z/+03:00 is taken
// as written.
public sealed class HepsiburadaContractTests
{
    [Fact]
    public void PaidOrderLinesMapOneOrderEachAndRefuseLinesWithoutOrderNumberOrDate()
    {
        using var document = ContractFixture.Json("Hepsiburada/orders-paid-list.json");
        var (items, total) = HepsiburadaJsonMapper.OrderPage(document.RootElement);
        var mapped = items.Select(item => new { order = HepsiburadaJsonMapper.PaidOrderLine(item, out var refusal), refusal }).ToList();

        ContractFixture.MatchesGolden("Hepsiburada/orders-paid-list.golden.json", new { total, mapped });
        Assert.Equal(5, total);
        var first = mapped[0].order!;
        // 14:30 Istanbul local is 11:30 UTC; the due date likewise.
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 11, 30, 0, TimeSpan.Zero), first.OrderedAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 20, 59, 0, TimeSpan.Zero), first.ShipmentDueAt);
        Assert.Equal(("TRY", 3m), (first.Currency, Assert.Single(first.Lines).Quantity));
        Assert.Equal((300m, 30m, 270m), (first.GrossAmount, first.DiscountAmount, first.NetAmount));
        // A time with its own offset is taken as written; quantity and amounts sent as text are read as numbers.
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 6, 0, 0, TimeSpan.Zero), mapped[2].order?.OrderedAt);
        Assert.Equal((2m, 39.95m, 79.90m), (mapped[2].order!.Lines[0].Quantity, mapped[2].order!.Lines[0].UnitPrice, mapped[2].order!.GrossAmount));
        // An unknown status is carried as sent.
        Assert.Equal("SomeFutureStatus", mapped[2].order!.LifecycleStatus);
        // D4: a status this panel does not know says nothing about a cancellation: UNKNOWN, not NOT_CANCELLED. A known
        // open status is not cancelled.
        Assert.Equal("UNKNOWN", mapped[2].order!.CancellationStatus);
        Assert.Equal("NOT_CANCELLED", first.CancellationStatus);
        Assert.Equal("UNKNOWN", first.PaymentStatus);
        // DEVIATION: a paid-order line without a status field gets LifecycleStatus "Open" (HepsiburadaJsonMapper.PaidOrderLine,
        // `rawStatus ?? "Open"`), while the order-detail mapper keeps a missing status unknown (null).
        Assert.Equal("Open", mapped[1].order?.LifecycleStatus);
        Assert.Null(mapped[3].order);
        Assert.Equal("ORDER_PACKAGE_INVALID", mapped[3].refusal?.Code);
        Assert.Null(mapped[4].order);
        Assert.NotNull(mapped[4].refusal);
    }

    [Fact]
    public void APartlyCancelledOrderDetailKeepsTheCancelledLineAndThePackedMultiQuantityLine()
    {
        using var document = ContractFixture.Json("Hepsiburada/order-detail-partial-cancel.json");

        var order = HepsiburadaJsonMapper.Order(document.RootElement, "4000000101");

        ContractFixture.MatchesGolden("Hepsiburada/order-detail-partial-cancel.golden.json", order);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 19, 15, 0, TimeSpan.Zero), order.OrderedAt);
        Assert.Equal(("Paid", "Open"), (order.PaymentStatus, order.LifecycleStatus));
        Assert.Equal(2, order.Lines.Count);
        var cancelled = Assert.Single(order.Lines, line => line.RawStatus == "CancelledByCustomer");
        Assert.Equal(1m, cancelled.Quantity);
        var packed = Assert.Single(order.Lines, line => line.RawStatus == "InTransit");
        Assert.Equal(2m, packed.Quantity);
        var package = Assert.Single(order.Packages);
        Assert.Equal(("5000000101", "InTransit"), (package.ExternalPackageId, package.RawStatus));
        Assert.Equal(("INVOICED", "FAKE2026000000101"), (package.Invoice?.RawStatus, package.Invoice?.InvoiceNumber));
    }

    [Fact]
    public void PackageRowsMapToTheirOrderAndARowWithoutOrderDateIsRefused()
    {
        using var document = ContractFixture.Json("Hepsiburada/packages-list.json");
        var (items, _) = HepsiburadaJsonMapper.PackagePage(document.RootElement);

        var mapped = items.Select(item => ContractFixture.Outcome(() => HepsiburadaJsonMapper.OrderPackage(item))).ToList();

        ContractFixture.MatchesGolden("Hepsiburada/packages-list.golden.json", mapped);
        var first = Assert.IsType<RemoteOrderPackage>(mapped[0]);
        Assert.Equal(("4000000201", "5000000201", "Packaged"), (first.ExternalOrderId, first.Package.ExternalPackageId, first.Package.RawStatus));
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), first.OrderSnapshot?.OrderedAt);
        Assert.Equal(4m, Assert.Single(first.Package.Allocations).AllocatedQuantity);
        Assert.IsNotType<RemoteOrderPackage>(mapped[1]);
    }

    [Fact]
    public void TheDocumentedAwaitingActionClaimMapsToAReturnClaim()
    {
        using var document = ContractFixture.Json("Hepsiburada/claim-awaitingaction-docs-sample.json");

        var claim = HepsiburadaJsonMapper.ReturnClaim(document.RootElement);
        var order = HepsiburadaJsonMapper.OrderFromReturnClaim(ContractFixture.Read("Hepsiburada/claim-awaitingaction-docs-sample.json"));

        ContractFixture.MatchesGolden("Hepsiburada/claim-awaitingaction-docs-sample.golden.json", new { claim, order });
        Assert.Equal(("356189794", "2152988946", "AwaitingAction"), (claim.ExternalClaimId, claim.ExternalOrderId, claim.RawStatus));
        var line = Assert.Single(claim.Lines);
        Assert.Equal(("61680dc6-b404-55fe-1171-d92506060606", 1m), (line.ExternalOrderLineId, line.Quantity));
        Assert.Equal(new DateTimeOffset(2021, 10, 14, 11, 3, 17, 676, TimeSpan.Zero), claim.RequestedAt ?? claim.LastModifiedAt);
    }

    [Fact]
    public void InventoryUploadsReportTheAsyncIdAndPartialSuccess()
    {
        using var document = ContractFixture.Json("Hepsiburada/inventory-uploads.json");
        var root = document.RootElement;

        var uploadId = HepsiburadaJsonMapper.InventoryUploadId(root.GetProperty("accepted"));
        var statuses = new[] { "processing", "completedPartly", "failedWhole", "unknownStatus" }
            .ToDictionary(name => name, name => ContractFixture.Outcome(() => HepsiburadaJsonMapper.InventoryUploadStatus(root.GetProperty(name), uploadId)));

        ContractFixture.MatchesGolden("Hepsiburada/inventory-uploads.golden.json", new { uploadId, statuses });
        Assert.Equal("71", uploadId);
        Assert.Equal("IN_PROGRESS", ((RemoteOperationStatus)statuses["processing"]!).Status);
        var partly = (RemoteOperationStatus)statuses["completedPartly"]!;
        Assert.Equal(("COMPLETED", "LISTING_INVENTORY:71"), (partly.Status, partly.ExternalOperationId));
        Assert.Equal(("SKU-FAKE-HB2", false), (Assert.Single(partly.Lines).ExternalKey, partly.Lines[0].Succeeded));
        Assert.Equal("REJECTED", ((RemoteOperationStatus)statuses["failedWhole"]!).Status);
    }

    [Fact]
    public void FinanceRecordsConvertNumericCurrenciesAndSkipRecordsWithoutAmountOrDay()
    {
        using var document = ContractFixture.Json("Hepsiburada/finance-transactions.json");

        var page = HepsiburadaFinanceMapper.Page(document.RootElement);

        ContractFixture.MatchesGolden("Hepsiburada/finance-transactions.golden.json", page);
        Assert.Equal(["HBFIN-FAKE-0001", "HBFIN-FAKE-0002"], page.Items.Select(item => item.Id).ToArray());
        Assert.Equal(("TRY", 280.0m), (page.Items[0].Currency, page.Items[0].Amount));
        // 10:00 Istanbul local is 07:00 UTC; a time with Z is taken as written.
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 7, 0, 0, TimeSpan.Zero), page.Items[0].InvoiceDate);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), page.Items[1].DueDate);
        Assert.Equal(2, page.Skipped!.Count);
    }

    [Fact]
    public void QuestionsMapStatusesAndConversations()
    {
        using var document = ContractFixture.Json("Hepsiburada/questions-list.json");

        var page = HepsiburadaQuestionMapper.Page(document, new("PRODUCT", null, null, null, 1, 25));

        ContractFixture.MatchesGolden("Hepsiburada/questions-list.golden.json", page);
        Assert.Equal(4, page.Items.Count);
        Assert.Equal(["WAITING_FOR_ANSWER", "ANSWERED", "EXPIRED"], page.Items.Take(3).Select(item => item.Status).ToArray());
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 7, 0, 0, TimeSpan.Zero), page.Items[0].CreatedAt);
        // D2: a question without a status field stays unknown (CLAUDE.md: a missing field is never read as a state).
        Assert.Equal("UNKNOWN", page.Items[3].Status);
    }

    public static TheoryData<string, CannedHttpCase> ErrorAnswers => new()
    {
        { "429 with Retry-After", new(() => CannedHttp.RateLimited(17), AdapterErrorClass.RateLimit, "HEPSIBURADA_RATE_LIMITED", 429, TimeSpan.FromSeconds(17)) },
        { "401 credentials", new(() => CannedHttp.Unauthorized(), AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIALS_REJECTED", 401, null) },
        { "403 HTML gateway page", new(CannedHttp.GatewayBlocked, AdapterErrorClass.RateLimit, "HEPSIBURADA_GATEWAY_BLOCKED", 403, TimeSpan.FromMinutes(15)) },
        { "403 JSON access", new(() => new CannedHttp(HttpStatusCode.Forbidden, """{"message":"Forbidden"}"""), AdapterErrorClass.Authentication, "HEPSIBURADA_ACCESS_FORBIDDEN", 403, null) },
        { "400 validation", new(() => CannedHttp.Fixture(HttpStatusCode.BadRequest, "Hepsiburada/error-validation.json"), AdapterErrorClass.Validation, "HEPSIBURADA_REQUEST_REJECTED", 400, null) },
        { "500 server", new(() => new CannedHttp(HttpStatusCode.InternalServerError, "{}"), AdapterErrorClass.Remote5xx, "HEPSIBURADA_REMOTE_ERROR", 500, TimeSpan.FromSeconds(15)) }
    };

    [Theory]
    [MemberData(nameof(ErrorAnswers))]
    public async Task EachErrorAnswerIsClassifiedThroughTheClientsSendPath(string name, CannedHttpCase answer)
    {
        var http = answer.Http();
        var client = new HepsiburadaHttpClient(http, null!, Options.Create(new HepsiburadaOptions { AuthenticationMode = "BASIC" }), TimeProvider.System, new ConfigurationBuilder().Build(), NullLogger<HepsiburadaHttpClient>.Instance);

        var result = await client.GetPackageTrackingInfoAsync(Account(), "5000000101", CancellationToken.None);

        Assert.Equal(1, http.Requests);
        answer.AssertMatches(name, result.Error);
    }

    private static HepsiburadaRequestContext Account() => new(
        new PlatformConnection { PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Hepsiburada", ExternalStoreId = "00000000-0000-4000-8000-0000000000e1", ApiVersion = "v1" },
        new Uri("https://oms.example.test/"), new Uri("https://listing.example.test/"), "fakeintegrator", "fake-secret");
}
