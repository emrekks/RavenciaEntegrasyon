using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyInvoiceDeliveryPolicyTests
{
    [Fact]
    public void DeliveryPayloadRequiresShopifyOrderGidInvoiceNumberAndHttpsLink()
    {
        const string valid = "{\"shopifyOrderId\":\"gid://shopify/Order/123\",\"invoiceNumber\":\"INV-1\",\"invoiceLink\":\"https://files.example/invoice.pdf\"}";
        Assert.True(ShopifyHttpClient.IsValidInvoiceDeliveryPayload(valid));
        Assert.False(ShopifyHttpClient.IsValidInvoiceDeliveryPayload(valid.Replace("https://", "http://", StringComparison.Ordinal)));
        Assert.False(ShopifyHttpClient.IsValidInvoiceDeliveryPayload(valid.Replace("gid://shopify/Order/123", "123", StringComparison.Ordinal)));
        Assert.False(ShopifyHttpClient.IsValidInvoiceDeliveryPayload(valid.Replace("INV-1", " ", StringComparison.Ordinal)));
    }

    [Fact]
    public void ExistingInvoiceValuesMustMatchExactlyToCountAsDelivered()
    {
        Assert.True(ShopifyHttpClient.AreShopifyInvoiceMetafieldsMatching("INV-1", "https://files.example/invoice.pdf", "INV-1", "https://files.example/invoice.pdf"));
        Assert.False(ShopifyHttpClient.AreShopifyInvoiceMetafieldsMatching("INV-2", "https://files.example/invoice.pdf", "INV-1", "https://files.example/invoice.pdf"));
        Assert.False(ShopifyHttpClient.AreShopifyInvoiceMetafieldsMatching("INV-1", null, "INV-1", "https://files.example/invoice.pdf"));
    }

    [Fact]
    public void MetafieldWritesIncludeCompareDigestForCreateAndCompareAndSet()
    {
        var createInputs = JsonSerializer.SerializeToElement(ShopifyHttpClient.BuildInvoiceMetafieldSetInputs("gid://shopify/Order/123", "INV-1", "https://files.example/invoice.pdf", null, null));
        Assert.Equal("ravencia", createInputs[0].GetProperty("namespace").GetString());
        Assert.Equal("invoice_pdf_url", createInputs[0].GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Null, createInputs[0].GetProperty("compareDigest").ValueKind);

        var updateInputs = JsonSerializer.SerializeToElement(ShopifyHttpClient.BuildInvoiceMetafieldSetInputs("gid://shopify/Order/123", "INV-1", "https://files.example/invoice.pdf", "pdf-digest", "number-digest"));
        Assert.Equal("pdf-digest", updateInputs[0].GetProperty("compareDigest").GetString());
        Assert.Equal("number-digest", updateInputs[1].GetProperty("compareDigest").GetString());
    }
}
