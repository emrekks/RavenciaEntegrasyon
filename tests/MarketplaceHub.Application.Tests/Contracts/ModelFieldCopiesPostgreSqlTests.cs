using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// KATALOG-14 D5 step B2b: Catalog:MirrorModelFields. Each test runs its flow in both positions (ModelFieldCopies.Use):
// the owner's writers compare against the model (a value equal to a stale copy but not to the model is written), no copy
// column is written while the switch is off, the same marketplace work is queued in both positions, and switching
// on → off → on changes no reader's output.
[Collection("postgres-integration")]
public sealed class ModelFieldCopiesPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    public static TheoryData<bool> Positions => new() { true, false };

    private CatalogService Service(AppDbContext db) => new(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private static async Task<string> CopiesAsync(AppDbContext db, Guid tenantId) =>
        JsonSerializer.Serialize(new
        {
            Cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.Description, x.VatRate, x.VatInclusion, x.CategoryId, x.WarrantyMonths, x.DeliveryDays }).ToListAsync(),
            Variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.Desi }).ToListAsync(),
        });

    // The lost edit: every copy says 20 % excluded while the model says 10 % included. The owner sets 20 % on one colour
    // (the editor, then the products file, then Toplu Değer Ata on another model value): the model takes it although the
    // value equals the stale copy. Before the rebase the writers compared with the copy, saw no change and wrote nothing.
    [PostgreSqlTheory]
    [MemberData(nameof(Positions))]
    public async Task AValueEqualToAStaleCopyButNotToTheModel_IsWritten(bool copies)
    {
        using var position = ModelFieldCopies.Use(copies);
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        await db.Products.Where(x => x.TenantId == seeded.Tenant.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.VatRate, 20m).SetProperty(x => x.VatInclusion, "EXCLUDED").SetProperty(x => x.WarrantyMonths, 6).SetProperty(x => x.Description, "Bayat kopya"));
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        var card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[0].Id);

        // Editor: VAT 20 % excluded and the description that the copies hold.
        var saved = await Service(db).UpdateProductAsync(seeded.Tenant.Id, card.Id, card.Version, new UpdateProductCommand(card.Title, "Bayat kopya", model.BrandId, model.CategoryId, VatRate: 20m, VatInclusion: "EXCLUDED"), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        var after = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id);
        Assert.Equal(((decimal?)20m, "EXCLUDED", "Bayat kopya"), (after.VatRate, after.VatInclusion, after.Description));

        // Toplu Değer Ata: warranty 6, which every copy holds and the model (24) does not.
        var bulk = await Service(db).BulkSetFieldAsync(seeded.Tenant.Id, new BulkProductFieldCommand([seeded.Cards[1].Id], BulkProductFields.WarrantyMonths, "6"), CancellationToken.None);
        Assert.True(bulk.Succeeded, bulk.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal((int?)6, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id)).WarrantyMonths);

        // Products file: VAT 10 % on a colour whose copy says 20 %: the model (now 20) takes 10.
        await db.Products.Where(x => x.TenantId == seeded.Tenant.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.VatRate, 10m));
        var third = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[2].Id);
        var applied = await new ProductTransferService(db, fixture.TimeProvider).ApplyAsync(seeded.Tenant.Id, Guid.CreateVersion7(),
            new ApplyProductTransferCommand([], [new ProductTransferProductChange(third.Id, third.Version, null, 10m)]), $"b2b-{Guid.NewGuid():N}", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal((decimal?)10m, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id)).VatRate);
        Assert.All(await EffectiveCatalog.Products(db).Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync(), x => Assert.Equal(((decimal?)10m, (int?)6), (x.VatRate, x.WarrantyMonths)));
    }

    // Off: the editor, the products file and Toplu Değer Ata (description, VAT, warranty, desi) and a new colour change the
    // model and write no copy column of the seven values; the readers and the composed payloads follow the model, so the
    // poisoned-copy proof still holds after edits made while off. On: the same edits write the copies equal to the model.
    [PostgreSqlTheory]
    [MemberData(nameof(Positions))]
    public async Task OwnerEdits_WriteTheModel_AndTheCopiesOnlyWhileTheSwitchIsOn(bool copies)
    {
        using var position = ModelFieldCopies.Use(copies);
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        var before = await CopiesAsync(db, seeded.Tenant.Id);
        await EditAsync(db, seeded);
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        Assert.Equal(("B2b açıklama", (decimal?)20m, (int?)12, (int?)5, (decimal?)2m), (model.Description, model.VatRate, model.WarrantyMonths, model.DeliveryDays, model.Desi));
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync();
        var original = seeded.Cards.Select(x => x.Id).ToHashSet();
        if (copies)
        {
            Assert.All(cards, x => Assert.Equal(("B2b açıklama", (decimal?)20m, (int?)12, (int?)5), (x.Description, x.VatRate, x.WarrantyMonths, x.DeliveryDays)));
            Assert.All(await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync(), x => Assert.Equal((decimal?)2m, x.Desi));
        }
        else
        {
            // The seeded colours' copies are as they were; the new colour holds only its NOT NULL description.
            var now = JsonSerializer.Deserialize<JsonElement>(await CopiesAsync(db, seeded.Tenant.Id));
            var then = JsonSerializer.Deserialize<JsonElement>(before);
            Assert.Equal(then.GetProperty("Cards").ToString(), JsonSerializer.Serialize(now.GetProperty("Cards").EnumerateArray().Where(x => original.Contains(x.GetProperty("Id").GetGuid()))));
            var added = cards.Single(x => !original.Contains(x.Id));
            Assert.Equal(((decimal?)null, (string?)null, (Guid?)null, (int?)null, (int?)null), (added.VatRate, added.VatInclusion, added.CategoryId, added.WarrantyMonths, added.DeliveryDays));
            Assert.All(await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id && x.ProductId == added.Id).ToListAsync(), x => Assert.Null(x.Desi));
        }
        Assert.All(await EffectiveCatalog.Products(db).Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync(), x => Assert.Equal(("B2b açıklama", (decimal?)20m, (int?)12, (int?)5, model.CategoryId), (x.Description, x.VatRate, x.WarrantyMonths, x.DeliveryDays, x.CategoryId)));
        Assert.All(await EffectiveCatalog.Variants(db).Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync(), x => Assert.Equal((decimal?)2m, x.Desi));
        // No false copy warning in either position.
        Assert.Empty(await ModelMirror.ReportGapsAsync(db, seeded.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False(await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == seeded.Tenant.Id && x.Code == ProductModelValues.DifferCode && x.Status != IssueStatus.Resolved));

        // Poisoned copies change nothing a reader gives.
        var payloads = ModelComposerGoldenPostgreSqlTests.Normalize(await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded), seeded);
        await StaleCardCopyReadersPostgreSqlTests.PoisonCopiesAsync(db, seeded.Tenant.Id);
        Assert.Equal(payloads, ModelComposerGoldenPostgreSqlTests.Normalize(await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded), seeded));
    }

    // The same owner edits queue the same marketplace work in both positions: the product links marked edited (status,
    // dirty fields, version), the jobs and the outbox events, compared colour by colour.
    [PostgreSqlFact]
    public async Task OwnerEdits_QueueTheSameMarketplaceWork_InBothPositions()
    {
        async Task<string> RunAsync(bool copies)
        {
            using var position = ModelFieldCopies.Use(copies);
            var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
            await using var db = fixture.CreateContext();
            foreach (var connection in seeded.Connections)
                foreach (var card in seeded.Cards)
                    db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, ExternalId = $"{connection.PlatformCode}-{card.Id:N}" });
            await db.SaveChangesAsync();
            var jobsBefore = await db.IntegrationJobs.AsNoTracking().CountAsync(x => x.TenantId == seeded.Tenant.Id);
            await EditAsync(db, seeded);
            var cardIndex = seeded.Cards.Select((x, i) => (x.Id, i)).ToDictionary(x => x.Id, x => x.i);
            var platform = seeded.Connections.ToDictionary(x => x.Id, x => x.PlatformCode);
            var links = (await db.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync())
                .Where(x => cardIndex.ContainsKey(x.ProductId))
                .Select(x => $"{platform[x.ConnectionId]} card{cardIndex[x.ProductId]} {x.SyncStatus} {x.DirtyFieldsJson} v{x.Version}").Order(StringComparer.Ordinal);
            var jobs = (await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).Select(x => x.JobType).ToListAsync()).Order(StringComparer.Ordinal);
            var outbox = (await db.IntegrationOutboxEvents.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).Select(x => x.AggregateType + ":" + x.OperationType).ToListAsync()).Order(StringComparer.Ordinal);
            return string.Join("\n", links.Append($"jobs {jobsBefore}: {string.Join(",", jobs)}").Append($"outbox: {string.Join(",", outbox)}"));
        }
        var on = await RunAsync(true);
        var off = await RunAsync(false);
        Assert.Contains("LOCAL_CHANGES_PENDING", on, StringComparison.Ordinal);
        Assert.Equal(on, off);
    }

    // On → off → on: an edit while off leaves the copies stale; switching back on rewrites nothing by itself and every
    // reader (composed payloads, effective values) stays the same across the three positions.
    [PostgreSqlFact]
    public async Task SwitchingOnOffOn_LeavesEveryReadTheSame()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        string copiesOff;
        List<string> payloadsOff;
        await using (var db = fixture.CreateContext())
        using (ModelFieldCopies.Use(false))
        {
            await EditAsync(db, seeded);
            copiesOff = await CopiesAsync(db, seeded.Tenant.Id);
            payloadsOff = ModelComposerGoldenPostgreSqlTests.Normalize(await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded), seeded);
        }
        using (ModelFieldCopies.Use(true))
        {
            await using var db = fixture.CreateContext();
            Assert.Equal(payloadsOff, ModelComposerGoldenPostgreSqlTests.Normalize(await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded), seeded));
            Assert.Equal(copiesOff, await CopiesAsync(db, seeded.Tenant.Id));
            // The model scan and the gap report write none of it either. The warranty pass's one-time repair (review L3: as
            // the release before B2a) may give a new card's empty warranty copy 0 while the switch is on; it writes no model
            // field, so no reader changes.
            var models = JsonSerializer.Serialize(await db.ProductModels.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).OrderBy(x => x.Id).ToListAsync());
            await ProductModelValues.RefreshTenantAsync(db, seeded.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            await ModelMirror.ReportGapsAsync(db, seeded.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(copiesOff, await CopiesAsync(db, seeded.Tenant.Id));
            await ProductWarranty.RunPassAsync(db, firstPassAfterStart: true, CancellationToken.None);
            Assert.Equal(models, JsonSerializer.Serialize(await db.ProductModels.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).OrderBy(x => x.Id).ToListAsync()));
            Assert.Equal(payloadsOff, ModelComposerGoldenPostgreSqlTests.Normalize(await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded), seeded));
        }
    }

    // The owner's edits of the flows above: editor (description, delivery days), products file (VAT), Toplu Değer Ata
    // (warranty, desi) and a new colour from the editor.
    private async Task EditAsync(AppDbContext db, ModelComposerGoldenPostgreSqlTests.Seeded seeded)
    {
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        var card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[0].Id);
        var saved = await Service(db).UpdateProductAsync(seeded.Tenant.Id, card.Id, card.Version, new UpdateProductCommand(card.Title, "B2b açıklama", model.BrandId, model.CategoryId, DeliveryDays: 5), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        var second = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[1].Id);
        var applied = await new ProductTransferService(db, fixture.TimeProvider).ApplyAsync(seeded.Tenant.Id, Guid.CreateVersion7(),
            new ApplyProductTransferCommand([], [new ProductTransferProductChange(second.Id, second.Version, null, 20m)]), $"b2b-{Guid.NewGuid():N}", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.True((await Service(db).BulkSetFieldAsync(seeded.Tenant.Id, new BulkProductFieldCommand([seeded.Cards[2].Id], BulkProductFields.WarrantyMonths, "12"), CancellationToken.None)).Succeeded);
        db.ChangeTracker.Clear();
        Assert.True((await Service(db).BulkSetFieldAsync(seeded.Tenant.Id, new BulkProductFieldCommand([seeded.Cards[2].Id], BulkProductFields.Desi, "2"), CancellationToken.None)).Succeeded);
        db.ChangeTracker.Clear();
        card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[0].Id);
        var options = new Dictionary<string, string> { ["Renk"] = "Haki", ["Beden"] = "M" };
        var colour = await Service(db).UpdateProductAsync(seeded.Tenant.Id, card.Id, card.Version, new UpdateProductCommand(card.Title, "B2b açıklama", model.BrandId, model.CategoryId,
            VariantsToCreate: [new CreateVariantCommand("MZ300-HM", "8690000033111", "MZ300", options, Desi: 2m, DefaultListPrice: 900m, DefaultSalePrice: 630m)]), CancellationToken.None);
        Assert.True(colour.Succeeded, colour.Error?.Message);
        db.ChangeTracker.Clear();
    }
}
