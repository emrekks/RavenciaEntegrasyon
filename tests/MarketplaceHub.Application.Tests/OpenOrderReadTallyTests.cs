using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OpenOrderReadTallyTests
{
    private static AdapterError Error(AdapterErrorClass @class, string code) => new(@class, code, code, null, null, null);

    private static OpenOrderReadTally Failing(params AdapterError[] errors)
    {
        var tally = new OpenOrderReadTally();
        foreach (var error in errors) tally.Failed(error);
        return tally;
    }

    // The connection's failure: one request is enough.
    [Theory]
    [InlineData(AdapterErrorClass.TransientNetwork, "SHOPIFY_NETWORK_ERROR")]
    [InlineData(AdapterErrorClass.RateLimit, "N11_RATE_LIMITED")]
    [InlineData(AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIALS_REJECTED")]
    [InlineData(AdapterErrorClass.Remote5xx, "PAZARAMA_TOKEN_REMOTE_ERROR")]
    [InlineData(AdapterErrorClass.ContractViolation, "PAZARAMA_TOKEN_INVALID")]
    public void AConnectionLevelFailureFailsTheScan(AdapterErrorClass @class, string code)
    {
        var tally = Failing(Error(@class, code));

        Assert.Equal(OpenOrderScanVerdict.Fail, tally.Verdict());
        Assert.Equal(code, tally.FirstFailure!.Code);
    }

    // An unclear token answer (a contract code) makes the job a manual review, not a silent success.
    [Fact]
    public void AnUnclearTokenAnswerOnEveryOrderIsAManualReview()
    {
        var token = Error(AdapterErrorClass.ContractViolation, "PAZARAMA_TOKEN_INVALID");
        var tally = Failing(token, token, token);

        Assert.Equal(OpenOrderScanVerdict.Fail, tally.Verdict());
        Assert.Equal(JobCompletionKind.ManualReview, JobExecutionResult.FromAdapterError(tally.FirstFailure!).Kind);
    }

    [Fact]
    public void ServerErrorsFailOnlyFromTwoRequestsAndOnlyUntilPoisoned()
    {
        var one = Error(AdapterErrorClass.Remote5xx, "SHOPIFY_REMOTE_ERROR");

        Assert.Equal(OpenOrderScanVerdict.Proceed, Failing(one).Verdict());
        Assert.Equal(OpenOrderScanVerdict.FailUntilPoisoned, Failing(one, one).Verdict());
    }

    [Fact]
    public void DifferentCodesOrOneAnswerLetTheScanGoOn()
    {
        var mixed = Failing(Error(AdapterErrorClass.TransientNetwork, "SHOPIFY_NETWORK_ERROR"), Error(AdapterErrorClass.Remote5xx, "SHOPIFY_REMOTE_ERROR"));
        var answered = Failing(Error(AdapterErrorClass.TransientNetwork, "SHOPIFY_NETWORK_ERROR"));
        answered.Answered();

        Assert.Equal(OpenOrderScanVerdict.Proceed, mixed.Verdict());
        Assert.Equal(OpenOrderScanVerdict.Proceed, answered.Verdict());
        Assert.Equal(1, answered.Failures);
    }

    // One order's own contract error, even on every order, is the orders', not the connection's.
    [Fact]
    public void OrderContractErrorsLeaveTheScanASuccess()
    {
        var contract = Error(AdapterErrorClass.ContractViolation, "PAZARAMA_ORDER_CONTRACT_INVALID");

        Assert.Equal(OpenOrderScanVerdict.Proceed, Failing(contract, contract).Verdict());
        Assert.Equal(OpenOrderScanVerdict.Proceed, Failing(Error(AdapterErrorClass.Validation, "N11_REQUEST_REJECTED"), Error(AdapterErrorClass.Validation, "N11_REQUEST_REJECTED")).Verdict());
    }

    [Fact]
    public void AScanThatAskedNothingGoesOn() => Assert.Equal(OpenOrderScanVerdict.Proceed, new OpenOrderReadTally().Verdict());

    [Theory]
    [InlineData(null, 0, null)]
    [InlineData("after:k-24", 0, "after:k-24")]
    [InlineData("strikes:2|after:k-24", 2, "after:k-24")]
    [InlineData("strikes:1|", 1, null)]
    [InlineData("strikes:1|trendyol-open-v1:recent:p-1", 1, "trendyol-open-v1:recent:p-1")]
    [InlineData("strikes:x|after:1", 0, "strikes:x|after:1")]
    public void StrikesAreKeptInFrontOfThePosition(string? opaque, int strikes, string? position)
    {
        Assert.Equal((strikes, position), OpenScanStrikes.Split(opaque));
        if (strikes > 0) Assert.Equal(opaque, OpenScanStrikes.Join(strikes, position));
    }

    [Fact]
    public void ThePartlyFailedNoteIsOnlyOnOpenOrderScans()
    {
        Assert.Equal(" · 2 sipariş okunamadı", MarketplaceJobProcessor.OpenOrderScanNote(MarketplaceJobTypes.PazaramaOrderStatusSync, 2));
        Assert.Equal(" · 1 sipariş okunamadı", MarketplaceJobProcessor.OpenOrderScanNote(MarketplaceJobTypes.OrderStatusSync, 1));
        Assert.Equal("", MarketplaceJobProcessor.OpenOrderScanNote(MarketplaceJobTypes.PazaramaOrderStatusSync, 0));
        Assert.Equal("", MarketplaceJobProcessor.OpenOrderScanNote(MarketplaceJobTypes.PazaramaOrderSync, 2));
        Assert.Equal("", MarketplaceJobProcessor.OpenOrderScanNote(MarketplaceJobTypes.OrderRecoverySync, 2));
        Assert.Equal("", MarketplaceJobProcessor.OpenOrderScanNote(MarketplaceJobTypes.ReturnSync, 2));
    }
}
