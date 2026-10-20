using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ConnectionLifecyclePolicyTests
{
    private static readonly DateTimeOffset DisabledAt = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reactivation_needs_a_connection_test_newer_than_the_disable()
    {
        Assert.False(ConnectionActivationPolicy.HasTestSinceDisabled(DisabledAt.AddHours(-1), DisabledAt));
        Assert.False(ConnectionActivationPolicy.HasTestSinceDisabled(null, DisabledAt));
        Assert.True(ConnectionActivationPolicy.HasTestSinceDisabled(DisabledAt.AddMinutes(1), DisabledAt));
        // Connections disabled before the time was kept follow the earlier rule.
        Assert.True(ConnectionActivationPolicy.HasTestSinceDisabled(DisabledAt.AddDays(-30), null));
    }

    [Fact]
    public void Refused_credentials_during_a_sync_are_recorded_on_the_connection()
    {
        var refused = JobExecutionResult.FromAdapterError(new AdapterError(AdapterErrorClass.Authentication, "TRENDYOL_UNAUTHORIZED", "Kimlik reddedildi.", 401, null, null));

        Assert.True(MarketplaceJobProcessor.IsConnectionAuthenticationFailure(MarketplaceJobTypes.OrderSync, refused));
        // The connection test writes its own outcome.
        Assert.False(MarketplaceJobProcessor.IsConnectionAuthenticationFailure(MarketplaceJobTypes.ConnectionTest, refused));
    }

    [Fact]
    public void Quota_and_other_failures_are_not_credential_failures()
    {
        var quota = JobExecutionResult.FromAdapterError(new AdapterError(AdapterErrorClass.RateLimit, "TRENDYOL_RATE_LIMITED", "Kota dolu.", 429, null, null));
        var validation = JobExecutionResult.FromAdapterError(new AdapterError(AdapterErrorClass.Validation, "PAYLOAD_INVALID", "Geçersiz.", 422, null, null));

        Assert.False(MarketplaceJobProcessor.IsConnectionAuthenticationFailure(MarketplaceJobTypes.OrderSync, quota));
        Assert.False(MarketplaceJobProcessor.IsConnectionAuthenticationFailure(MarketplaceJobTypes.OrderSync, validation));
    }
}
