using System.Net;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan finding B-10 (25 Nov 2026): small adapter defects.
public sealed class ScanB10AdapterTests
{
    // A Hepsiburada 429 whose headers say "retry in 0 seconds" (or a reset already past) waits at least one second.
    [Fact]
    public void A_hepsiburada_rate_limit_that_says_zero_waits_at_least_one_second()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), HepsiburadaHttpClient.Error(HttpStatusCode.TooManyRequests, TimeSpan.Zero, null).RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(1), HepsiburadaHttpClient.Error(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(-3), null).RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(7), HepsiburadaHttpClient.Error(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7), null).RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(5), HepsiburadaHttpClient.Error(HttpStatusCode.TooManyRequests, null, null).RetryAfter);
    }

    // The cargo's Delivered moves an open package on, never a cancelled or returned one.
    [Theory]
    [InlineData("Delivered", "Open", "Delivered")]
    [InlineData("Delivered", "Undelivered", "Delivered")]
    [InlineData("Delivered", "Cancelled", "Cancelled")]
    [InlineData("Delivered", "Returned", "Returned")]
    [InlineData("InTransit", "Open", "InTransit")]
    [InlineData("InTransit", "Delivered", "Delivered")]
    [InlineData("Undelivered", "Delivered", "Delivered")]
    [InlineData(null, "Open", "Open")]
    public void A_hepsiburada_tracking_read_moves_only_an_open_package_on(string? tracking, string listed, string expected)
    {
        Assert.Equal(expected, HepsiburadaHttpClient.TrackingStatus(tracking, listed));
    }

    // A late Shopify order or refund delivery is processed (its ingest reads the order by id); any other topic is dropped.
    [Theory]
    [InlineData("orders/updated", false)]
    [InlineData("orders/create", false)]
    [InlineData("refunds/create", false)]
    [InlineData("products/delete", true)]
    [InlineData("made/up", true)]
    public void A_late_shopify_order_delivery_is_processed(string topic, bool dropped)
    {
        Assert.Equal(dropped, ShopifyWebhookVerifier.DropsWhenStale(topic));
    }
}
