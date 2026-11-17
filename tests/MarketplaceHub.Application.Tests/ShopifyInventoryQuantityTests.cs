using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyInventoryQuantityTests
{
    [Theory]
    [InlineData("""{"quantities":[{"name":"available","quantity":7}]}""", 7)]
    [InlineData("""{"quantities":[{"name":"on_hand","quantity":9},{"name":"available","quantity":4}]}""", 4)]
    [InlineData("""{"quantities":[{"quantity":0}]}""", 0)]
    public void AvailableQuantity_ReadsTheAvailableEntryOfTheList(string level, int expected)
    {
        using var document = JsonDocument.Parse(level);

        Assert.Equal(expected, ShopifyHttpClient.AvailableQuantity(document.RootElement));
    }

    [Theory]
    [InlineData("""{"quantities":[]}""")]
    [InlineData("""{"quantities":[{"name":"available","quantity":null}]}""")]
    [InlineData("""{"quantities":{"name":"available","quantity":3}}""")]
    [InlineData("""{}""")]
    public void AvailableQuantity_IsMissingWhenShopifyGaveNoNumber(string level)
    {
        using var document = JsonDocument.Parse(level);

        Assert.Null(ShopifyHttpClient.AvailableQuantity(document.RootElement));
    }

    // Price and stock address a Shopify variant by the id an import stored (numeric, or a full gid); anything
    // else falls back to the barcode search.
    [Theory]
    [InlineData("44791234567890", "gid://shopify/ProductVariant/44791234567890")]
    [InlineData(" 44791234567890 ", "gid://shopify/ProductVariant/44791234567890")]
    [InlineData("gid://shopify/ProductVariant/12", "gid://shopify/ProductVariant/12")]
    [InlineData("8690000000011-A", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void VariantGid_IsTheStoredVariantIdOrNothing(string? value, string? expected) =>
        Assert.Equal(expected, ShopifyHttpClient.VariantGid(value));


    // ADAPTOR-DIGER-1: a send cut off by a throttle or network error after some lines were applied keeps those lines'
    // results and names the interrupted and later lines NOT_SENT (retryable), so the job records them as not sent and the
    // same values go again. A lasting error (a refused token, a refused request) mid-list keeps them too. A line whose
    // price was written before its stock write was cut off is named SHOPIFY_PRICE_ONLY_APPLIED.
    [Theory]
    [InlineData(AdapterErrorClass.RateLimit)]
    [InlineData(AdapterErrorClass.TransientNetwork)]
    [InlineData(AdapterErrorClass.Remote5xx)]
    [InlineData(AdapterErrorClass.Authentication)]
    [InlineData(AdapterErrorClass.Validation)]
    public async Task AnInterruptedSendKeepsTheLinesAlreadyApplied_AndNamesTheRestNotSent(AdapterErrorClass errorClass)
    {
        var applied = new RemoteOperationLine[] { new("BC-1", true, "gid://shopify/ProductVariant/1", null, false, "COMPLETED"), new("BC-2", false, null, "SHOPIFY_VARIANT_NOT_FOUND", false, "REJECTED") };
        var error = new AdapterError(errorClass, "SHOPIFY_THROTTLED", "Shopify kotası dolu.", 429, TimeSpan.FromSeconds(2), null);
        var priceOnly = new RemoteOperationLine("BC-3", false, "gid://shopify/ProductVariant/3", ShopifyHttpClient.PriceOnlyAppliedCode, true, ShopifyHttpClient.PriceOnlyAppliedStatus);

        var result = ShopifyHttpClient.InterruptedPriceInventory(applied, ["BC-4", "BC-5"], priceOnly, error, null, DateTimeOffset.UnixEpoch);

        Assert.True(result.IsSuccess);
        var operation = await Client().GetOperationAsync(null!, result.Value!.ExternalOperationId, CancellationToken.None);
        Assert.Equal(["BC-1", "BC-2", "BC-3", "BC-4", "BC-5"], operation.Value!.Lines.Select(line => line.ExternalKey).ToArray());
        Assert.Equal("COMPLETED", operation.Value.Status);
        Assert.Equal(ShopifyHttpClient.PriceOnlyAppliedCode, operation.Value.Lines[2].ErrorCode);
        Assert.All(operation.Value.Lines.Skip(3), line => Assert.Equal((false, true, ShopifyHttpClient.NotSentStatus, "SHOPIFY_THROTTLED"), (line.Succeeded, line.Retryable, line.Status, line.ErrorCode)));
    }

    [Fact]
    public async Task APriceWrittenAloneCountsAsApplied()
    {
        var error = new AdapterError(AdapterErrorClass.RateLimit, "SHOPIFY_THROTTLED", "Shopify kotası dolu.", 429, null, null);
        var priceOnly = new RemoteOperationLine("BC-1", false, "gid://shopify/ProductVariant/1", ShopifyHttpClient.PriceOnlyAppliedCode, true, ShopifyHttpClient.PriceOnlyAppliedStatus);

        var result = ShopifyHttpClient.InterruptedPriceInventory([], ["BC-2"], priceOnly, error, null, DateTimeOffset.UnixEpoch);

        Assert.True(result.IsSuccess);
        var operation = await Client().GetOperationAsync(null!, result.Value!.ExternalOperationId, CancellationToken.None);
        Assert.Equal(["BC-1", "BC-2"], operation.Value!.Lines.Select(line => line.ExternalKey).ToArray());
    }

    [Fact]
    public void AnInterruptedSendWithNothingAppliedStillFails()
    {
        var throttled = new AdapterError(AdapterErrorClass.RateLimit, "SHOPIFY_THROTTLED", "Shopify kotası dolu.", 429, null, null);
        var denied = new AdapterError(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Yetki yok.", 401, null, null);
        var rejectedOnly = new RemoteOperationLine[] { new("BC-2", false, null, "SHOPIFY_VARIANT_NOT_FOUND", false, "REJECTED") };

        Assert.Equal("SHOPIFY_THROTTLED", ShopifyHttpClient.InterruptedPriceInventory(rejectedOnly, ["BC-3"], null, throttled, null, DateTimeOffset.UnixEpoch).Error!.Code);
        Assert.Equal("SHOPIFY_CREDENTIAL_INVALID", ShopifyHttpClient.InterruptedPriceInventory(rejectedOnly, [], null, denied, null, DateTimeOffset.UnixEpoch).Error!.Code);
        Assert.Equal("SHOPIFY_CREDENTIAL_INVALID", ShopifyHttpClient.InterruptedPriceInventory([], ["BC-1"], null, denied, null, DateTimeOffset.UnixEpoch).Error!.Code);
    }

    private static ShopifyHttpClient Client() =>
        new(null!, null!, Microsoft.Extensions.Options.Options.Create(new ShopifyOptions()), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ShopifyHttpClient>.Instance, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
}
