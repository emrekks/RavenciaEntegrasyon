using System.Data.Common;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// KATALOG-14 D5 step B2b, review of 27 Nov 2026: the editor's after-commit model write puts back no value another save
// wrote (1), a switched-off save that fails is applied by a retry in the same context (2), and after switching back on a
// stale copy is healed by an owner save without a send while no automatic pass writes and no false warning opens (3).
[Collection("postgres-integration")]
public sealed class ModelFieldCopiesReviewPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    public static TheoryData<bool> Positions => new() { true, false };

    private CatalogService Service(AppDbContext db) => new(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    // 1: the editor saves the model's fields (FamilySharedFields); right after its commit another session (Toplu Değer Ata on
    // another colour) gives the model warranty 6; the editor's after-commit write keeps it. Before the fix it wrote every
    // field from the card as the editor had loaded it, so the warranty went back to 24.
    [PostgreSqlTheory]
    [MemberData(nameof(Positions))]
    public async Task TheEditorsAfterCommitWrite_PutsBackNoValueAnotherSaveWrote(bool copies)
    {
        using var position = ModelFieldCopies.Use(copies);
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var other = new AfterFirstCommit(async () =>
        {
            await using var db = fixture.CreateContext();
            var bulk = await Service(db).BulkSetFieldAsync(seeded.Tenant.Id, new BulkProductFieldCommand([seeded.Cards[1].Id], BulkProductFields.WarrantyMonths, "6"), CancellationToken.None);
            Assert.True(bulk.Succeeded, bulk.Error?.Message);
        });
        await using (var editor = fixture.CreateContextWith(other))
        {
            var shown = (await Service(editor).GetProductAsync(seeded.Tenant.Id, seeded.Cards[0].Id, CancellationToken.None)).Value!;
            editor.ChangeTracker.Clear();
            other.Armed = true;
            var saved = await Service(editor).UpdateProductAsync(seeded.Tenant.Id, shown.Id, shown.Version, new UpdateProductCommand(shown.Title, "Editörün açıklaması", shown.BrandId, shown.CategoryId,
                WarrantyMonths: shown.WarrantyMonths, VatRate: shown.VatRate, VatInclusion: shown.VatInclusion, DeliveryDays: shown.DeliveryDays, FamilySharedFields: ProductFamilySharedFields.All), CancellationToken.None);
            Assert.True(saved.Succeeded, saved.Error?.Message);
            Assert.True(other.Ran);
        }
        await using var check = fixture.CreateContext();
        var model = await check.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        Assert.Equal(("Editörün açıklaması", (int?)6), (model.Description, model.WarrantyMonths));
        Assert.NotNull(model.FieldsSavedAt);
    }

    // 2: a save that fails after EF held back the copy columns (here the model write) is applied by a retry in the same
    // context. Before the fix, with the switch off, the retry saw no change and the owner's description was lost.
    [PostgreSqlTheory]
    [MemberData(nameof(Positions))]
    public async Task AFailedSave_IsAppliedByARetryInTheSameContext(bool copies)
    {
        using var position = ModelFieldCopies.Use(copies);
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var failing = new FailOnce(sql => sql.Contains("UPDATE catalog.product_models", StringComparison.Ordinal) && sql.Contains("\"Description\"", StringComparison.Ordinal));
        await using var db = fixture.CreateContextWith(failing);
        var card = await db.Products.SingleAsync(x => x.Id == seeded.Cards[0].Id);
        await EffectiveCatalog.RebaseAsync(db, CancellationToken.None);
        card.Description = "Yeniden denenen açıklama";
        card.Version++;
        failing.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        Assert.True(failing.Failed);
        await db.SaveChangesAsync();
        await using var check = fixture.CreateContext();
        Assert.Equal("Yeniden denenen açıklama", (await check.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id)).Description);
        Assert.All(await EffectiveCatalog.Products(check).Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync(), x => Assert.Equal("Yeniden denenen açıklama", x.Description));
    }

    // 3 (decision, 27 Nov 2026): with the switch on everything behaves as on main. After switching back from off, a model
    // whose VAT was edited while off has stale VAT copies on its colours, so the nightly gap check opens the informational
    // warning (as main would for any copy that differs). The owner saves each stale colour once, changing nothing: its copy
    // is healed from the model (no field change, nothing marked or queued beyond what the same no-change save queues on a
    // tenant whose copies are fresh), and the next check resolves the warning. No model field is written by any of it.
    [PostgreSqlFact]
    public async Task AfterSwitchingBackOn_TheGapWarningOpens_AndAnOwnerNoChangeSaveHealsTheCopyAndResolvesIt()
    {
        // The catalogue as the switch on wrote it (whatever the run's position).
        using var seeding = ModelFieldCopies.Use(true);
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await AddLinksAsync(seeded);
        using (ModelFieldCopies.Use(false))
        {
            await using var db = fixture.CreateContext();
            var second = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[1].Id);
            var applied = await new ProductTransferService(db, fixture.TimeProvider).ApplyAsync(seeded.Tenant.Id, Guid.CreateVersion7(),
                new ApplyProductTransferCommand([], [new ProductTransferProductChange(second.Id, second.Version, null, 20m)]), $"b2b-{Guid.NewGuid():N}", CancellationToken.None);
            Assert.True(applied.Succeeded, applied.Error?.Message);
        }
        using (ModelFieldCopies.Use(true))
        {
            await using var db = fixture.CreateContext();
            Assert.Equal(3, await db.Products.CountAsync(x => x.TenantId == seeded.Tenant.Id && x.VatRate == 10m));
            var models = await ModelsAsync(db, seeded.Tenant.Id);
            var gaps = await ModelMirror.ReportGapsAsync(db, seeded.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal([ProductModelValues.Vat], Assert.Single(gaps).Fields);
            Assert.Equal(IssueStatus.Open, await GapStatusAsync(db, seeded.Tenant.Id));

            var queued = new List<string>();
            foreach (var card in seeded.Cards) queued.Add(await NoChangeSaveAsync(db, card.Id));
            Assert.Equal(3, await db.Products.CountAsync(x => x.TenantId == seeded.Tenant.Id && x.VatRate == 20m));
            Assert.Equal(models, await ModelsAsync(db, seeded.Tenant.Id));
            Assert.Empty(await ModelMirror.ReportGapsAsync(db, seeded.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.Equal(IssueStatus.Resolved, await GapStatusAsync(db, seeded.Tenant.Id));

            // The same no-change saves on a tenant whose copies are fresh queue exactly the same.
            var control = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
            await AddLinksAsync(control);
            await using var controlDb = fixture.CreateContext();
            var controlQueued = new List<string>();
            foreach (var card in control.Cards) controlQueued.Add(await NoChangeSaveAsync(controlDb, card.Id));
            Assert.Equal(controlQueued, queued);
        }

        async Task AddLinksAsync(ModelComposerGoldenPostgreSqlTests.Seeded tenant)
        {
            await using var db = fixture.CreateContext();
            foreach (var connection in tenant.Connections)
                foreach (var card in tenant.Cards)
                    db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, ExternalId = $"{connection.PlatformCode}-{card.Id:N}" });
            await db.SaveChangesAsync();
        }
        static async Task<string> ModelsAsync(AppDbContext db, Guid tenantId) =>
            JsonSerializer.Serialize(await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Description, x.VatRate, x.VatInclusion, x.CategoryId, x.WarrantyMonths, x.DeliveryDays, x.Desi, x.BrandId, x.Title }).ToListAsync());
        static Task<IssueStatus> GapStatusAsync(AppDbContext db, Guid tenantId) =>
            db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenantId && x.DedupeKey.StartsWith(ModelMirror.GapDedupePrefix)).Select(x => x.Status).SingleAsync();
    }

    // An editor save of a card as the editor shows it; what it queued: link states, jobs, outbox kinds.
    private async Task<string> NoChangeSaveAsync(AppDbContext db, Guid cardId)
    {
        var tenantId = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == cardId)).TenantId;
        var outboxBefore = await db.IntegrationOutboxEvents.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.Id).ToListAsync();
        var jobsBefore = await db.IntegrationJobs.AsNoTracking().CountAsync(x => x.TenantId == tenantId);
        db.ChangeTracker.Clear();
        var shown = (await Service(db).GetProductAsync(tenantId, cardId, CancellationToken.None)).Value!;
        db.ChangeTracker.Clear();
        var saved = await Service(db).UpdateProductAsync(tenantId, shown.Id, shown.Version, new UpdateProductCommand(shown.Title, shown.Description, shown.BrandId, shown.CategoryId,
            WarrantyMonths: shown.WarrantyMonths, VatRate: shown.VatRate, VatInclusion: shown.VatInclusion, DeliveryDays: shown.DeliveryDays), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        var links = (await db.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => new { x.SyncStatus, x.DirtyFieldsJson, x.Version }).ToListAsync()).Select(x => $"{x.SyncStatus} {x.DirtyFieldsJson} v{x.Version}").Order(StringComparer.Ordinal);
        var outbox = (await db.IntegrationOutboxEvents.AsNoTracking().Where(x => x.TenantId == tenantId && !outboxBefore.Contains(x.Id)).Select(x => x.AggregateType + ":" + x.OperationType).ToListAsync()).Order(StringComparer.Ordinal);
        var jobs = await db.IntegrationJobs.AsNoTracking().CountAsync(x => x.TenantId == tenantId) - jobsBefore;
        return $"links {string.Join("|", links)}\njobs +{jobs}\noutbox {string.Join(",", outbox)}";
    }

    private sealed class AfterFirstCommit(Func<Task> action) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Ran { get; private set; }

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed && !Ran)
            {
                Ran = true;
                await action();
            }
            await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    private sealed class FailOnce(Func<string, bool> matches) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Failed { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Failed && matches(command.CommandText))
            {
                Failed = true;
                throw new InvalidOperationException("Test: the model write failed once.");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
