using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// VERITABANI-DAGITIM-16: deleting a connection row never takes its finance history with it.
public sealed class FinanceHistoryForeignKeyModelTests
{
    [Theory]
    [InlineData(typeof(MarketplaceFinanceTransaction))]
    [InlineData(typeof(MarketplacePayoutOrder))]
    [InlineData(typeof(MarketplaceCargoInvoiceItem))]
    // The optional part (20261116090100_RestrictConnectionChildCascades): no foreign key to a connection cascades any more;
    // every path that deletes a connection row deletes these first.
    [InlineData(typeof(CommissionTariff))]
    [InlineData(typeof(PromotionOfferFile))]
    [InlineData(typeof(AdReportFile))]
    [InlineData(typeof(MarketplaceFinanceSyncState))]
    [InlineData(typeof(MarketplaceQuestionSyncState))]
    public void FinanceHistoryRestrictsConnectionDelete(Type entityType)
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=schema-model-only;Username=none;Password=none").Options);
        var connectionKey = Assert.Single(db.Model.FindEntityType(entityType)!.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(PlatformConnection));

        Assert.Equal(DeleteBehavior.Restrict, connectionKey.DeleteBehavior);
    }

    [Fact]
    public void NoForeignKeyToAConnectionCascades()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=schema-model-only;Username=none;Password=none").Options);
        var cascading = db.Model.GetEntityTypes().SelectMany(type => type.GetForeignKeys())
            .Where(key => key.PrincipalEntityType.ClrType == typeof(PlatformConnection) && key.DeleteBehavior == DeleteBehavior.Cascade)
            .Select(key => key.DeclaringEntityType.ClrType.Name).ToList();
        Assert.Empty(cascading);
    }
}
