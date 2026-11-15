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
    // results; the interrupted and later lines are left out (the job records them as unconfirmed and sends them again).
    [Theory]
    [InlineData(AdapterErrorClass.RateLimit)]
    [InlineData(AdapterErrorClass.TransientNetwork)]
    [InlineData(AdapterErrorClass.Remote5xx)]
    public async Task AnInterruptedSendKeepsTheLinesAlreadyApplied(AdapterErrorClass errorClass)
    {
        var applied = new RemoteOperationLine[] { new("BC-1", true, "gid://shopify/ProductVariant/1", null, false, "COMPLETED"), new("BC-2", false, null, "SHOPIFY_VARIANT_NOT_FOUND", false, "REJECTED") };
        var error = new AdapterError(errorClass, "SHOPIFY_THROTTLED", "Shopify kotası dolu.", 429, TimeSpan.FromSeconds(2), null);

        var result = ShopifyHttpClient.InterruptedPriceInventory(applied, error, null, DateTimeOffset.UnixEpoch);

        Assert.True(result.IsSuccess);
        Assert.StartsWith("shopify-completed:", result.Value!.ExternalOperationId, StringComparison.Ordinal);
        var client = new ShopifyHttpClient(null!, null!, Microsoft.Extensions.Options.Options.Create(new ShopifyOptions()), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ShopifyHttpClient>.Instance, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var operation = await client.GetOperationAsync(null!, result.Value.ExternalOperationId, CancellationToken.None);
        Assert.Equal(["BC-1", "BC-2"], operation.Value!.Lines.Select(line => line.ExternalKey).ToArray());
        Assert.Equal("COMPLETED", operation.Value.Status);
    }

    [Fact]
    public void AnInterruptedSendWithNothingAppliedOrALastingErrorStillFails()
    {
        var throttled = new AdapterError(AdapterErrorClass.RateLimit, "SHOPIFY_THROTTLED", "Shopify kotası dolu.", 429, null, null);
        var denied = new AdapterError(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Yetki yok.", 401, null, null);
        var rejectedOnly = new RemoteOperationLine[] { new("BC-2", false, null, "SHOPIFY_VARIANT_NOT_FOUND", false, "REJECTED") };
        var applied = new RemoteOperationLine[] { new("BC-1", true, "gid://shopify/ProductVariant/1", null, false, "COMPLETED") };

        Assert.Equal("SHOPIFY_THROTTLED", ShopifyHttpClient.InterruptedPriceInventory(rejectedOnly, throttled, null, DateTimeOffset.UnixEpoch).Error!.Code);
        Assert.Equal("SHOPIFY_CREDENTIAL_INVALID", ShopifyHttpClient.InterruptedPriceInventory(applied, denied, null, DateTimeOffset.UnixEpoch).Error!.Code);
    }
}
