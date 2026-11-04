using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Ürün Hareketleri on PostgreSQL: the list merges the stock ledger, the offer price history and the product change rows
// newest first, leaves out what changed nothing, pages without repeating a row, and filters by kind, platform and
// search; the bulk status change and the panel price edit write their change rows in the same save.
[Collection("postgres-integration")]
public sealed class ProductActivityPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.ProductChangeEntries.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.ChannelPriceHistory.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.StockLedgerEntries.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task The_list_merges_every_source_newest_first_and_skips_what_changed_nothing()
    {
        var seed = await Seed();
        await using var db = fixture.CreateContext();
        var service = new ProductActivityService(db);

        var page = await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(), CancellationToken.None);

        Assert.True(page.Succeeded, page.Error?.Message);
        var items = page.Value!.Items;
        Assert.Equal(["STATUS", "OFFER_PRICE", "OFFER_PRICE", "MANUAL_ADJUSTMENT", "IMPORT_OPENING"], items.Select(x => x.Type).ToArray());
        Assert.Equal(items.OrderByDescending(x => x.OccurredAt).Select(x => x.Id), items.Select(x => x.Id));
        var status = items[0];
        Assert.Equal((ProductActivityKinds.Status, "ACTIVE", "ARCHIVED", ProductChangeSources.BulkEdit, (Guid?)seed.UserId), (status.Kind, status.PreviousStatus, status.NewStatus, status.Source, status.ActorUserId));
        Assert.Equal(seed.ProductTitle, status.ProductTitle);
        // The marketplace's repeat of the same price is left out; its real change shows before → after.
        var marketplacePrice = items[1];
        Assert.Equal((ProductActivitySources.MarketplaceImport, (decimal?)120m, (decimal?)110m, "TRENDYOL"), (marketplacePrice.Source, marketplacePrice.PreviousSalePrice, marketplacePrice.NewSalePrice, marketplacePrice.PlatformCode));
        var userPrice = items[2];
        Assert.Equal((ProductActivitySources.BulkEdit, (decimal?)null, (decimal?)120m, (Guid?)seed.UserId), (userPrice.Source, userPrice.PreviousSalePrice, userPrice.NewSalePrice, userPrice.ActorUserId));
        var manual = items[3];
        Assert.Equal((ProductActivityKinds.Stock, (decimal?)3m, ProductActivitySources.BulkEdit, "Toplu ürün stok düzenleme", seed.Sku), (manual.Kind, manual.QuantityDelta, manual.Source, manual.Note, manual.Sku));
        var opening = items[4];
        Assert.Equal((ProductActivitySources.MarketplaceImport, (decimal?)5m, (Guid?)seed.ConnectionId), (opening.Source, opening.QuantityDelta, opening.ConnectionId));
    }

    [PostgreSqlFact]
    public async Task Pages_follow_each_other_without_repeating_a_row()
    {
        var seed = await Seed();
        await using var db = fixture.CreateContext();
        var service = new ProductActivityService(db);

        var seen = new List<Guid>();
        string? after = null;
        do
        {
            var page = (await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Limit: 2, After: after), CancellationToken.None)).Value!;
            seen.AddRange(page.Items.Select(x => x.Id));
            after = page.HasMore ? page.NextCursor : null;
        } while (after is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(400, (await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(After: "bozuk"), CancellationToken.None)).Error!.Status);
    }

    [PostgreSqlFact]
    public async Task Kind_platform_and_search_filters_narrow_the_list_and_the_counts_ignore_the_kind()
    {
        var seed = await Seed();
        await using var db = fixture.CreateContext();
        var service = new ProductActivityService(db);

        var prices = (await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Kinds: ["PRICE"]), CancellationToken.None)).Value!.Items;
        Assert.All(prices, x => Assert.Equal(ProductActivityKinds.Price, x.Kind));
        Assert.Equal(2, prices.Count);
        // Only rows of a Trendyol connection: the offer prices and the marketplace opening stock.
        var trendyol = (await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Platforms: ["TRENDYOL"]), CancellationToken.None)).Value!.Items;
        Assert.Equal(["OFFER_PRICE", "OFFER_PRICE", "IMPORT_OPENING"], trendyol.Select(x => x.Type).ToArray());
        Assert.Empty((await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Platforms: ["N11"]), CancellationToken.None)).Value!.Items);
        // A search by the variant's code finds its rows and the card-wide status change of its product.
        Assert.Equal(5, (await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Search: seed.Sku), CancellationToken.None)).Value!.Items.Count);
        Assert.Empty((await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Search: "yok-boyle-bir-kod"), CancellationToken.None)).Value!.Items);
        // Another tenant sees nothing.
        Assert.Empty((await service.PageAsync(Guid.CreateVersion7(), new ProductActivityQuery(), CancellationToken.None)).Value!.Items);

        var counts = (await service.CountsAsync(seed.Tenant.Id, new ProductActivityQuery(Kinds: ["STATUS"]), CancellationToken.None)).Value!;
        Assert.Equal([new ProductActivityCount("PRICE", 2), new ProductActivityCount("STATUS", 1), new ProductActivityCount("STOCK", 2)], counts);
    }

    [PostgreSqlFact]
    public async Task The_panel_price_edit_records_its_change_with_the_price_before()
    {
        var seed = await Seed();
        await using var db = fixture.CreateContext();
        var catalog = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
        var variant = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == seed.VariantId);

        var changed = await catalog.UpdateVariantDefaultPricesAsync(seed.Tenant.Id, seed.UserId, variant.Id, variant.Version, new(250m, 199.9m, "Hızlı düzenleme"), "corr-price", CancellationToken.None);

        Assert.True(changed.Succeeded, changed.Error?.Message);
        await using var check = fixture.CreateContext();
        var entry = await check.ProductChangeEntries.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.Kind == ProductChangeKinds.DefaultPrice);
        Assert.Equal(((decimal?)150m, (decimal?)250m, (decimal?)100m, (decimal?)199.9m, ProductChangeSources.PriceEdit, (Guid?)seed.UserId, (Guid?)variant.Id),
            (entry.PreviousListPrice, entry.NewListPrice, entry.PreviousSalePrice, entry.NewSalePrice, entry.Source, entry.ActorUserId, entry.VariantId));
    }

    private sealed record Seeded(Tenant Tenant, Guid ConnectionId, Guid VariantId, string Sku, string ProductTitle, Guid UserId);

    // One Trendyol product: opening stock from the catalog import 4 hours ago, a bulk stock edit 3 hours ago, an order
    // baseline (information only, never listed), the first price a user set 2 hours ago, the marketplace repeating it
    // 90 minutes ago (no change) and lowering it an hour ago, then the bulk status change now.
    private async Task<Seeded> Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.CreateVersion7();
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"activity-{Guid.NewGuid():N}", DisplayName = "activity", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "TRENDYOL",
            Environment = "PRODUCTION",
            DisplayName = "Trendyol",
            ExternalStoreId = $"seller-{Guid.NewGuid():N}",
            Status = "ACTIVE",
            ApiVersion = "V2",
            SettingsJson = "{}"
        };
        var title = $"Hareket Bluz {tag}";
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = title, Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var sku = $"ACT-{tag}";
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, OptionSignature = "Renk: Siyah | Beden: M", DefaultListPrice = 150m, DefaultSalePrice = 100m, CreatedAt = now, UpdatedAt = now };
        var inventory = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 8m, Available = 8m };
        var offer = new ChannelOffer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            VariantId = variant.Id,
            ListPrice = 150m,
            SalePrice = 110m,
            Currency = "TRY",
            VatRate = 20m,
            VatInclusion = "INCLUDED",
            RoundingMode = "HALF_EVEN",
            Status = "ACTIVE",
            PriceVersion = 3
        };
        StockLedgerEntry Ledger(string type, decimal delta, string sourceType, string sourceId, TimeSpan ago, Guid? actor = null) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            InventoryItemId = inventory.Id,
            MovementType = type,
            QuantityDelta = delta,
            SourceType = sourceType,
            SourceId = sourceId,
            SourceEventId = $"{type}:{Guid.NewGuid():N}",
            IdempotencyKey = $"{type}:{Guid.NewGuid():N}",
            OccurredAt = now - ago,
            RecordedAt = now - ago,
            ActorUserId = actor,
            CorrelationId = "seed"
        };
        ChannelPriceHistory Price(long version, decimal sale, string actor, string reason, TimeSpan ago) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            OfferId = offer.Id,
            PriceVersion = version,
            ListPrice = 150m,
            SalePrice = sale,
            Currency = "TRY",
            Reason = reason,
            ActorSource = actor,
            EffectiveAt = now - ago
        };
        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.PlatformConnections.Add(connection);
            db.Products.Add(product);
            db.ProductVariants.Add(variant);
            db.InventoryItems.Add(inventory);
            db.ChannelOffers.Add(offer);
            await db.SaveChangesAsync();
            db.StockLedgerEntries.AddRange(
                Ledger(ProductActivityTypes.ImportOpening, 5m, "MARKETPLACE_CATALOG", connection.Id.ToString("D"), TimeSpan.FromHours(4)),
                Ledger(ProductActivityTypes.ManualAdjustment, 3m, "MANUAL", "Toplu ürün stok düzenleme", TimeSpan.FromHours(3), userId),
                Ledger("ORDER_STOCK_BASELINE", 1m, "ORDER_LINE", Guid.NewGuid().ToString("D"), TimeSpan.FromMinutes(150)));
            db.ChannelPriceHistory.AddRange(
                Price(1, 120m, $"USER:{userId:D}", "Toplu ürün fiyat düzenleme", TimeSpan.FromHours(2)),
                Price(2, 120m, "SYSTEM:TRENDYOL", "TRENDYOL_CATALOG_IMPORT", TimeSpan.FromMinutes(90)),
                Price(3, 110m, "SYSTEM:TRENDYOL", "TRENDYOL_CATALOG_IMPORT", TimeSpan.FromHours(1)));
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var catalog = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
            var archived = await catalog.BulkSetStatusAsync(tenant.Id, new BulkProductStatusCommand([product.Id], "ARCHIVED"), CancellationToken.None, userId, "corr-status");
            Assert.True(archived.Succeeded, archived.Error?.Message);
        }
        return new(tenant, connection.Id, variant.Id, sku, title, userId);
    }
}
