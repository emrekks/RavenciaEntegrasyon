using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobWaitingPolicyTests
{
    [Theory]
    [InlineData("PRODUCT_BATCH_PENDING")]
    [InlineData("PRODUCT_UPDATE_BATCH_PENDING")]
    [InlineData("PRICE_INVENTORY_BATCH_PENDING")]
    [InlineData("PRODUCT_APPROVAL_PENDING")]
    [InlineData("HEPSIBURADA_PRODUCT_APPROVAL_PENDING")]
    [InlineData("INVOICE_REMOTE_PENDING")]
    public void AMarketplaceStillProcessingIsAWaitingCheck(string code)
    {
        Assert.True(JobWaitingPolicy.IsWaiting(JobExecutionResult.Retry(code, "işleniyor", TimeSpan.FromSeconds(15))));
    }

    [Fact]
    public void RealFailuresAndRefundedWaitsAreNotWaitingChecks()
    {
        Assert.False(JobWaitingPolicy.IsWaiting(JobExecutionResult.Retry("REMOTE_TIMEOUT", "zaman aşımı")));
        Assert.False(JobWaitingPolicy.IsWaiting(JobExecutionResult.Blocked("N11_TASK_REJECTED", "reddedildi")));
        Assert.False(JobWaitingPolicy.IsWaiting(JobExecutionResult.ManualReview("PRODUCT_BATCH_PENDING", "inceleme")));
        // A deferral already gives its attempt back on its own.
        Assert.False(JobWaitingPolicy.IsWaiting(JobExecutionResult.Deferred("INVOICE_REMOTE_PENDING", "kısa bekleme")));
    }

    [Fact]
    public void AnAttemptReadsAsACheckUnlessTheJobRanOutOfAttemptsOnIt()
    {
        Assert.Equal("WAITING", JobWaitingPolicy.AttemptOutcome(true, false, "PRODUCT_BATCH_PENDING", false));
        Assert.Equal("FAILED", JobWaitingPolicy.AttemptOutcome(true, false, "PRODUCT_BATCH_PENDING", true));
        Assert.Equal("FAILED", JobWaitingPolicy.AttemptOutcome(true, false, "N11_TASK_REJECTED", false));
        Assert.Equal("FAILED", JobWaitingPolicy.AttemptOutcome(true, false, null, false));
        Assert.Equal("SUCCEEDED", JobWaitingPolicy.AttemptOutcome(true, true, null, false));
        Assert.Equal("RUNNING", JobWaitingPolicy.AttemptOutcome(false, false, null, false));
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(4, 15)]
    [InlineData(5, 30)]
    [InlineData(6, 60)]
    [InlineData(7, 120)]
    [InlineData(8, 300)]
    [InlineData(40, 300)]
    public void ALongBatchIsAskedLessOftenAfterAFewChecks(int checks, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), JobWaitingPolicy.Delay(TimeSpan.FromSeconds(15), checks));
    }

    [Fact]
    public void ALongerWaitTheProcessorAskedForIsKept()
    {
        Assert.Equal(TimeSpan.FromHours(1), JobWaitingPolicy.Delay(TimeSpan.FromHours(1), 2));
        Assert.Equal(TimeSpan.FromSeconds(30), JobWaitingPolicy.Delay(null, 1));
    }

    [Fact]
    public void AnApprovalCheckWaitsOutItsSevenDaysWithoutUsingAttempts()
    {
        Assert.True(JobWaitingPolicy.FreeWaitingWindow(MarketplaceJobTypes.ProductApprovalReconcile) > TimeSpan.FromDays(7));
        Assert.Equal(TimeSpan.FromDays(1), JobWaitingPolicy.FreeWaitingWindow(MarketplaceJobTypes.ProductCreate));
    }
}

