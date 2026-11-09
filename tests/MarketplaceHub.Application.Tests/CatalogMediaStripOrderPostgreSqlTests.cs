using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// RYP007 (owner, 6 Nov 2026): "beden tablosu ekledim, eşleştirdim, kaydet dedim, yine kaydı sıralaması". One card with
// 6 colours × 3 sizes and 3 pictures per colour; a size chart uploaded last (product-level row at the end) and assigned
// to every variant (each variant's rows renumbered 0-3, the chart 3). GET /products/{id} gave mediaUrls colour by colour
// (green ×3, chart, brown ×3…), so the strip moved the chart to 4th place. Every list now reads the product-level order.
[Collection("postgres-integration")]
public sealed class CatalogMediaStripOrderPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly string[] Colours = ["Haki", "Kahverengi", "Kiremit", "Lacivert", "Siyah", "Vizon"];
    private static readonly string[] Sizes = ["L", "XL", "2XL"];

    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetProduct_KeepsASizeChartUploadedLastAndAssignedToEveryVariantLast(bool customOrder)
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenant, product, expected) = await SeedAsync(db, customOrder);
        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

        var result = await service.GetProductAsync(tenant.Id, product.Id, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var view = result.Value!;
        Assert.Equal(customOrder, view.HasCustomMediaOrder);
        Assert.Equal(expected, view.MediaUrls);
        Assert.Equal(expected, view.FamilyMediaUrls);
        if (customOrder) Assert.Equal(expected, view.FamilyOrderedMediaUrls);
        else Assert.Null(view.FamilyOrderedMediaUrls);
        Assert.Equal(expected[0], view.PrimaryImageUrl);

        // The products list shows the same front picture; its slim card carries no picture strip (only the editor reads
        // it) and each variant only its first picture, the size chart never among them.
        var listed = await service.ListProductsAsync(tenant.Id, 50, null, null, null, null, null, null, CancellationToken.None);
        var card = Assert.Single(listed.Items);
        Assert.Null(card.MediaUrls);
        Assert.Equal(expected[0], card.PrimaryImageUrl);
        Assert.All(card.Variants, variant => Assert.Single(variant.MediaUrls!));
        Assert.DoesNotContain(card.Variants, variant => variant.MediaUrls![0].Contains("beden-tablosu", StringComparison.Ordinal));
        await transaction.RollbackAsync();
    }

    private async Task<(Tenant Tenant, Product Product, List<string> Expected)> SeedAsync(AppDbContext db, bool customOrder)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"ryp007-{Guid.NewGuid():N}", DisplayName = "ryp007", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kadın Triko Kazak", Description = "Açıklama", VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Tenants.Add(tenant);
        db.Products.Add(product);
        var expected = new List<string>();
        var assets = new Dictionary<string, FileAsset>();
        FileAsset Asset(string url)
        {
            if (assets.TryGetValue(url, out var existing)) return existing;
            var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = url, MimeType = "image/remote", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
            db.FileAssets.Add(asset);
            return assets[url] = asset;
        }
        // A family reorder numbers every row rank × stride (live: 0, 92… 1656); a plain card 0, 1, 2…
        int Rank(int index) => customOrder ? index * 92 : index;
        string Role(int index) => customOrder ? index == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY" : index == 0 ? "PRIMARY" : "GALLERY";
        void Media(ProductVariant? variant, string url, int sortOrder, int position) =>
            db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, VariantId = variant?.Id, FileAssetId = Asset(url).Id, MediaRole = Role(position), SortOrder = sortOrder, Status = "ACTIVE" });

        const string chart = "https://cdn.dsmcdn.com/ryp007/beden-tablosu.jpg";
        string Picture(int colour, int picture) => $"https://cdn.dsmcdn.com/ryp007/{Colours[colour].ToLowerInvariant()}-{picture + 1}.jpg";
        for (var colour = 0; colour < Colours.Length; colour++)
            for (var picture = 0; picture < 3; picture++)
            {
                expected.Add(Picture(colour, picture));
                Media(null, Picture(colour, picture), Rank(colour * 3 + picture), colour * 3 + picture);
            }
        expected.Add(chart);
        Media(null, chart, Rank(18), 18);
        var sortOrder = 0;
        for (var colour = 0; colour < Colours.Length; colour++)
            foreach (var size in Sizes)
            {
                var sku = $"RYP007-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
                var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, ModelCode = "RYP007", OptionSignature = $"Renk: {Colours[colour]} | Beden: {size}", SortOrder = sortOrder++, CreatedAt = now, UpdatedAt = now, Version = 1 };
                db.ProductVariants.Add(variant);
                // "Seçeneklere Görsel Ata" renumbers the variant's own rows 0-3: its colour's 3 pictures, then the chart.
                for (var picture = 0; picture < 3; picture++) Media(variant, Picture(colour, picture), picture, picture);
                Media(variant, chart, 3, 3);
            }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (tenant, product, expected);
    }
}
