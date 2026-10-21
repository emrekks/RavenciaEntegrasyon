using System.Text;
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
}