public sealed class ProductApprovalListWalkTests
{
    [Theory]
    // The live Pazarama store on 29 October 2026: 30 waiting barcodes, 18 linked, 30 unsettled; the walk is a few pages.
    [InlineData(30, 18, 30, 30)]
    // One card with one barcode: one lookup is cheaper than any walk.
    [InlineData(1, 0, 1, 0)]
    // A big store with few waiting barcodes: asking each one costs less than walking thousands of products.
    [InlineData(6, 3000, 6, 0)]
    public void TheListsAreWalkedOnlyWhenThatCostsLessThanAskingEachBarcode(int waiting, int linked, int unsettled, int expected)
    {
        Assert.Equal(expected, ProductApprovalReconciliationPolicy.ListWalkBudget(waiting, linked, unsettled));
    }

    private static AdapterResult<AdapterPageResult<RemoteCatalogProduct>> Page(string json, bool pending, string? next)
    {
        using var document = JsonDocument.Parse(json);
        var page = PazaramaProductMapper.Page(document.RootElement, pending);
        return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(page.Items, next, next is not null));
    }

    [Fact]
    public async Task OneWalkAnswersEveryListedBarcodeAsTheSingleLookupWould()
    {
        var asked = new List<string?>();
        var snapshot = await PazaramaHttpClient.WalkApprovalSnapshotAsync(request =>
        {
            asked.Add(request.Cursor);
            return Task.FromResult(request.Cursor switch
            {
                null => Page("""{"data":[{"code":"A1","groupCode":"G1","productStatus":1},{"code":"A2","groupCode":"G1","productStatus":10}],"success":true}""", false, "A:c2"),
                "A:c2" => Page("""{"data":[{"code":"A3","groupCode":"G2"}],"success":true}""", false, "U:1:"),
                _ => Page("""{"data":[{"code":"P1","groupCode":"G3","status":"Onay Bekliyor"},{"code":"R1","groupCode":"G4","status":"Reddedildi"},{"code":"A1","groupCode":"G1","status":"Onay Bekliyor"}],"success":true}""", true, null)
            });
        }, 10, CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        var statuses = snapshot.Value!.ByBarcode;
        Assert.True(snapshot.Value.Complete);
        Assert.Equal([null, "A:c2", "U:1:"], asked);
        Assert.Equal("APPROVED", statuses["A1"].Status);
        Assert.Equal("group:G1", statuses["A1"].ExternalProductId);
        Assert.Equal("ARCHIVED", statuses["A2"].Status);
        Assert.Equal("APPROVED", statuses["a3"].Status);
        Assert.Equal("PENDING_APPROVAL", statuses["P1"].Status);
        Assert.Equal("REJECTED", statuses["R1"].Status);
        Assert.Equal("PAZARAMA_PRODUCT_REJECTED", statuses["R1"].RejectionCode);
        Assert.False(statuses.ContainsKey("MISSING"));
    }

    [Fact]
    public async Task TheWalkStopsAtItsBudgetAndSaysItDidNotFinish()
    {
        var calls = 0;
        var snapshot = await PazaramaHttpClient.WalkApprovalSnapshotAsync(_ =>
        {
            calls++;
            return Task.FromResult(Page($$"""{"data":[{"code":"A{{calls}}","groupCode":"G"}],"success":true}""", false, $"A:c{calls}"));
        }, 3, CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.False(snapshot.Value!.Complete);
        Assert.Equal(3, calls);
        Assert.Equal(3, snapshot.Value.ByBarcode.Count);
    }

    [Fact]
    public async Task ARefusedPageEndsTheWalkWithThatRefusal()
    {
        var refusal = new AdapterError(AdapterErrorClass.RateLimit, "PAZARAMA_RATE_LIMITED", "Kota", null, TimeSpan.FromSeconds(60), null);
        var snapshot = await PazaramaHttpClient.WalkApprovalSnapshotAsync(_ => Task.FromResult(AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(refusal)), 5, CancellationToken.None);

        Assert.False(snapshot.IsSuccess);
        Assert.Equal("PAZARAMA_RATE_LIMITED", snapshot.Error!.Code);
    }
}
