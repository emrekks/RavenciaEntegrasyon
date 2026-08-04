using MarketplaceHub.Application;

namespace MarketplaceHub.Application.Tests;

public sealed class JobExecutionResultTests
{
    [Theory]
    [InlineData(AdapterErrorClass.TransientNetwork, JobCompletionKind.Retry)]
    [InlineData(AdapterErrorClass.RateLimit, JobCompletionKind.Retry)]
    [InlineData(AdapterErrorClass.Remote5xx, JobCompletionKind.Retry)]
    [InlineData(AdapterErrorClass.ContractViolation, JobCompletionKind.ManualReview)]
    [InlineData(AdapterErrorClass.Authentication, JobCompletionKind.Blocked)]
    [InlineData(AdapterErrorClass.Validation, JobCompletionKind.Blocked)]
    [InlineData(AdapterErrorClass.InternalBug, JobCompletionKind.ManualReview)]
    public void Adapter_error_is_mapped_to_safe_job_completion(AdapterErrorClass errorClass, JobCompletionKind expected)
    {
        var error = new AdapterError(errorClass, "TEST", new string('x', 700), 500, TimeSpan.FromSeconds(12), "remote-1");
        var result = JobExecutionResult.FromAdapterError(error);
        Assert.Equal(expected, result.Kind);
        Assert.True(result.ErrorSummary!.Length <= 512);
        Assert.Equal("remote-1", result.RemoteRequestId);
    }
}
