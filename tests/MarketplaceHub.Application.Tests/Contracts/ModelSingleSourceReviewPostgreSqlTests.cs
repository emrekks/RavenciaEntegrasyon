using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// KATALOG-14 D5 step B2a, review of 27 Nov 2026: an owner edit is never silently lost (M1), models opened by the reconcile
// and for a card that loses its code carry the desi (M2), the warranty pass writes no more than before (L3), and the report
// lines read the model's desi (L5).
[Collection("postgres-integration")]
public sealed class ModelSingleSourceReviewPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private CatalogService Service(AppDbContext db) => new(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    // M1: an editor save that deletes a variant runs serializable. Its model fields are written in its own transaction, so
    // when the model row cannot be written (another session holds it) the save answers "nothing saved, try again" and no
    // card copy, variant delete or model field stands; saving again applies the edit to the model. Before the fix the save
    // answered success, the card held the new text and the model (which every reader reads) the old one.
    [PostgreSqlFact]
    public async Task ASerializableEditorSave_ThatCannotWriteItsModel_SavesNothing_AndSavingAgainApplies()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        db.ModelLockTimeout = "1s";
        var card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[0].Id);
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == card.FamilyId);
        var dropped = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == card.Id).OrderBy(x => x.SortOrder).LastAsync();
        UpdateProductCommand Command() => new(card.Title, "Editörden yeni açıklama", model.BrandId, model.CategoryId, VariantIdsToDelete: [dropped.Id]);

        await using (var holder = fixture.CreateContext())
        {
            await using var hold = await holder.Database.BeginTransactionAsync();
            await holder.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM catalog.product_models WHERE "Id" = {model.Id} FOR UPDATE""");
            var refused = await Service(db).UpdateProductAsync(seeded.Tenant.Id, card.Id, card.Version, Command(), CancellationToken.None);
            Assert.False(refused.Succeeded);
            Assert.Equal((409, "SAVE_CONFLICT_RETRY"), (refused.Error!.Status, refused.Error.Code));
            await hold.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal(card.Description, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id)).Description);
        Assert.Equal(model.Description, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id)).Description);
        Assert.True(await db.ProductVariants.AnyAsync(x => x.Id == dropped.Id));

        var saved = await Service(db).UpdateProductAsync(seeded.Tenant.Id, card.Id, card.Version, Command(), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        Assert.Equal("Editörden yeni açıklama", saved.Value!.Description);
        db.ChangeTracker.Clear();
        Assert.Equal("Editörden yeni açıklama", (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id)).Description);
        Assert.All(await EffectiveCatalog.Products(db).Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync(), x => Assert.Equal("Editörden yeni açıklama", x.Description));
        Assert.False(await db.ProductVariants.AnyAsync(x => x.Id == dropped.Id));
    }

    // M2 (reconcile): a code changed on every colour of a model whose id cannot be kept (another model of the brand holds a
    // colour of it) opens the code's model from the first card's model, desi included.
    [PostgreSqlFact]
    public async Task AModelTheReconcileOpens_CarriesTheDesiOfTheModelItsCardsLeft()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        var oldModel = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        await db.ProductModels.Where(x => x.Id == oldModel.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Desi, 4.5m));
        // One colour moves to a new code alone: the old model keeps the other colours, so a new model opens for it.
        var moving = seeded.Cards[2].Id;
        await db.ProductVariants.Where(x => x.ProductId == moving).ExecuteUpdateAsync(s => s.SetProperty(x => x.ModelCode, "MZ999"));
        await ProductModelLinks.ReconcileAsync(db, seeded.Tenant.Id, ["MZ300", "MZ999"], DateTimeOffset.UtcNow, CancellationToken.None);
        var opened = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id && x.ModelCodeKey == "MZ999");
        Assert.Equal(opened.Id, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == moving)).FamilyId);
        Assert.Equal((decimal?)4.5m, opened.Desi);
        Assert.Equal(oldModel.Description, opened.Description);
    }

    // M2 (own model): a colour whose code is removed moves to a model of its own opened from the model it leaves, desi too.
    [PostgreSqlFact]
    public async Task AnOwnModelOpenedForACardThatLostItsCode_CarriesTheDesi()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        var oldModel = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        await db.ProductModels.Where(x => x.Id == oldModel.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Desi, 3.5m));
        var moving = seeded.Cards[2].Id;
        await db.ProductVariants.Where(x => x.ProductId == moving).ExecuteUpdateAsync(s => s.SetProperty(x => x.ModelCode, (string?)null));
        await ProductModelLinks.ReconcileAsync(db, seeded.Tenant.Id, ["MZ300"], DateTimeOffset.UtcNow, CancellationToken.None);
        var own = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id && x.ModelCodeKey == $"{ProductModelLinks.OwnModelKeyPrefix}{moving:N}");
        Assert.Equal(own.Id, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == moving)).FamilyId);
        Assert.Equal((decimal?)3.5m, own.Desi);
    }

    // L3: the first warranty pass after a start writes where the previous release wrote and nowhere else. A model with an
    // empty warranty holding a draft and a card on sale keeps its empty warranty and so does the draft's copy (the card on
    // sale's empty copy takes 0, as before); the later passes count by the model.
    [PostgreSqlFact]
    public async Task TheWarrantyPass_LeavesAMixedDraftAndActiveEmptyModelAlone()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        await db.ProductModels.Where(x => x.Id == model.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WarrantyMonths, (int?)null));
        await db.Products.Where(x => x.TenantId == seeded.Tenant.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WarrantyMonths, (int?)null));
        var draft = seeded.Cards[1].Id;
        await db.Products.Where(x => x.Id == draft).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ProductStatus.Draft));

        var later = await ProductWarranty.RunPassAsync(db, firstPassAfterStart: false, CancellationToken.None);
        Assert.True(later.EmptyCardsNotDraft >= 2);
        await ProductWarranty.RunPassAsync(db, firstPassAfterStart: true, CancellationToken.None);
        Assert.Null((await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id)).WarrantyMonths);
        Assert.Null((await db.Products.AsNoTracking().SingleAsync(x => x.Id == draft)).WarrantyMonths);
        Assert.Equal((int?)0, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[0].Id)).WarrantyMonths);
    }

    // L5 (gap 2): a new colour added from the editor brings the model's VAT, not the opened card's poisoned copy. Before the
    // fix it brought the copy's 20 % excluded, which the model refused and reported as a VAT difference.
    [PostgreSqlFact]
    public async Task ANewColourCard_BringsTheModelsVat_SoNoVatDifferenceIsReported()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        await StaleCardCopyReadersPostgreSqlTests.PoisonCopiesAsync(db, seeded.Tenant.Id);
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        var card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[0].Id);
        var options = new Dictionary<string, string> { ["Renk"] = "Haki", ["Beden"] = "M" };
        var saved = await Service(db).UpdateProductAsync(seeded.Tenant.Id, card.Id, card.Version, new UpdateProductCommand(card.Title, model.Description, model.BrandId, model.CategoryId,
            VariantsToCreate: [new CreateVariantCommand("MZ300-HM", "8690000033111", "MZ300", options, Desi: 1m, DefaultListPrice: 900m, DefaultSalePrice: 630m)]), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        Assert.False(await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == seeded.Tenant.Id && x.DedupeKey == $"{ModelMirror.DifferDedupePrefix}{model.Id:N}"));
    }

    // L5 (report desi): a report line without a desi snapshot takes the model's desi, not the variant's poisoned copy.
    [PostgreSqlFact]
    public async Task AReportLine_ReadsTheModelsDesi()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        var connection = seeded.Connections[0];
        var variant = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == seeded.Cards[0].Id).OrderBy(x => x.SortOrder).FirstAsync();
        var now = fixture.Now;
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ConnectionId = connection.Id, ExternalOrderId = $"r-{Guid.NewGuid():N}", OrderNumber = $"r-{Guid.NewGuid():N}", Currency = "TRY", OrderedAt = now.AddDays(-1), LastRemoteModifiedAt = now.AddDays(-1), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Orders.Add(order);
        db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, OrderId = order.Id, ExternalLineId = "r-1", Sku = variant.Sku, TitleSnapshot = "Bluz", RawStatus = "Delivered", VariantId = variant.Id, OrderedQuantity = 1, UnitPrice = 100m });
        await db.SaveChangesAsync();
        await StaleCardCopyReadersPostgreSqlTests.PoisonCopiesAsync(db, seeded.Tenant.Id);

        var rows = await ReportsService.LineRows(db, seeded.Tenant.Id,
            db.Orders.AsNoTracking().Where(x => x.Id == order.Id).Select(x => new ReportsService.PeriodOrder { Order = x, RealAt = x.OrderedAt })).ToListAsync();
        var line = Assert.Single(rows);
        Assert.Equal((decimal?)1m, line.Desi);
        Assert.Equal(seeded.Cards[0].Id, line.ProductId);
    }
}
