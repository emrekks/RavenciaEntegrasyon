using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The Düşük Stok window reads a whole stock list in one page (114 models, 443 cards on live): the list's cards are built
// from per-card lookups of the page's variants, profiles, links, listings and pictures, and the model picture from one
// family lookup. A card built in a page of many must equal the same card built in a page of its model alone (where no
// other card's rows can be picked up): every number, state, picture and order of the answer, for LOW, GAP and the plain list.
[Collection("postgres-integration")]
public sealed class LowStockListViewsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly string[] ListingStates = ["LIVE", "REJECTED", "APPROVAL_PENDING", "MANUAL_REVIEW", "LIVE", "QUEUED"];

    private async Task<Guid> SeedAsync(AppDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"lowviews-{Guid.NewGuid():N}", DisplayName = "lowviews", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Tenants.Add(tenant);
        var ravencia = Guid.CreateVersion7();
        var lavian = Guid.CreateVersion7();
        db.Brands.Add(new Brand { Id = ravencia, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = lavian, TenantId = tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        var connections = new[] { "TRENDYOL", "HEPSIBURADA" }.Select(code => new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = code, Environment = "PRODUCTION", DisplayName = code, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", SettingsJson = "{\"ExternalWritesEnabled\":false}", Version = 1 }).ToArray();
        db.PlatformConnections.AddRange(connections);
        var at = now.AddDays(-2);
        var step = 0;
        // Three models of several colours (one code under two brands), stock that makes colours LOW and GAP, and on each
        // marketplace links (one the marketplace no longer knows) and listings in every state, held ones included.
        foreach (var (brand, code, colours) in new (Guid Brand, string Code, string[] Colours)[] { (ravencia, "LS100", ["Siyah", "Mavi", "Bej"]), (lavian, "LS100", ["Siyah", "Gri"]), (ravencia, "LS200", ["Ekru", "Haki", "Lacivert", "Vizon"]) })
            foreach (var colour in colours)
            {
                step++;
                var card = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"{code} {colour}", Description = "Açıklama", BrandId = brand, VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = at.AddMinutes(step), UpdatedAt = at.AddMinutes(step), Version = 1 };
                db.Products.Add(card);
                var profiles = connections.Select((connection, index) => new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = (step + index) % 3 == 0 ? "UPDATE_PENDING" : "LIVE" }).ToArray();
                db.ChannelListingProfiles.AddRange(profiles);
                string[] sizes = ["S", "M", "L"];
                for (var index = 0; index < sizes.Length; index++)
                {
                    var sku = $"{code}-{colour}-{sizes[index]}-{Guid.NewGuid():N}"[..36];
                    var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), ModelCode = code, OptionSignature = $"Renk: {colour} | Beden: {sizes[index]}", Desi = 1m, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = ProductStatus.Active, SortOrder = index, CreatedAt = card.CreatedAt, UpdatedAt = card.CreatedAt, Version = 1 };
                    db.ProductVariants.Add(variant);
                    var onHand = (step + index) % 3 == 0 ? 4m : 0m;
                    db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = onHand, Available = onHand, ProjectionVersion = 1, Version = 1 });
                    for (var c = 0; c < connections.Length; c++)
                    {
                        if ((step + index + c) % 4 != 3)
                            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connections[c].Id, VariantId = variant.Id, ExternalId = $"x-{Guid.NewGuid():N}", ExternalBarcode = sku, ExternalSku = sku, HealthStatus = (step + index + c) % 5 == 0 ? MarketplaceVariantLinkHealth.NotFound : null, SaleState = (step + c) % 2 == 0 ? ListingSaleState.OnSale : ListingSaleState.OffSale, SaleStateReason = (step + c) % 2 == 0 ? null : ListingSaleState.StockZero });
                        db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = profiles[c].Id, VariantId = variant.Id, ExternalSku = sku, ExternalBarcode = sku, DesiredStatus = "LIVE", ActualStatus = ListingStates[(step + index + c) % ListingStates.Length] });
                    }
                    if (index == 1)
                    {
                        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.test/{step}/{code}/{colour}/v.jpg", MimeType = "image/remote", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
                        db.FileAssets.Add(asset);
                        db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, VariantId = variant.Id, FileAssetId = asset.Id, MediaRole = "GALLERY", SortOrder = 10 + step, Status = "ACTIVE" });
                    }
                }
                if (step % 2 == 0)
                {
                    var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.test/{step}/{code}/{colour}/front.jpg", MimeType = "image/remote", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
                    db.FileAssets.Add(asset);
                    db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, FileAssetId = asset.Id, MediaRole = "PRIMARY", SortOrder = step, Status = "ACTIVE" });
                }
            }
        await db.SaveChangesAsync();
        await ProductModelLinks.ReconcileTenantAsync(db, tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        db.ChangeTracker.Clear();
        return tenant.Id;
    }

    private async Task<List<ProductView>> ListAllAsync(AppDbContext db, Guid tenantId, string? stock, int limit)
    {
        var service = new CatalogService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
        var items = new List<ProductView>();
        string? after = null;
        for (var page = 0; page < 50; page++)
        {
            var result = await service.ListProductsAsync(tenantId, limit, after, null, null, null, stock, null, CancellationToken.None);
            items.AddRange(result.Items);
            if (!result.HasMore) break;
            after = result.NextCursor;
        }
        return items;
    }

    [PostgreSqlFact]
    public async Task ACardBuiltInAWholeStockList_EqualsTheCardBuiltWithItsModelAlone()
    {
        await using var db = fixture.CreateContext();
        var tenantId = await SeedAsync(db);
        var dump = Environment.GetEnvironmentVariable("LOW_STOCK_VIEWS_DUMP");
        foreach (var stock in new string?[] { "LOW", "GAP", null })
        {
            var whole = await ListAllAsync(db, tenantId, stock, 200);
            var oneByOne = await ListAllAsync(db, tenantId, stock, 1);
            Assert.NotEmpty(whole);
            Assert.Equal(JsonSerializer.Serialize(oneByOne), JsonSerializer.Serialize(whole));
            if (dump is not null) await File.WriteAllTextAsync($"{dump}-{stock ?? "ALL"}.json", JsonSerializer.Serialize(whole));
            // The seed reaches every branch the lookups replaced: held, refused, waiting and reviewed listings, a link the
            // marketplace no longer knows, sale states, card and variant pictures, one code under two brands.
            if (stock is null)
            {
                var statuses = whole.SelectMany(view => view.PlatformStatuses ?? []).ToList();
                Assert.Contains(statuses, status => status.MissingVariantCount > 0);
                Assert.Contains(statuses, status => status.RejectedVariantCount > 0 || status.ResubmittedVariantCount > 0);
                Assert.Contains(statuses, status => status.PendingVariantCount > 0);
                Assert.Contains(statuses, status => status.ReviewVariantCount > 0);
                Assert.Contains(statuses, status => status.OnSaleVariantCount > 0);
                Assert.Contains(statuses, status => status.OffSaleVariantCount > 0);
                Assert.Equal(9, whole.Count);
                Assert.Equal(2, whole.Where(view => view.ModelCode == "LS100").Select(view => view.FamilyImageUrl).Distinct().Count());
                Assert.All(whole, view => Assert.NotNull(view.PrimaryImageUrl));
            }
        }
    }
}
