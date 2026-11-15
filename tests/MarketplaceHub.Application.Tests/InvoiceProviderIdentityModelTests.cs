using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// VERITABANI-DAGITIM-11: one official invoice (ETTN, or the issuer's invoice number) belongs to one invoice row.
public sealed class InvoiceProviderIdentityModelTests
{
    [Fact]
    public void ProviderInvoiceIdentityIsUnique()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=schema-model-only;Username=none;Password=none").Options);
        var invoice = db.Model.FindEntityType(typeof(Invoice))!;
        var ettn = Assert.Single(invoice.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(Invoice.TenantId), nameof(Invoice.EttnUuid) }));
        var number = Assert.Single(invoice.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(Invoice.TenantId), nameof(Invoice.LegalEntityProfileId), nameof(Invoice.InvoiceNumber) }));

        Assert.Equal("UX_invoices_TenantId_EttnUuid", ettn.GetDatabaseName());
        Assert.Equal("\"EttnUuid\" IS NOT NULL", ettn.GetFilter());
        Assert.Equal("UX_invoices_TenantId_LegalEntityProfileId_InvoiceNumber", number.GetDatabaseName());
        Assert.Equal("\"InvoiceNumber\" IS NOT NULL", number.GetFilter());
    }
}
