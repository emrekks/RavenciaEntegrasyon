using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceDeliveryRetryPolicyTests
{
    [Fact]
    public void UsesProgressiveInvoiceDeliveryBackoff()
    {
        Assert.Equal(
            new[] { 30d, 120d, 600d, 1800d },
            Enumerable.Range(1, 4).Select(attempt => InvoiceDeliveryRetryPolicy.DelayAfterAttempt(attempt).TotalSeconds));
    }

    [Theory]
    [InlineData(1, 20, 30)]
    [InlineData(1, 45, 45)]
    [InlineData(2, 60, 120)]
    [InlineData(2, 300, 300)]
    [InlineData(5, 300, 1800)]
    public void ProviderRateLimitCanDelayButCannotShortenThePolicy(
        int attempt,
        int providerRetryAfterSeconds,
        int expectedSeconds) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            InvoiceDeliveryRetryPolicy.DelayAfterAttempt(attempt, TimeSpan.FromSeconds(providerRetryAfterSeconds)));
}
