using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReturnActionDuePolicyTests
{
    private static readonly DateTimeOffset Waiting = new(2026, 10, 23, 13, 42, 0, TimeSpan.Zero);

    [Fact]
    public void A_later_change_of_a_waiting_claim_does_not_move_its_estimated_deadline_later()
    {
        // Trendyol touched the claim (a note, cargo) six hours later; its 48 hours did not restart.
        var next = ReturnActionDuePolicy.Next(Waiting.AddHours(48), true, "WaitingInAction", Waiting.AddHours(54), true, "WaitingInAction");

        Assert.Equal((Waiting.AddHours(48), true), next);
    }

    [Fact]
    public void An_earlier_estimate_a_new_status_or_a_marketplace_deadline_is_taken()
    {
        Assert.Equal((Waiting.AddHours(40), true), ReturnActionDuePolicy.Next(Waiting.AddHours(48), true, "WaitingInAction", Waiting.AddHours(40), true, "WaitingInAction"));
        Assert.Equal((Waiting.AddHours(80), true), ReturnActionDuePolicy.Next(Waiting.AddHours(48), true, "Created", Waiting.AddHours(80), true, "WaitingInAction"));
        Assert.Equal((Waiting.AddHours(72), false), ReturnActionDuePolicy.Next(Waiting.AddHours(48), true, "WaitingInAction", Waiting.AddHours(72), false, "WaitingInAction"));
        Assert.Equal((Waiting.AddHours(72), false), ReturnActionDuePolicy.Next(Waiting.AddHours(48), false, "AwaitingAction", Waiting.AddHours(72), false, "AwaitingAction"));
        Assert.Equal((null, false), ReturnActionDuePolicy.Next(Waiting.AddHours(48), true, "WaitingInAction", null, false, "Accepted"));
    }
}
