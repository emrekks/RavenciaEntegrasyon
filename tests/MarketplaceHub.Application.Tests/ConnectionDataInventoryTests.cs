using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Every table that keeps a connection's rows is either removed by a full reset or kept as a setting: a new table with a
// connection column fails here until it is placed (P-6, 30 Oct 2026).
public sealed class ConnectionDataInventoryTests
{
    private static List<string> ConnectionTables()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model-only").Options);
        return db.Model.GetEntityTypes()
            .Where(entity => entity.ClrType != typeof(MarketplaceHub.Domain.PlatformConnection) && entity.GetTableName() is not null)
            .Where(entity => entity.GetProperties().Any(property => property.Name.EndsWith("ConnectionId", StringComparison.Ordinal)))
            .Select(entity => $"{entity.GetSchema()}.{entity.GetTableName()}")
            .Distinct()
            .Order()
            .ToList();
    }

    [Fact]
    public void EveryTableWithAConnectionColumn_IsRemovedOrKept()
    {
        var unplaced = ConnectionTables().Where(table => !ConnectionDataInventory.Removed.Contains(table) && !ConnectionDataInventory.Kept.Contains(table)).ToList();
        Assert.Empty(unplaced);
    }

    [Fact]
    public void NoTableIsBothRemovedAndKept_AndEveryListedTableExists()
    {
        Assert.All(ConnectionDataInventory.Removed, table => Assert.DoesNotContain(table, ConnectionDataInventory.Kept));
        // The backoff table is written with SQL only; every other listed table is mapped.
        var mapped = ConnectionTables().Append("integration.connection_backoffs").ToHashSet();
        Assert.All(ConnectionDataInventory.Removed.Concat(ConnectionDataInventory.Kept), table => Assert.Contains(table, mapped));
    }

    [Fact]
    public void TheResetHoldsEverySyncLaneOfTheConnection()
    {
        Assert.Contains("orders", OperationalDataMaintenanceService.SyncLockGroups);
        Assert.Contains("returns", OperationalDataMaintenanceService.SyncLockGroups);
        Assert.Contains("products", OperationalDataMaintenanceService.SyncLockGroups);
        Assert.Contains("references", OperationalDataMaintenanceService.SyncLockGroups);
        Assert.Contains("finance", OperationalDataMaintenanceService.SyncLockGroups);
        Assert.Contains("connection", OperationalDataMaintenanceService.SyncLockGroups);
        Assert.Equal(MarketplaceHub.Application.MarketplaceJobTypes.ProcessorJobTypes.Select(MarketplaceSyncExecutionLock.GroupFor).Distinct().Order(), OperationalDataMaintenanceService.SyncLockGroups.Order());
    }
}
