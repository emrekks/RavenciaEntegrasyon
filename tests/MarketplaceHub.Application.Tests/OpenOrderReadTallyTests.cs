using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// OpenOrderReadTally's rules A–D.
public sealed class OpenOrderReadTallyTests
{
    private static AdapterError Error(AdapterErrorClass @class, string code) => new(@class, code, code, null, null, null);

    private static OpenOrderReadTally Failing(params AdapterError[] errors)
    {
        var tally = new OpenOrderReadTally();
        foreach (var error in errors) tally.Failed(error);
        return tally;
    }

    // A: the connection's failure, one request is enough; kinds may differ (a token error then a network one).
    [Theory]
    [InlineData(AdapterErrorClass.TransientNetwork, "SHOPIFY_NETWORK_ERROR")]
    [InlineData(AdapterErrorClass.RateLimit, "N11_RATE_LIMITED")]
    [InlineData(AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIALS_REJECTED")]
    [InlineData(AdapterErrorClass.Remote5xx, "PAZARAMA_TOKEN_REMOTE_ERROR")]
    [InlineData(AdapterErrorClass.ContractViolation, "PAZARAMA_TOKEN_INVALID")]
    public void RuleA_AConnectionLevelFailureFailsTheJob(AdapterErrorClass @class, string code)
    {
        var tally = Failing(Error(@class, code), Error(AdapterErrorClass.TransientNetwork, "PAZARAMA_NETWORK_ERROR"));

        Assert.Equal(OpenOrderScanVerdict.ConnectionFailed, tally.Verdict());
        Assert.Equal(code, tally.FirstFailure!.Code);
    }

    [Fact]
    public void RuleA_AnUnclearTokenAnswerOnEveryOrderIsAManualReview()
    {
        var token = Error(AdapterErrorClass.ContractViolation, "PAZARAMA_TOKEN_INVALID");
        var tally = Failing(token, token, token);

        Assert.Equal(JobCompletionKind.ManualReview, JobExecutionResult.FromAdapterError(tally.FirstFailure!).Kind);
    }

    // B: 5xx only, from two requests up: unread, ended blocked in one attempt (no retry chain, no hold on the connection).
    [Fact]
    public void RuleB_ServerErrorsFromTwoRequestsAreAnUnreadRun()
    {
        var error = Error(AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR");
        var tally = Failing(error, error, Error(AdapterErrorClass.Remote5xx, "N11_OTHER_5XX"));

        Assert.Equal(OpenOrderScanVerdict.Unread, tally.Verdict());
        var result = tally.UnreadResult();
        Assert.Equal(JobCompletionKind.Blocked, result.Kind);
        Assert.Equal("N11_REMOTE_ERROR", result.ErrorCode);
        Assert.False(result.BackOffConnection);
        Assert.Null(result.RetryAfter);
        Assert.Contains("3 istek", result.ErrorSummary);
    }

    // C: one answer makes it a success. D: one request with one 5xx, a mix of kinds, an order's own contract error.
    [Fact]
    public void RulesCAndD_AreASuccess()
    {
        var answered = Failing(Error(AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR"), Error(AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR"));
        answered.Answered();
        var contract = Error(AdapterErrorClass.ContractViolation, "PAZARAMA_ORDER_CONTRACT_INVALID");

        Assert.Equal(OpenOrderScanVerdict.Proceed, answered.Verdict());
        Assert.Equal(2, answered.Failures);
        Assert.Equal(OpenOrderScanVerdict.Proceed, Failing(Error(AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR")).Verdict());
        Assert.Equal(OpenOrderScanVerdict.Proceed, Failing(Error(AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR"), Error(AdapterErrorClass.TransientNetwork, "N11_NETWORK_ERROR")).Verdict());
        Assert.Equal(OpenOrderScanVerdict.Proceed, Failing(contract, contract).Verdict());
        Assert.Equal(OpenOrderScanVerdict.Proceed, new OpenOrderReadTally().Verdict());
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
