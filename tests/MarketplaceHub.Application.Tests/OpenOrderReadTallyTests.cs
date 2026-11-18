using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// OpenOrderReadTally: rule A only; everything else goes on as before.
public sealed class OpenOrderReadTallyTests
{
    private static AdapterError Error(AdapterErrorClass @class, string code) => new(@class, code, code, null, null, null);

    private static OpenOrderReadTally Failing(params AdapterError[] errors)
    {
        var tally = new OpenOrderReadTally();
        foreach (var error in errors) tally.Failed(error);
        return tally;
    }

    // Rule A: no answer and every failure the connection's; one request is enough and the kinds may differ.
    [Theory]
    [InlineData(AdapterErrorClass.TransientNetwork, "SHOPIFY_NETWORK_ERROR")]
    [InlineData(AdapterErrorClass.RateLimit, "N11_RATE_LIMITED")]
    [InlineData(AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIALS_REJECTED")]
    [InlineData(AdapterErrorClass.Remote5xx, "PAZARAMA_TOKEN_REMOTE_ERROR")]
    [InlineData(AdapterErrorClass.ContractViolation, "PAZARAMA_TOKEN_INVALID")]
    public void RuleA_AConnectionLevelFailureFailsTheJob(AdapterErrorClass @class, string code)
    {
        Assert.Equal(code, Failing(Error(@class, code)).ConnectionFailure()!.Code);
        Assert.Equal(code, Failing(Error(@class, code), Error(AdapterErrorClass.TransientNetwork, "PAZARAMA_NETWORK_ERROR")).ConnectionFailure()!.Code);
    }

    [Fact]
    public void RuleA_AnUnclearTokenAnswerOnEveryOrderIsAManualReview()
    {
        var token = Error(AdapterErrorClass.ContractViolation, "PAZARAMA_TOKEN_INVALID");

        Assert.Equal(JobCompletionKind.ManualReview, JobExecutionResult.FromAdapterError(Failing(token, token, token).ConnectionFailure()!).Kind);
    }

    // Everything else goes on: server errors however many, one answer, a mix with a non-connection error, an order's own
    // contract error, nothing asked.
    [Fact]
    public void AnythingElseGoesOn()
    {
        var serverError = Error(AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR");
        var answered = Failing(Error(AdapterErrorClass.TransientNetwork, "N11_NETWORK_ERROR"), Error(AdapterErrorClass.TransientNetwork, "N11_NETWORK_ERROR"));
        answered.Answered();
        var contract = Error(AdapterErrorClass.ContractViolation, "PAZARAMA_ORDER_CONTRACT_INVALID");

        Assert.Null(Failing(serverError).ConnectionFailure());
        Assert.Null(Failing(serverError, serverError, serverError).ConnectionFailure());
        Assert.Null(answered.ConnectionFailure());
        Assert.Equal(2, answered.Failures);
        Assert.Null(Failing(Error(AdapterErrorClass.TransientNetwork, "N11_NETWORK_ERROR"), serverError).ConnectionFailure());
        Assert.Null(Failing(contract, contract).ConnectionFailure());
        Assert.Null(new OpenOrderReadTally().ConnectionFailure());
    }

    // Every *CREDENTIAL* code the adapters raise is an Authentication error, so the class alone covers them.
    [Theory]
    [InlineData("PAZARAMA_CREDENTIAL_INVALID")]
    [InlineData("N11_CREDENTIAL_INVALID")]
    [InlineData("SHOPIFY_CREDENTIAL_INVALID")]
    [InlineData("HEPSIBURADA_QUESTION_CREDENTIALS")]
    public void CredentialCodesAreAuthenticationErrors(string code) =>
        Assert.True(OpenOrderReadTally.IsConnectionLevel(Error(AdapterErrorClass.Authentication, code)));

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
