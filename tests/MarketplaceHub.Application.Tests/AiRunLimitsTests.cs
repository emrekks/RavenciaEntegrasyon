using MarketplaceHub.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-3: one size chart per product at a time, at most two Claude runs per tenant at once, and a daily spend
// ceiling with a generous default.
public sealed class AiRunLimitsTests
{
    [Fact]
    public void ASecondRunForTheSameProduct_IsRefusedUntilTheFirstEnds()
    {
        var gate = new AiRunGate();
        var tenant = Guid.CreateVersion7();

        using var first = gate.TryEnter(tenant, "size-chart:a", out _);
        Assert.NotNull(first);
        Assert.Null(gate.TryEnter(tenant, "size-chart:a", out var refusal));
        Assert.Equal(AiRunGate.Refusal.SubjectBusy, refusal);

        first!.Dispose();
        first.Dispose();
        using var again = gate.TryEnter(tenant, "size-chart:a", out _);
        Assert.NotNull(again);
    }

    [Fact]
    public void ATenantRunsAtMostTwoAtOnce_AndOtherTenantsAreNotHeldBack()
    {
        var gate = new AiRunGate();
        var tenant = Guid.CreateVersion7();

        using var one = gate.TryEnter(tenant, "size-chart:a", out _);
        using var two = gate.TryEnter(tenant, "size-chart:b", out _);
        Assert.NotNull(one); Assert.NotNull(two);
        Assert.Null(gate.TryEnter(tenant, "size-chart:c", out var refusal));
        Assert.Equal(AiRunGate.Refusal.TenantBusy, refusal);
        using var other = gate.TryEnter(Guid.CreateVersion7(), "size-chart:c", out _);
        Assert.NotNull(other);

        two!.Dispose();
        using var three = gate.TryEnter(tenant, "size-chart:c", out _);
        Assert.NotNull(three);
    }

    [Theory]
    [InlineData(null, 25)]
    [InlineData("40", 40)]
    [InlineData("12.5", 12.5)]
    [InlineData("0", 0)]
    [InlineData("not a number", 25)]
    public void TheDailyLimitComesFromConfiguration_WithAGenerousDefault(string? configured, double expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AiSpendLimit.ConfigurationKey] = configured }).Build();

        var limit = AiSpendLimit.From(configuration);

        Assert.Equal((decimal)expected, limit.DailyUsd);
        Assert.Equal(expected > 0, limit.Enabled);
    }
}
