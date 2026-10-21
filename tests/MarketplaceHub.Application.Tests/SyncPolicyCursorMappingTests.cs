using MarketplaceHub.Application;
using Xunit;

public class SyncPolicyCursorMappingTests
{
    [Theory]
    [InlineData("ORDERS", "ORDERS_HOT")]
    [InlineData("ORDER_RECONCILE_DAILY", "ORDER_RECONCILIATION")]
    [InlineData("RETURN_RECONCILE_SHORT", "RETURN_RECONCILIATION")]
    [InlineData("RETURN_RECONCILE_DAILY", "RETURN_RECONCILIATION")]
    [InlineData("STOCK_RECONCILE_MEDIUM", "STOCK_RECONCILIATION")]
    [InlineData("QUESTIONS", "QUESTIONS")]
    public void Each_tier_reads_the_cursor_its_job_writes(string policy, string cursor) =>
        Assert.Equal(cursor, MarketplaceSyncPolicyRules.CursorResourceType(policy));

    [Fact]
    public void A_question_check_backed_off_to_ten_minutes_is_not_late()
    {
        var now = DateTimeOffset.UtcNow;
        var cadence = TimeSpan.FromSeconds(QuestionCadencePolicy.MaximumSeconds);
        Assert.Equal(MarketplaceSyncHealth.Healthy, MarketplaceSyncHealthPolicy.Classify(now.AddSeconds(-455), now, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(900), expectedCadence: cadence));
    }
}
