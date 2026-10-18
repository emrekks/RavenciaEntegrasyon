using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ExternalEffectReconcilePolicyTests
{
    [Theory]
    [InlineData(EffectVerdict.Applied, 10, ExternalEffectOutcome.Applied)]
    [InlineData(EffectVerdict.NotApplied, 30, ExternalEffectOutcome.Wait)]
    [InlineData(EffectVerdict.NotApplied, 180, ExternalEffectOutcome.NotApplied)]
    [InlineData(EffectVerdict.Unknown, 600, ExternalEffectOutcome.Wait)]
    [InlineData(EffectVerdict.Unknown, 1800, ExternalEffectOutcome.Unresolved)]
    public void AnUnclearActionIsSettledOnlyByWhatTheMarketplaceShows(EffectVerdict verdict, int ageSeconds, ExternalEffectOutcome expected) =>
        Assert.Equal(expected, ExternalEffectReconcilePolicy.Decide(verdict, TimeSpan.FromSeconds(ageSeconds)));
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
