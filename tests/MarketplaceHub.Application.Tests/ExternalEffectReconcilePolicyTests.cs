using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ExternalEffectReconcilePolicyTests
{
    [Theory]
    [InlineData(EffectVerdict.Applied, 10, 0, 0, ExternalEffectOutcome.Applied)]
    [InlineData(EffectVerdict.NotApplied, 30, 0, 0, ExternalEffectOutcome.Wait)]
    [InlineData(EffectVerdict.NotApplied, 180, 0, 0, ExternalEffectOutcome.NotApplied)]
    [InlineData(EffectVerdict.Unknown, 600, 20, 600, ExternalEffectOutcome.Wait)]
    [InlineData(EffectVerdict.Unknown, 1800, 60, 1800, ExternalEffectOutcome.Unresolved)]
    // The first unclear read after a night's stop: old, but barely verified.
    [InlineData(EffectVerdict.Unknown, 36_000, 1, 0, ExternalEffectOutcome.Wait)]
    [InlineData(EffectVerdict.Unknown, 36_000, 4, 2400, ExternalEffectOutcome.Wait)]
    // Reads that always came more than five minutes apart: closed a day after the action, not never.
    [InlineData(EffectVerdict.Unknown, 86_400, 5, 0, ExternalEffectOutcome.Unresolved)]
    [InlineData(EffectVerdict.Unknown, 86_400, 4, 0, ExternalEffectOutcome.Wait)]
    public void AnUnclearActionIsSettledOnlyByWhatTheMarketplaceShows(EffectVerdict verdict, int ageSeconds, int unclearChecks, int verificationSeconds, ExternalEffectOutcome expected) =>
        Assert.Equal(expected, ExternalEffectReconcilePolicy.Decide(verdict, TimeSpan.FromSeconds(ageSeconds), unclearChecks, TimeSpan.FromSeconds(verificationSeconds)));

    [Fact]
    public void OnlyTheTimeBetweenCloseReadsCountsAsVerification()
    {
        var now = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal((1, 0), ExternalEffectReconcilePolicy.RecordUnclearCheck(0, 0, null, now));
        Assert.Equal((2, 30), ExternalEffectReconcilePolicy.RecordUnclearCheck(1, 0, now.AddSeconds(-30), now));
        // The worker was stopped overnight between the two reads.
        Assert.Equal((3, 30), ExternalEffectReconcilePolicy.RecordUnclearCheck(2, 30, now.AddHours(-8), now));
    }
}

public sealed class InvoicePendingPolicyTests
{
    [Theory]
    [InlineData(5, 10)]
    [InlineData(45, 30)]
    [InlineData(150, 60)]
    [InlineData(400, null)]
    public void AJustSubmittedInvoiceIsCheckedQuicklyThenEveryTwoMinutes(int pendingSeconds, int? expectedSeconds) =>
        Assert.Equal(expectedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, InvoicePendingPolicy.QuickRecheck(TimeSpan.FromSeconds(pendingSeconds)));
}
