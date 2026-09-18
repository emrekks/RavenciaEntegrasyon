using MarketplaceHub.Api.Marketplace;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class WebhookBodyReaderTests
{
    [Fact]
    public async Task ContentLengthAboveLimitIsRejectedBeforeReading()
    {
        await using var body = new MemoryStream([1, 2, 3]);

        var result = await WebhookBodyReader.ReadAsync(body, WebhookBodyReader.MaximumBytes + 1L, CancellationToken.None);

        Assert.True(result.TooLarge);
        Assert.Null(result.Body);
        Assert.Equal(0, body.Position);
    }

    [Fact]
    public async Task ChunkedBodyAboveLimitIsRejectedAndBufferIsReturned()
    {
        await using var body = new MemoryStream(new byte[WebhookBodyReader.MaximumBytes + 1]);

        var result = await WebhookBodyReader.ReadAsync(body, null, CancellationToken.None);

        Assert.True(result.TooLarge);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task ExactLimitIsAcceptedAndReturnedMemoryHasExactLength()
    {
        await using var body = new MemoryStream(new byte[WebhookBodyReader.MaximumBytes]);

        var result = await WebhookBodyReader.ReadAsync(body, WebhookBodyReader.MaximumBytes, CancellationToken.None);

        Assert.False(result.TooLarge);
        Assert.NotNull(result.Body);
        Assert.Equal(WebhookBodyReader.MaximumBytes, result.Body.Memory.Length);
        result.Body.Dispose();
    }
}
