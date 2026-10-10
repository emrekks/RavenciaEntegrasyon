using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoicePackageUniquenessModelTests
{
    [Fact]
    public void ActiveFiscalSaleInvoiceIsUniquePerTenantAndPackage()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=invoice-model-only;Username=none;Password=none")
            .Options;
        using var db = new AppDbContext(options);
        var invoice = db.Model.FindEntityType(typeof(Invoice))!;
        var index = Assert.Single(invoice.GetIndexes(), candidate => candidate.IsUnique
            && candidate.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(Invoice.TenantId), nameof(Invoice.PackageId) }));

        Assert.Contains("SequencePurpose", index.GetFilter(), StringComparison.Ordinal);
        Assert.Contains("SALE", index.GetFilter(), StringComparison.Ordinal);
        Assert.Contains("OriginalInvoiceId", index.GetFilter(), StringComparison.Ordinal);
    }
}
