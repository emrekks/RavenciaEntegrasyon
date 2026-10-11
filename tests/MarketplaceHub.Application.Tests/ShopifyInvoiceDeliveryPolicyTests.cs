using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
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

    [Fact]
    public void OrderInvoiceMetafieldsAreReconciledForEveryPackageAndRemovalClearsThem()
    {
        var now = DateTimeOffset.UtcNow;
        var order = new RemoteOrder(
            "gid://shopify/Order/123", "#123", now, now, "TRY", 100, 0, 100,
            "{}", "{}", "{}", [],
            [
                new RemotePackage("gid://shopify/Fulfillment/1", null, "DELIVERED", now, null, null, []),
                new RemotePackage("order:123:remainder", null, "UNFULFILLED", now, null, null, [])
            ], "{}");

        var invoiced = ShopifyHttpClient.AttachInvoiceMetafields(order, "INV-1", "https://files.example/invoice.pdf", now);
        Assert.All(invoiced.Packages, package =>
        {
            Assert.Equal("INVOICED", package.Invoice?.RawStatus);
            Assert.Equal("INV-1", package.Invoice?.InvoiceNumber);
            Assert.Equal("https://files.example/invoice.pdf", package.Invoice?.InvoiceUrl);
        });

        var removed = ShopifyHttpClient.AttachInvoiceMetafields(invoiced, null, null, now.AddMinutes(1));
        Assert.All(removed.Packages, package =>
        {
            Assert.Equal("NOT_INVOICED", package.Invoice?.RawStatus);
            Assert.Null(package.Invoice?.InvoiceNumber);
            Assert.Null(package.Invoice?.InvoiceUrl);
        });
    }

    [Theory]
    [InlineData("INV-1", null, "RECEIVED")]
    [InlineData(null, "https://files.example/invoice.pdf", "RECEIVED")]
    [InlineData(null, null, "NOT_INVOICED")]
    public void PartialOrRemovedMetafieldsDoNotCountAsACompleteShopifyInvoice(string? invoiceNumber, string? invoiceUrl, string expectedStatus)
    {
        var now = DateTimeOffset.UtcNow;
        var order = new RemoteOrder(
            "gid://shopify/Order/123", "#123", now, now, "TRY", 100, 0, 100,
            "{}", "{}", "{}", [],
            [new RemotePackage("gid://shopify/Fulfillment/1", null, "DELIVERED", now, null, null, [])], "{}");

        var observed = ShopifyHttpClient.AttachInvoiceMetafields(order, invoiceNumber, invoiceUrl, now);
        var invoice = Assert.Single(observed.Packages).Invoice;

        Assert.Equal(expectedStatus, invoice?.RawStatus);
        Assert.True(invoice?.ReferencesAreAuthoritative);
        Assert.Equal(invoiceNumber, invoice?.InvoiceNumber);
        Assert.Equal(invoiceUrl, invoice?.InvoiceUrl);
    }
}
