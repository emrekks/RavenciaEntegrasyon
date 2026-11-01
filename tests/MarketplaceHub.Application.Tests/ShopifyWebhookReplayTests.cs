using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// G4: the unsigned delivery headers cannot be varied to replay a captured body past the deduplication.
public sealed class ShopifyWebhookReplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSameBodyAndTopicKeepTheirIdWhateverTheDeliveryHeadersSay()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":123,"updated_at":"2026-10-21T09:00:00Z"}""");

        Assert.Equal(ShopifyWebhookVerifier.MessageId("orders/updated", body), ShopifyWebhookVerifier.MessageId(" Orders/Updated ", body));
        Assert.NotEqual(ShopifyWebhookVerifier.MessageId("orders/updated", body), ShopifyWebhookVerifier.MessageId("orders/updated", Encoding.UTF8.GetBytes("""{"id":123,"updated_at":"2026-10-21T09:01:00Z"}""")));
        Assert.NotEqual(ShopifyWebhookVerifier.MessageId("orders/updated", body), ShopifyWebhookVerifier.MessageId("orders/create", body));
    }

    [Fact]
    public void AnUnknownTopicCannotMintNewIds()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":123}""");

        Assert.Equal(ShopifyWebhookVerifier.MessageId("made/up-1", body), ShopifyWebhookVerifier.MessageId("made/up-2", body));
        Assert.Equal(ShopifyWebhookVerifier.MessageId(null, body), ShopifyWebhookVerifier.MessageId("", body));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("not a time", true)]
    [InlineData("2026-10-21T08:56:00Z", true)]
    [InlineData("2026-10-21T09:04:30.877041743Z", true)]
    [InlineData("2026-10-21T08:54:00Z", false)]
    [InlineData("2026-10-21T08:50:00.877041743Z", false)]
    [InlineData("2026-10-21T09:06:00Z", false)]
    [InlineData("2026-10-21T11:58:00+03:00", true)]
    public void ATriggerTimeIsAcceptedOnlyWithinFiveMinutes(string? triggeredAt, bool fresh)
    {
        Assert.Equal(fresh, ShopifyWebhookVerifier.IsFresh(triggeredAt, Now));
    }

    [Theory]
    [InlineData("products/delete", "PRODUCTS_DELETE")]
    [InlineData(" Products/Delete ", "PRODUCTS_DELETE")]
    [InlineData("refunds/create", "REFUNDS")]
    [InlineData("orders/updated", "ORDERS")]
    public void ADeletedProductIsItsOwnResourceAndKeepsItsOwnId(string topic, string resourceType)
    {
        var body = Encoding.UTF8.GetBytes("""{"id":788032119674292922}""");

        Assert.Equal(resourceType, ShopifyWebhookVerifier.ResourceType(topic));
        Assert.NotEqual(ShopifyWebhookVerifier.MessageId("products/delete", body), ShopifyWebhookVerifier.MessageId("orders/updated", body));
        Assert.StartsWith("products/delete:", ShopifyWebhookVerifier.MessageId("products/delete", body));
    }

    [Fact]
    public void TheUpkeepAddsOnlyTheMissingProductDeleteTopicAtThePanelsAddress()
    {
        const string url = "https://panel.example.com/webhooks/shopify/route";
        string[] orderTopics = ["ORDERS_CREATE", "ORDERS_UPDATED", "ORDERS_CANCELLED", "ORDERS_FULFILLED", "ORDERS_PARTIALLY_FULFILLED", "REFUNDS_CREATE"];
        var listed = orderTopics.Select((topic, index) => new RemoteWebhook((index + 1).ToString(), url, "ACTIVE", null, topic))
            .Append(new RemoteWebhook("99", "https://other.example.com/hook", "ACTIVE", null, "PRODUCTS_DELETE"))
            .ToList();

        var plan = ShopifyHttpClient.PlanWebhookRestore("1,2,3,4,5,6", listed);

        Assert.True(plan.Changed);
        Assert.Equal(url, plan.Url);
        Assert.Equal(["1", "2", "3", "4", "5", "6"], plan.KeptIds);
        Assert.Equal([ShopifyHttpClient.ProductDeleteTopic], plan.MissingTopics);
    }

    [Fact]
    public void ASubscriptionAlreadyAtTheAddressIsAdoptedNotMadeTwiceAndACompleteSetChangesNothing()
    {
        const string url = "https://panel.example.com/webhooks/shopify/route";
        var listed = ShopifyHttpClient.WebhookTopics.Select((topic, index) => new RemoteWebhook((index + 1).ToString(), url, "ACTIVE", null, topic)).ToList();

        var adopting = ShopifyHttpClient.PlanWebhookRestore("1,2,3,4,5,6", listed);
        var complete = ShopifyHttpClient.PlanWebhookRestore("1,2,3,4,5,6,7", listed);

        Assert.True(adopting.Changed);
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7"], adopting.KeptIds);
        Assert.Empty(adopting.MissingTopics);
        Assert.False(complete.Changed);
    }
}
