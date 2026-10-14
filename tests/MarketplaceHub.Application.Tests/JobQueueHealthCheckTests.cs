using MarketplaceHub.Api.Operations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobQueueHealthCheckTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    [Fact]
    public void MovingQueue_IsHealthy()
    {
        Assert.Equal(HealthStatus.Healthy, JobQueueHealthCheck.Evaluate(Now, null, 0, Limit).Status);
        Assert.Equal(HealthStatus.Healthy, JobQueueHealthCheck.Evaluate(Now, Now.AddMinutes(-3), 0, Limit).Status);
    }

    [Fact]
    public void LongWaitingJobOrDeadLease_IsDegradedButNeverUnhealthy()
    {
        var waiting = JobQueueHealthCheck.Evaluate(Now, Now.AddMinutes(-11), 0, Limit);
        var staleLease = JobQueueHealthCheck.Evaluate(Now, null, 2, Limit);

        Assert.Equal(HealthStatus.Degraded, waiting.Status);
        Assert.Equal(660d, waiting.Data["oldestRunnableWaitSeconds"]);
        Assert.Equal(HealthStatus.Degraded, staleLease.Status);
        Assert.Equal(2, staleLease.Data["staleLeases"]);
    }
}
