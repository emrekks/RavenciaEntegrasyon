using System.Data.Common;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// K6 (audit 2026-10-24): a manual stock write runs SERIALIZABLE and the worker changes the same stock row for orders and
// reservations. When the worker's change lands between the panel's read and its write, PostgreSQL aborts the panel's
// write with 40001. The write is run again on fresh values instead of answering 500, and when it keeps colliding the
// panel gets 409 STOCK_CHANGED ("stok değişti, yenileyin") with nothing written.
[Collection("postgres-integration")]
public sealed class InventorySerializableRetryPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];
    private static string ConnectionString => Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task SetOnHand_WhenAConcurrentWorkerWriteAbortsTheFirstAttempt_IsRetriedAndSaved()
    {
        var (tenant, variant, item) = await SeedAsync("k6-set-retry", 5m);
        var collide = new ConcurrentStockWrite(item.Id, times: 1);
        await using var db = CreateContext(collide);
        var service = NewService(db);

        var result = await service.SetOnHandAsync(tenant.Id, Guid.CreateVersion7(), variant.Id, new StockSetCommand(9m, 5m, "Hızlı stok", "k6-set"), $"k6-set-{Guid.NewGuid():N}", "k6", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(1, collide.Fired);
        await using var read = fixture.CreateContext();
        var stored = await read.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == item.Id);
        Assert.Equal(9m, stored.OnHand);
        Assert.NotNull(stored.ObservedRemoteAt);
        Assert.Equal(4m, (await read.StockLedgerEntries.AsNoTracking().SingleAsync(x => x.InventoryItemId == item.Id)).QuantityDelta);
    }

    [PostgreSqlFact]
    public async Task Adjust_WhenAConcurrentWorkerWriteAbortsTheFirstAttempt_IsRetriedAndSaved()
    {
        var (tenant, variant, item) = await SeedAsync("k6-adjust-retry", 5m);
        var collide = new ConcurrentStockWrite(item.Id, times: 1);
        await using var db = CreateContext(collide);
        var service = NewService(db);

        var result = await service.AdjustAsync(tenant.Id, Guid.CreateVersion7(), variant.Id, new StockAdjustmentCommand(-2m, "Sayım", "k6-adjust"), $"k6-adjust-{Guid.NewGuid():N}", "k6", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(1, collide.Fired);
        await using var read = fixture.CreateContext();
        Assert.Equal(3m, (await read.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == item.Id)).OnHand);
        Assert.Single(await read.StockLedgerEntries.AsNoTracking().Where(x => x.InventoryItemId == item.Id).ToListAsync());
    }

    [PostgreSqlFact]
    public async Task SetOnHand_WhenEveryAttemptCollides_AnswersStockChangedAndWritesNothing()
    {
        var (tenant, variant, item) = await SeedAsync("k6-set-exhausted", 5m);
        var collide = new ConcurrentStockWrite(item.Id, times: int.MaxValue);
        await using var db = CreateContext(collide);
        var service = NewService(db);

        var result = await service.SetOnHandAsync(tenant.Id, Guid.CreateVersion7(), variant.Id, new StockSetCommand(9m, 5m, "Hızlı stok", "k6-exhausted"), $"k6-exhausted-{Guid.NewGuid():N}", "k6", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.Error?.Status);
        Assert.Equal("STOCK_CHANGED", result.Error?.Code);
        Assert.True(collide.Fired > 1);
        await using var read = fixture.CreateContext();
        Assert.Equal(5m, (await read.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == item.Id)).OnHand);
        Assert.False(await read.StockLedgerEntries.AsNoTracking().AnyAsync(x => x.InventoryItemId == item.Id));
    }

    private async Task<(Tenant Tenant, ProductVariant Variant, InventoryItem Item)> SeedAsync(string code, decimal onHand)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {code}", Description = "Stok testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var sku = $"K6-{Guid.NewGuid():N}"[..14];
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), OptionSignature = sku, CreatedAt = now, UpdatedAt = now };
        var item = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = onHand, Available = onHand };
        await using var seed = fixture.CreateContext();
        seed.Tenants.Add(tenant); seed.Products.Add(product); seed.ProductVariants.Add(variant); seed.InventoryItems.Add(item);
        await seed.SaveChangesAsync();
        return (tenant, variant, item);
    }

    private static AppDbContext CreateContext(IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).AddInterceptors(interceptor).Options);

    private InventoryService NewService(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());

    // Plays the worker: right before the panel's transaction writes the stock row, another connection changes that row
    // (a remote stock observation, not the on-hand) and commits, so the panel's older snapshot can no longer write it.
    private sealed class ConcurrentStockWrite(Guid inventoryItemId, int times) : DbCommandInterceptor
    {
        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await CollideAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await CollideAsync(command, cancellationToken);
            return result;
        }

        private async Task CollideAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (Fired >= times || !command.CommandText.Contains("UPDATE inventory.inventory_items", StringComparison.OrdinalIgnoreCase)) return;
            Fired++;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var update = new NpgsqlCommand("UPDATE inventory.inventory_items SET \"ObservedRemoteAt\" = now() WHERE \"Id\" = @id", connection);
            update.Parameters.AddWithValue("id", inventoryItemId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
