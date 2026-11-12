using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// STOCK_ORDER_CONSUMPTION_BLOCKED asks the owner to check the stock count; the missing units are never taken later, so
// before this the notice stayed open after the count was fixed by hand and only a cancellation closed it. A stock
// count set by hand now closes the open notices of that stock item, and leaves another item's notice open.
[Collection("postgres-integration")]
public sealed class StockShortfallNoticePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task SetOnHand_ClosesTheShortfallNoticesOfThatItemOnly()
    {
        var (tenantId, variantId, itemId, lineId) = await SeedAsync("shortfall-set");
        var (_, _, otherItemId, otherLineId) = await SeedAsync("shortfall-other", tenantId);
        await using var db = fixture.CreateContext();
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());

        var result = await service.SetOnHandAsync(tenantId, Guid.CreateVersion7(), variantId, new StockSetCommand(7m, 0m, "Sayım", "shortfall-set"), $"shortfall-set-{Guid.NewGuid():N}", "shortfall", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        await using var read = fixture.CreateContext();
        var issues = await read.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.DedupeKey);
        Assert.Equal(IssueStatus.Resolved, issues[StockShortfallNotices.DedupeKey(lineId)].Status);
        Assert.NotNull(issues[StockShortfallNotices.DedupeKey(lineId)].ResolvedAt);
        Assert.Equal(IssueStatus.Open, issues[StockShortfallNotices.DedupeKey(otherLineId)].Status);
        Assert.NotEqual(itemId, otherItemId);
    }

    [PostgreSqlFact]
    public async Task Adjust_ClosesTheShortfallNotice()
    {
        var (tenantId, variantId, _, lineId) = await SeedAsync("shortfall-adjust");
        await using var db = fixture.CreateContext();
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());

        var result = await service.AdjustAsync(tenantId, Guid.CreateVersion7(), variantId, new StockAdjustmentCommand(3m, "Sayım", "shortfall-adjust"), $"shortfall-adjust-{Guid.NewGuid():N}", "shortfall", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        await using var read = fixture.CreateContext();
        Assert.Equal(IssueStatus.Resolved, (await read.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.DedupeKey == StockShortfallNotices.DedupeKey(lineId))).Status);
    }

    // A stock item at 0 whose order line could not take 2 units: the shortfall marker row and its open notice.
    private async Task<(Guid TenantId, Guid VariantId, Guid ItemId, Guid LineId)> SeedAsync(string code, Guid? existingTenantId = null)
    {
        var now = DateTimeOffset.UtcNow;
        await using var seed = fixture.CreateContext();
        var tenantId = existingTenantId ?? Guid.CreateVersion7();
        if (existingTenantId is null)
            seed.Tenants.Add(new Tenant { Id = tenantId, Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 });
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = $"Ürün {code}", Description = "Stok testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var sku = $"SF-{Guid.NewGuid():N}"[..14];
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), OptionSignature = sku, CreatedAt = now, UpdatedAt = now };
        var item = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenantId, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 0m, Available = 0m };
        var lineId = Guid.CreateVersion7();
        seed.Products.Add(product); seed.ProductVariants.Add(variant); seed.InventoryItems.Add(item);
        seed.StockLedgerEntries.Add(new StockLedgerEntry
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            InventoryItemId = item.Id,
            MovementType = OrderInventoryStockPolicy.ShortfallMovementType,
            QuantityDelta = 2m,
            SourceType = "ORDER_LINE",
            SourceId = lineId.ToString("D"),
            SourceEventId = $"shortfall:{lineId:N}",
            IdempotencyKey = $"order-stock-shortfall:{item.Id:N}:{lineId:N}",
            OccurredAt = now,
            RecordedAt = now,
            CorrelationId = $"order:{Guid.NewGuid():N}"
        });
        seed.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = StockShortfallNotices.DedupeKey(lineId), Code = StockShortfallNotices.IssueCode, Summary = "2 adet stoktan düşülemedi.", Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
        await seed.SaveChangesAsync();
        return (tenantId, variant.Id, item.Id, lineId);
    }
}
