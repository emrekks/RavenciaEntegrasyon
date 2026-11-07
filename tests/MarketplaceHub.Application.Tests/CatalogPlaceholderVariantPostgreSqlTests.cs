using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 6 Nov 2026: a card saved as a draft before its variants were generated holds an option-less "Tek Ürün"
// variant. When the editor replaces it with generated variants, the save removes it only when nothing is tied to it;
// otherwise it stays and the save still succeeds.
[Collection("postgres-integration")]
public sealed class CatalogPlaceholderVariantPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Save_RemovesAnUntiedPlaceholderNextToOptionedVariants()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var seed = await SeedAsync(db, "ph-free");

        var saved = await Save(db, seed, [seed.Placeholder.Id]);

        Assert.True(saved.Succeeded, saved.Error?.Message);
        Assert.DoesNotContain(saved.Value!.Variants, variant => variant.Id == seed.Placeholder.Id);
        db.ChangeTracker.Clear();
        Assert.False(await db.ProductVariants.AsNoTracking().AnyAsync(x => x.Id == seed.Placeholder.Id));
        Assert.False(await db.InventoryItems.AsNoTracking().AnyAsync(x => x.VariantId == seed.Placeholder.Id));
        Assert.Equal(2, await db.ProductVariants.AsNoTracking().CountAsync(x => x.ProductId == seed.Product.Id));
        await transaction.RollbackAsync();
    }

    [PostgreSqlTheory]
    [InlineData("link")]
    [InlineData("offer")]
    [InlineData("order")]
    [InlineData("stock")]
    public async Task Save_KeepsATiedPlaceholderAndStillSucceeds(string tie)
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var seed = await SeedAsync(db, $"ph-{tie}");
        await TieAsync(db, seed, tie);

        var saved = await Save(db, seed, [seed.Placeholder.Id]);

        Assert.True(saved.Succeeded, saved.Error?.Message);
        Assert.Contains(saved.Value!.Variants, variant => variant.Id == seed.Placeholder.Id);
        db.ChangeTracker.Clear();
        Assert.True(await db.ProductVariants.AsNoTracking().AnyAsync(x => x.Id == seed.Placeholder.Id));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task Save_KeepsAPlaceholderThatIsTheLastVariantOrHasOptions()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var lone = await SeedAsync(db, "ph-lone", withOptionedVariants: false);
        var keptAlone = await Save(db, lone, [lone.Placeholder.Id]);
        Assert.True(keptAlone.Succeeded, keptAlone.Error?.Message);
        Assert.Contains(keptAlone.Value!.Variants, variant => variant.Id == lone.Placeholder.Id);

        // A variant with options is never removed as a placeholder, even when its id is sent as one.
        var optioned = await SeedAsync(db, "ph-optioned");
        var keptOptioned = await Save(db, optioned, [optioned.Optioned[0].Id]);
        Assert.True(keptOptioned.Succeeded, keptOptioned.Error?.Message);
        Assert.Contains(keptOptioned.Value!.Variants, variant => variant.Id == optioned.Optioned[0].Id);
        await transaction.RollbackAsync();
    }

    private sealed record Seed(Tenant Tenant, Product Product, ProductVariant Placeholder, InventoryItem PlaceholderStock, IReadOnlyList<ProductVariant> Optioned);

    private async Task<ServiceResult<ProductView>> Save(AppDbContext db, Seed seed, IReadOnlyList<Guid> placeholderIds)
    {
        db.ChangeTracker.Clear();
        var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seed.Product.Id);
        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
        return await service.UpdateProductAsync(seed.Tenant.Id, product.Id, product.Version,
            new UpdateProductCommand(product.Title, product.Description, null, null, Status: "DRAFT", PlaceholderVariantIdsToDelete: placeholderIds), CancellationToken.None);
    }

    private async Task<Seed> SeedAsync(AppDbContext db, string code, bool withOptionedVariants = true)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Taslak {code} {Guid.NewGuid():N}", Description = "Açıklama", VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Draft, CreatedAt = now, UpdatedAt = now, Version = 1 };
        ProductVariant Variant(string signature, int order)
        {
            var sku = $"{code}-{order}-{Guid.NewGuid():N}"[..28].ToUpperInvariant();
            return new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, OptionSignature = signature, SortOrder = order, CreatedAt = now, UpdatedAt = now, Version = 1 };
        }
        var placeholder = Variant("-", 0);
        var optioned = withOptionedVariants ? new[] { Variant("Beden:S", 1), Variant("Beden:M", 2) } : [];
        var stock = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = placeholder.Id, LocationCode = "MAIN", OnHand = 0, Available = 0 };
        db.Tenants.Add(tenant);
        db.Products.Add(product);
        db.ProductVariants.Add(placeholder);
        db.ProductVariants.AddRange(optioned);
        db.InventoryItems.Add(stock);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(tenant, product, placeholder, stock, optioned);
    }

    private async Task TieAsync(AppDbContext db, Seed seed, string tie)
    {
        var now = fixture.Now;
        var tenantId = seed.Tenant.Id;
        var variantId = seed.Placeholder.Id;
        PlatformConnection Connection() => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Placeholder test", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", Version = 1 };
        switch (tie)
        {
            case "link":
                var linkConnection = Connection();
                db.PlatformConnections.Add(linkConnection);
                db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = linkConnection.Id, VariantId = variantId, ExternalId = $"EXT-{Guid.NewGuid():N}" });
                break;
            case "offer":
                var offerConnection = Connection();
                db.PlatformConnections.Add(offerConnection);
                db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = offerConnection.Id, VariantId = variantId, ListPrice = 100m, SalePrice = 90m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", SafetyStock = 0, Status = "ACTIVE", PriceVersion = 1, Version = 1 });
                break;
            case "order":
                var orderConnection = Connection();
                var orderId = Guid.CreateVersion7();
                var orderNumber = $"ph-order-{Guid.NewGuid():N}";
                db.PlatformConnections.Add(orderConnection);
                db.Orders.Add(new Order { Id = orderId, TenantId = tenantId, ConnectionId = orderConnection.Id, ExternalOrderId = orderNumber, OrderNumber = orderNumber, Currency = "TRY", GrossAmount = 100m, NetAmount = 100m, OrderedAt = now.AddDays(-1), LastRemoteModifiedAt = now.AddDays(-1), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 });
                db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = orderId, VariantId = variantId, ExternalLineId = "line-1", Sku = seed.Placeholder.Sku, TitleSnapshot = "Tek Ürün", OrderedQuantity = 1m, UnitPrice = 100m, VatRate = 10m, RawStatus = "Delivered", Version = 1 });
                break;
            case "stock":
                db.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenantId, InventoryItemId = seed.PlaceholderStock.Id, MovementType = "MANUAL_ADJUSTMENT", QuantityDelta = 1, SourceType = "MANUAL", SourceId = Guid.NewGuid().ToString("D"), SourceEventId = "placeholder-test", IdempotencyKey = $"placeholder-test:{Guid.NewGuid():N}", OccurredAt = now, RecordedAt = now, CorrelationId = "placeholder-test" });
                break;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
