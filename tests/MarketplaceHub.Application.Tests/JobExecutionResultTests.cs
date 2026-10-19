using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobExecutionResultTests
{
    [Fact]
    public void RefusedCredentialsBlockTheJobAndHoldTheConnectionsOtherJobs()
    {
        var result = JobExecutionResult.FromAdapterError(new(AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIALS_REJECTED", "reddedildi", 401, null, null));

        Assert.Equal(JobCompletionKind.Blocked, result.Kind);
        Assert.True(result.BackOffConnection);
        Assert.Equal(TimeSpan.FromMinutes(5), result.RetryAfter);
    }

    [Fact]
    public void AMarketplaceThatSaysHowLongToWaitIsObeyed()
    {
        var result = JobExecutionResult.FromAdapterError(new(AdapterErrorClass.Authentication, "PAZARAMA_ACCESS_DENIED", "erişim yok", 403, TimeSpan.FromMinutes(15), null));

        Assert.Equal(TimeSpan.FromMinutes(15), result.RetryAfter);
        Assert.True(result.BackOffConnection);
    }

    [Fact]
    public void OtherRefusalsStillBlockWithoutHoldingTheConnection()
    {
        var result = JobExecutionResult.FromAdapterError(new(AdapterErrorClass.Validation, "PAZARAMA_REQUEST_REJECTED", "geçersiz", 400, null, null));

        Assert.Equal(JobCompletionKind.Blocked, result.Kind);
        Assert.False(result.BackOffConnection);
    }
}
