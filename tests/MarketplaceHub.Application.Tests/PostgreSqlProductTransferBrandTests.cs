using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The brand the products file writes is the model's: it goes to the colour cards of the model that had the row's own
// brand. A card under the same model code with another brand (an ambiguous model-code group) is only warned about
// (owner, 15 Nov 2026): no other card's row re-brands it; only its own row in the file does.
[Collection("postgres-integration")]
public sealed class PostgreSqlProductTransferBrandTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string ModelCode = "KENE77";

    private sealed record Seed(Guid TenantId, Guid X, Guid Y, Product A1, Product A2, Product B);

    // A1 and A2: two colours of brand X under the code; B: the same code under brand Y.
    private static async Task<Seed> SeedAsync(AppDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"transfer-brand-{Guid.NewGuid():N}", DisplayName = "transfer brand", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var x = Guid.CreateVersion7();
        var y = Guid.CreateVersion7();
        db.Tenants.Add(tenant);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Etek", NormalizedName = "ETEK", Path = "Etek", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = x, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = y, TenantId = tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        var a1 = AddCard(db, tenant.Id, categoryId, x, "Siyah", 0);
        var a2 = AddCard(db, tenant.Id, categoryId, x, "Mavi", 1);
        var b = AddCard(db, tenant.Id, categoryId, y, "Gri", 2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(tenant.Id, x, y, a1, a2, b);
    }

    private static Product AddCard(AppDbContext db, Guid tenantId, Guid categoryId, Guid brandId, string colour, int minute)
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = $"Kadın {colour} Etek", Description = "Açıklama", CategoryId = categoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", DeliveryDays = 1, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = ProductStatus.Active, CreatedAt = now.AddMinutes(minute), UpdatedAt = now, Version = 1 };
        db.Products.Add(card);
        var index = 0;
        foreach (var size in new[] { "M", "L" })
        {
            var sku = $"{ModelCode}-{colour}-{size}-{Guid.NewGuid():N}"[..36];
            var barcode = $"869{Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L)}";
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = card.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode, ModelCode = ModelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = ProductStatus.Active, SortOrder = index++, CreatedAt = now, UpdatedAt = now, Version = 1 });
        }
        return card;
    }

    private async Task ApplyAsync(AppDbContext db, Seed seed, params ProductTransferProductChange[] changes)
    {
        var applied = await new ProductTransferService(db, fixture.TimeProvider).ApplyAsync(seed.TenantId, Guid.CreateVersion7(), new ApplyProductTransferCommand([], changes), $"transfer-brand-{Guid.NewGuid():N}", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        db.ChangeTracker.Clear();
    }

    private static Task<Dictionary<Guid, Product>> CardsAsync(AppDbContext db, Seed seed) =>
        db.Products.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToDictionaryAsync(x => x.Id);

    private static Task<Guid> BrandAsync(AppDbContext db, Seed seed, string normalizedName) =>
        db.Brands.AsNoTracking().Where(x => x.TenantId == seed.TenantId && x.NormalizedName == normalizedName).Select(x => x.Id).SingleAsync();

    private static Task<IssueStatus?> AmbiguityAsync(AppDbContext db, Seed seed) =>
        db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == seed.TenantId && x.Code == ProductModelLinks.AmbiguousCode).Select(x => (IssueStatus?)x.Status).SingleOrDefaultAsync();

    [PostgreSqlFact]
    public async Task ARowRebrandingACard_MovesItsOwnBrandsColours_AndNeverTheOtherBrandsCardOfTheCode()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        Assert.Equal(IssueStatus.Open, await AmbiguityAsync(db, seed));

        await ApplyAsync(db, seed, new ProductTransferProductChange(seed.A1.Id, 1, null, null, Brand: "Mavera"));

        var z = await BrandAsync(db, seed, "MAVERA");
        var cards = await CardsAsync(db, seed);
        Assert.Equal([z, z], [cards[seed.A1.Id].BrandId, cards[seed.A2.Id].BrandId]);
        Assert.Equal((seed.Y, 1L), (cards[seed.B.Id].BrandId, cards[seed.B.Id].Version));
        // The code is still under two brands: nothing linked, the warning stays open, B is in no model of A's.
        Assert.Null(cards[seed.B.Id].FamilyId);
        Assert.True(cards[seed.A1.Id].FamilyId is null || cards[seed.A1.Id].FamilyId != cards[seed.B.Id].FamilyId);
        Assert.Equal(IssueStatus.Open, await AmbiguityAsync(db, seed));
    }

    [PostgreSqlFact]
    public async Task RowsForBothBrands_EachMoveTheirOwnCards()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);

        await ApplyAsync(db, seed,
            new ProductTransferProductChange(seed.A1.Id, 1, null, null, Brand: "Mavera"),
            new ProductTransferProductChange(seed.B.Id, 1, null, null, Brand: "Mavera"));

        var z = await BrandAsync(db, seed, "MAVERA");
        var cards = await CardsAsync(db, seed);
        Assert.Equal([z, z, z], [cards[seed.A1.Id].BrandId, cards[seed.A2.Id].BrandId, cards[seed.B.Id].BrandId]);
        // One brand under the code now: the reconcile links the three cards into one model and closes the warning.
        Assert.NotNull(cards[seed.A1.Id].FamilyId);
        Assert.Equal([cards[seed.A1.Id].FamilyId, cards[seed.A1.Id].FamilyId], [cards[seed.A2.Id].FamilyId, cards[seed.B.Id].FamilyId]);
        Assert.Equal(IssueStatus.Resolved, await AmbiguityAsync(db, seed));
    }

    // KATALOG-3: the server refuses, as the preview does, a file whose rows give two cards of one model two names (the
    // colour word aside) or two VAT rates, and writes nothing.
    [PostgreSqlTheory]
    [InlineData("Kadın Siyah Midi Etek", "Kadın Mavi Kısa Etek", null, null)]
    [InlineData(null, null, "20", "18")]
    public async Task TwoCardsOfAModelAskingForTwoValues_AreRefused(string? firstTitle, string? secondTitle, string? firstVat, string? secondVat)
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        decimal? Vat(string? text) => text is null ? null : decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        var applied = await new ProductTransferService(db, fixture.TimeProvider).ApplyAsync(seed.TenantId, Guid.CreateVersion7(), new ApplyProductTransferCommand([],
            [new ProductTransferProductChange(seed.A1.Id, 1, firstTitle, Vat(firstVat)), new ProductTransferProductChange(seed.A2.Id, 1, secondTitle, Vat(secondVat))]),
            $"transfer-mixed-{Guid.NewGuid():N}", CancellationToken.None);

        Assert.False(applied.Succeeded);
        Assert.Equal((ProductTransferService.ModelFieldMixedCode, 422), (applied.Error!.Code, applied.Error.Status));
        db.ChangeTracker.Clear();
        var cards = await CardsAsync(db, seed);
        Assert.All(cards.Values, card => Assert.Equal((1L, 10m), (card.Version, card.VatRate!.Value)));
    }

    [PostgreSqlFact]
    public async Task TwoCardsOfAModelNamingTheSameModelInTheirOwnColours_AreWritten()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);

        await ApplyAsync(db, seed,
            new ProductTransferProductChange(seed.A1.Id, 1, "Kadın Siyah Midi Etek", 20m),
            new ProductTransferProductChange(seed.A2.Id, 1, "Kadın Mavi Midi Etek", 20m));

        var cards = await CardsAsync(db, seed);
        Assert.Equal(("Kadın Siyah Midi Etek", "Kadın Mavi Midi Etek"), (cards[seed.A1.Id].Title, cards[seed.A2.Id].Title));
        Assert.Equal([20m, 20m], [cards[seed.A1.Id].VatRate, cards[seed.A2.Id].VatRate]);
    }

    [PostgreSqlFact]
    public async Task ARowRebrandingTheOtherBrandsCard_ToTheModelsBrand_LeavesTheModelsCardsAlone()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);

        await ApplyAsync(db, seed, new ProductTransferProductChange(seed.B.Id, 1, null, null, Brand: "Ravencia"));

        var cards = await CardsAsync(db, seed);
        Assert.Equal(seed.X, cards[seed.B.Id].BrandId);
        Assert.Equal((seed.X, 1L, seed.X, 1L), (cards[seed.A1.Id].BrandId, cards[seed.A1.Id].Version, cards[seed.A2.Id].BrandId, cards[seed.A2.Id].Version));
        Assert.Equal(IssueStatus.Resolved, await AmbiguityAsync(db, seed));
    }
}
