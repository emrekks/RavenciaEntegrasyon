using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Ürün Hareketleri › Ürün Bilgisi: the rows a panel save writes for the information fields it changed, read from the
// tracked cards and variants before the save (no database is reached: nothing names a brand or category here).
public sealed class ProductFieldChangeRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

    private static AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

    [Fact]
    public async Task Only_real_changes_of_the_kept_fields_become_rows_with_old_and_new_text()
    {
        using var db = Context();
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant, Title = "Bluz", Description = "<p>Pamuk</p> ", VatRate = 10m, VatInclusion = "INCLUDED", WarrantyMonths = null, DeliveryDays = 1 };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant, ProductId = product.Id, Sku = "BLZ-1", SkuNormalized = "BLZ1", Barcode = "868000", ModelCode = "MZ01", OptionSignature = "Renk: Siyah", CostPrice = 100m, Desi = 1.0m };
        var untouched = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant, ProductId = product.Id, Sku = "BLZ-2", SkuNormalized = "BLZ2", OptionSignature = "Renk: Beyaz", CostPrice = 50m };
        var foreign = new Product { Id = Guid.CreateVersion7(), TenantId = Guid.CreateVersion7(), Title = "Başka", Description = "" };
        db.AttachRange(product, variant, untouched, foreign);

        product.Title = "Bluz Yeni";
        product.Description = "<p>Pamuk</p>";        // only the spaces around it: no change
        product.VatRate = 20m;
        product.WarrantyMonths = 24;
        variant.CostPrice = 120.5m;
        variant.Desi = 1.00m;                          // the same number: no change
        variant.Barcode = null;
        variant.DefaultSalePrice = 99m;                // a panel price has its own kind: not here
        foreign.Title = "Başka Yeni";                  // another tenant's card: never this tenant's row

        var written = await ProductFieldChangeRecorder.RecordAsync(db, tenant, ProductChangeSources.Editor, user, "corr", Now, CancellationToken.None);

        var rows = db.ChangeTracker.Entries<ProductChangeEntry>().Select(x => x.Entity).OrderBy(x => x.Field, StringComparer.Ordinal).ToList();
        Assert.Equal(written, rows.Count);
        Assert.Equal(
            [
                (ProductChangeFields.Barcode, (string?)"868000", (string?)null, (Guid?)variant.Id),
                (ProductChangeFields.Cost, "100,00 ₺", "120,50 ₺", variant.Id),
                (ProductChangeFields.Title, "Bluz", "Bluz Yeni", null),
                (ProductChangeFields.VatRate, "%10", "%20", null),
                (ProductChangeFields.Warranty, null, "24 Ay", null)
            ],
            rows.Select(x => (x.Field!, x.PreviousValue, x.NewValue, x.VariantId)).ToArray());
        Assert.All(rows, row => Assert.Equal((ProductChangeKinds.Field, ProductChangeSources.Editor, (Guid?)user, product.Id, tenant), (row.Kind, row.Source, row.ActorUserId, row.ProductId, row.TenantId)));

        // A call for another tenant finds none of this tenant's changes.
        Assert.Equal(0, await ProductFieldChangeRecorder.RecordAsync(db, Guid.CreateVersion7(), ProductChangeSources.Editor, user, "corr", Now, CancellationToken.None));
    }

    [Fact]
    public async Task A_description_whose_shown_start_stays_names_the_change_further_on()
    {
        using var db = Context();
        var tenant = Guid.CreateVersion7();
        var start = new string('a', 130);
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant, Title = "Bluz", Description = $"{start} sonu bir" };
        db.Attach(product);

        product.Description = $"{start} sonu iki";

        Assert.Equal(1, await ProductFieldChangeRecorder.RecordAsync(db, tenant, ProductChangeSources.BulkEdit, null, "corr", Now, CancellationToken.None));
        var row = db.ChangeTracker.Entries<ProductChangeEntry>().Single().Entity;
        Assert.Equal((ProductChangeFields.Description, "Açıklamanın devamı değişti."), (row.Field, row.Note));
        Assert.Equal(row.PreviousValue, row.NewValue);
    }
}
