using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class WebhookPollingPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NotificationsAreHealthyWhileTheyArriveAndMissAtMostOneOrderADay()
    {
        Assert.True(WebhookPollingPolicy.IsHealthy(Now.AddMinutes(-20), Now.AddDays(-3), [], Now));
        Assert.True(WebhookPollingPolicy.IsHealthy(null, Now.AddHours(-1), [], Now));
        Assert.True(WebhookPollingPolicy.IsHealthy(Now.AddMinutes(-5), Now.AddDays(-3), [Now.AddHours(-2)], Now));
        Assert.False(WebhookPollingPolicy.IsHealthy(Now.AddMinutes(-5), Now.AddDays(-3), [Now.AddHours(-2), Now.AddHours(-1)], Now));
        Assert.True(WebhookPollingPolicy.IsHealthy(Now.AddMinutes(-5), Now.AddDays(-3), [Now.AddDays(-2), Now.AddHours(-1)], Now));
        Assert.False(WebhookPollingPolicy.IsHealthy(Now.AddHours(-7), Now.AddDays(-3), [], Now));
        Assert.False(WebhookPollingPolicy.IsHealthy(null, null, [], Now));
    }

    [Fact]
    public void OnlyOrderPollsAreStretchedAndRightAfterRegistrationNothingCountsAsMissed()
    {
        Assert.Equal(900, WebhookPollingPolicy.StretchedIntervalSeconds("ORDERS"));
        Assert.Equal(3600, WebhookPollingPolicy.StretchedIntervalSeconds("ORDER_LIFECYCLE"));
        Assert.Null(WebhookPollingPolicy.StretchedIntervalSeconds("RETURNS"));
        Assert.False(WebhookPollingPolicy.CountsAsGap(Now.AddMinutes(-5), Now));
        Assert.True(WebhookPollingPolicy.CountsAsGap(Now.AddMinutes(-30), Now));
        Assert.False(WebhookPollingPolicy.CountsAsGap(null, Now));
    }
}

public sealed class QuestionCadencePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, 120)]
    [InlineData(45, 240)]
    [InlineData(70, 480)]
    [InlineData(200, 600)]
    public void QuietConnectionsAreAskedLessOften(int minutesSinceLastQuestion, int expectedSeconds) =>
        Assert.Equal(expectedSeconds, QuestionCadencePolicy.IntervalSeconds(120, Now.AddMinutes(-minutesSinceLastQuestion), Now));

    [Fact]
    public void AConnectionWithoutQuestionsIsAskedEveryTenMinutes() =>
        Assert.Equal(600, QuestionCadencePolicy.IntervalSeconds(120, null, Now));
}
