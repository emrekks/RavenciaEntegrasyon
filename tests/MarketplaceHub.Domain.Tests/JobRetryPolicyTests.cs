using MarketplaceHub.Domain;

namespace MarketplaceHub.Domain.Tests;

public sealed class JobRetryPolicyTests
{
    [Fact]
    public void Default_schedule_matches_the_binding_retry_sequence()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20), TimeSpan.FromHours(1)],
            JobRetryPolicy.DefaultSchedule);
        Assert.Equal(6, JobRetryPolicy.DefaultMaxAttempts);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 60)]
    [InlineData(3, 300)]
    [InlineData(4, 1200)]
    [InlineData(5, 3600)]
    public void Retry_delay_has_deterministic_ten_to_twenty_percent_jitter(int attempt, int baseSeconds)
    {
        var jobId = Guid.Parse("10000000-0000-0000-0000-000000000000");
        var actual = JobRetryPolicy.DelayAfterAttempt(attempt, jobId);

        Assert.InRange(actual, TimeSpan.FromSeconds(baseSeconds * 1.10), TimeSpan.FromSeconds(baseSeconds * 1.20));
        Assert.Equal(actual, JobRetryPolicy.DelayAfterAttempt(attempt, jobId));
    }

    [Fact]
    public void Heartbeat_interval_is_within_one_third_of_the_lease()
    {
        var interval = JobRetryPolicy.HeartbeatInterval(JobRetryPolicy.DefaultLeaseDuration);

        Assert.Equal(TimeSpan.FromSeconds(30), interval);
        Assert.True(interval <= JobRetryPolicy.DefaultLeaseDuration / 3);
    }

    [Fact]
    public void Heartbeat_interval_rejects_a_non_positive_lease()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JobRetryPolicy.HeartbeatInterval(TimeSpan.Zero));
    }
}
