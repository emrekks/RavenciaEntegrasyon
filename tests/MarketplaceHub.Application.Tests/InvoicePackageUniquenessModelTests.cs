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

    [Fact]
    public void GuardedFiscalSaleInvoiceIsUniquePerTenantAndOrder()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=invoice-model-only;Username=none;Password=none")
            .Options;
        using var db = new AppDbContext(options);
        var invoice = db.Model.FindEntityType(typeof(Invoice))!;
        var index = Assert.Single(invoice.GetIndexes(), candidate => candidate.IsUnique
            && candidate.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(Invoice.TenantId), nameof(Invoice.OrderId) }));

        Assert.Equal("IX_invoices_TenantId_OrderId_ActiveSaleFiscal", index.GetDatabaseName());
        Assert.Contains("OrderFiscalGuarded", index.GetFilter(), StringComparison.Ordinal);
        Assert.Contains("SALE", index.GetFilter(), StringComparison.Ordinal);
    }

    [Fact]
    public void MarketplaceDeliveryStateIsUniquePerInvoiceAndTargetPackage()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=invoice-model-only;Username=none;Password=none")
            .Options;
        using var db = new AppDbContext(options);
        var state = db.Model.FindEntityType(typeof(MarketplaceDeliveryState))!;
        var index = Assert.Single(state.GetIndexes(), candidate => candidate.IsUnique
            && candidate.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(MarketplaceDeliveryState.TenantId),
                nameof(MarketplaceDeliveryState.InvoiceId),
                nameof(MarketplaceDeliveryState.PackageId)
            }));

        Assert.Equal("IX_marketplace_delivery_states_TenantId_InvoiceId_PackageId", index.GetDatabaseName());
    }
}
