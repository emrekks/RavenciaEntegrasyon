using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed class JobProcessingException(JobExecutionResult result) : Exception(result.ErrorSummary ?? result.ErrorCode)
{
    public JobExecutionResult Result { get; } = result;

    public static JobProcessingException FromAdapter(AdapterError error) => new(JobExecutionResult.FromAdapterError(error));
}
