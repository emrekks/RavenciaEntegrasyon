using System.Text.Json;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Shopify returns were never read on live (no RETURNS policy, no activation read) until 6 Nov 2026.
public sealed class ShopifyReturnSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShopifyReturnsAreAScheduledReadThatStartsEnabled()
    {
        Assert.True(ScheduledJobProducer.IsShopifyReadPolicy("RETURNS"));
        Assert.True(ScheduledJobProducer.SupportsShopifyPolicy("RETURNS"));
        Assert.True(ScheduledJobProducer.DefaultPolicyEnabled("SHOPIFY", "RETURNS"));
        // The return decisions stay a write behind its own switch, off by default.
        Assert.False(ScheduledJobProducer.IsShopifyReadPolicy(MarketplaceExternalWritePolicies.Return));
        Assert.False(ScheduledJobProducer.DefaultPolicyEnabled("SHOPIFY", MarketplaceExternalWritePolicies.Return));
    }

    [Fact]
    public void ShopifyReturnsKeepTheCadenceOfTheOtherMarketplaces()
    {
        var defaults = ScheduledJobProducer.DefaultPolicies(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Single(x => x.ResourceType == "RETURNS");
        var shopify = ScheduledOrderPollingCadencePolicy.ForPlatform("SHOPIFY", "RETURNS", defaults.IntervalSeconds, defaults.JitterSeconds);

        Assert.Equal(ScheduledOrderPollingCadencePolicy.ForPlatform("TRENDYOL", "RETURNS", defaults.IntervalSeconds, defaults.JitterSeconds), shopify);
        Assert.Equal(ReturnCadencePolicy.BaseSeconds, shopify.IntervalSeconds);
    }

    [Fact]
    public void ShopifyActivationQueuesAFullReturnRead()
    {
        var connectionId = Guid.NewGuid();
        var bootstrap = MarketplaceConnectionService.CreateReturnActivationBootstrap("shopify", connectionId);

        Assert.NotNull(bootstrap);
        Assert.Equal(MarketplaceJobTypes.ReturnSync, bootstrap.Value.JobType);
        Assert.Equal(MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.ForPlatform("SHOPIFY", MarketplaceJobTypes.ReturnSync));
        using var payload = JsonDocument.Parse(bootstrap.Value.PayloadJson);
        Assert.Equal(connectionId, payload.RootElement.GetProperty("connectionId").GetGuid());
        Assert.True(payload.RootElement.GetProperty("forceFull").GetBoolean());
    }

    [Fact]
    public void TheFirstOrAFullReadGoesBack180Days()
    {
        Assert.Equal(Now.AddDays(-180), MarketplaceJobProcessor.ShopifyReturnReadStart(false, null, null, Now));
        Assert.Equal(Now.AddDays(-180), MarketplaceJobProcessor.ShopifyReturnReadStart(true, Now.AddHours(-1), Now.AddHours(-1), Now));
    }

    [Fact]
    public void ALaterReadStartsBeforeTheLastCompletedReadBegan()
    {
        var watermark = Now.AddMinutes(-10);

        Assert.Equal(watermark.AddMinutes(-15), MarketplaceJobProcessor.ShopifyReturnReadStart(false, Now.AddMinutes(-9), watermark, Now));
        // Without a watermark the last success stands in for it.
        Assert.Equal(Now.AddMinutes(-24), MarketplaceJobProcessor.ShopifyReturnReadStart(false, Now.AddMinutes(-9), null, Now));
    }

    [Fact]
    public void AFailedRunDoesNotTurnTheNextIntoA180DayRead()
    {
        // The read start depends only on the last completed read: a failure leaves the watermark where it was, so the
        // next run reads from there instead of the whole 180 days.
        var watermark = Now.AddDays(-2);

        Assert.Equal(watermark.AddMinutes(-15), MarketplaceJobProcessor.ShopifyReturnReadStart(false, Now.AddDays(-2), watermark, Now));
    }

    [Fact]
    public void TheReadFollowsThePageCursorBelowTheRestartPoint()
    {
        var state = new MarketplaceJobProcessor.ShopifyReturnSyncState("shopify-returns-v1", "C1", Now.AddDays(-180), Now, 10);

        var next = MarketplaceJobProcessor.NextShopifyReturnPage(state, "C2", Now.AddDays(-100));

        Assert.Equal("C2", next.Cursor);
        Assert.Equal(11, next.Pages);
        Assert.Equal(state.ModifiedAfter, next.ModifiedAfter);
        Assert.Equal(state.Anchor, next.Anchor);
    }

    [Fact]
    public void NearShopifys25000ResultCapTheReadStartsANewQueryFromTheLatestOrderChange()
    {
        var seen = Now.AddDays(-40);
        var state = new MarketplaceJobProcessor.ShopifyReturnSyncState("shopify-returns-v1", "C199", Now.AddDays(-180), Now, ShopifyOrderHistoryPolicy.PagesBeforeRestart - 1);

        var next = MarketplaceJobProcessor.NextShopifyReturnPage(state, "C200", seen);

        Assert.Null(next.Cursor);
        Assert.Equal(0, next.Pages);
        Assert.Equal(seen, next.ModifiedAfter);
        // The anchor (when the run began) stays: it becomes the watermark once the run completes.
        Assert.Equal(Now, next.Anchor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutALaterOrderChangeTheReadKeepsPaging(bool sameAsStart)
    {
        var from = Now.AddDays(-180);
        var state = new MarketplaceJobProcessor.ShopifyReturnSyncState("shopify-returns-v1", "C199", from, Now, ShopifyOrderHistoryPolicy.PagesBeforeRestart - 1);

        var next = MarketplaceJobProcessor.NextShopifyReturnPage(state, "C200", sameAsStart ? from : null);

        Assert.Equal("C200", next.Cursor);
        Assert.Equal(from, next.ModifiedAfter);
    }

    [Fact]
    public void ASavedStateFromBeforeThePageCountStillResumes()
    {
        var saved = JsonSerializer.Serialize(new { Version = "shopify-returns-v1", Cursor = "C5", ModifiedAfter = Now.AddDays(-1), Anchor = Now });

        var state = JsonSerializer.Deserialize<MarketplaceJobProcessor.ShopifyReturnSyncState>(saved);

        Assert.NotNull(state);
        Assert.Equal("C5", state.Cursor);
        Assert.Equal(0, state.Pages);
    }
}
