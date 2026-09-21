using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OrderLinePresentationPolicyTests
{
    [Fact]
    public void FullyCancelledLineIsNotPresented()
    {
        Assert.Equal(0m, OrderLinePresentationPolicy.ActiveQuantity(3m, 3m));
        Assert.False(OrderLinePresentationPolicy.HasActiveQuantity(3m, 3m));
    }

    [Fact]
    public void PartiallyCancelledLineKeepsOnlyActiveQuantity()
    {
        Assert.Equal(1m, OrderLinePresentationPolicy.ActiveQuantity(3m, 2m));
        Assert.True(OrderLinePresentationPolicy.HasActiveQuantity(3m, 2m));
    }

    [Fact]
    public void CancellationCannotProduceNegativeActiveQuantity()
    {
        Assert.Equal(0m, OrderLinePresentationPolicy.ActiveQuantity(1m, 4m));
        Assert.False(OrderLinePresentationPolicy.HasActiveQuantity(1m, 4m));
    }
}
