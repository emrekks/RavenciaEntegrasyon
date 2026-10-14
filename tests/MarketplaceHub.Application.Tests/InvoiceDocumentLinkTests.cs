using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceDocumentLinkTests
{
    [Fact]
    public void LinkBindsTenantInvoiceAndDocumentAndRejectsTampering()
    {
        var protection = new EphemeralDataProtectionProvider();
        var tenant = Guid.NewGuid(); var invoice = Guid.NewGuid(); var document = Guid.NewGuid();
        var url = InvoiceDocumentLink.Create(protection, "https://panel.example.com", tenant, invoice, document);
        var token = new Uri(url).Segments[^2].TrimEnd('/');
        Assert.True(InvoiceDocumentLink.TryRead(protection, token, out var actualTenant, out var actualInvoice, out var actualDocument));
        Assert.Equal(tenant, actualTenant); Assert.Equal(invoice, actualInvoice); Assert.Equal(document, actualDocument);
        Assert.False(InvoiceDocumentLink.TryRead(protection, "x" + token[1..], out _, out _, out _));
        Assert.False(InvoiceDocumentLink.TryRead(new EphemeralDataProtectionProvider(), token, out _, out _, out _));
        Assert.False(InvoiceDocumentLink.TryRead(protection, "invalid", out _, out _, out _));
    }

    [Theory]
    [InlineData("http://panel.example.com")]
    [InlineData("https://localhost")]
    [InlineData("https://panel.example.com/path")]
    public void InvalidOriginCannotPublishDocument(string origin) =>
        Assert.Throws<ArgumentException>(() => InvoiceDocumentLink.Create(new EphemeralDataProtectionProvider(), origin, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
}
