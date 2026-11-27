using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 D5, B1 review: products ("TenantId", "FamilyId") -> product_models is ON DELETE RESTRICT
// (20261127170948_ProductFamilyRestrict). ProductModelLinks deletes "a model without a card" with a READ COMMITTED check; a
// card another save adds and commits while that delete runs is not seen by the check, and the key now refuses the delete with
// restrict_violation (23001), which DeleteModelsWithoutCardsAsync answers by running the delete again (the model stays with the
// new card). The migration goes back and forth and runs again harmlessly.
[Collection("postgres-integration")]
public sealed class ProductFamilyRestrictPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seeded(Guid TenantId, Guid ModelId);

    // A tenant with one model and no card yet (as a model is between its last card leaving and the reconcile deleting it).
    private async Task<Seeded> SeedCardlessModelAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"restrict-{Guid.NewGuid():N}"[..30], DisplayName = "restrict", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var model = new ProductModel { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ModelCode = "MZ700", ModelCodeKey = "MZ700", Title = "Kadın Siyah Etek", Description = "Model açıklaması", VatRate = 10m, VatInclusion = "INCLUDED", CreatedAt = now, UpdatedAt = now };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.ProductModels.Add(model);
        await db.SaveChangesAsync();
        return new(tenant.Id, model.Id);
    }

    private static Task<int> InsertCardAsync(AppDbContext db, Seeded seeded, Guid cardId) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO catalog.products ("Id", "TenantId", "Title", "Description", "Status", "SourcePolicyVersion", "FamilyId", "CreatedAt", "UpdatedAt", "Version")
            VALUES ({cardId}, {seeded.TenantId}, 'Kadın Siyah Etek', 'Model açıklaması', 'Active', 1, {seeded.ModelId}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow}, 1)
            """);

    private static IQueryable<ProductModel> Cardless(AppDbContext db, Seeded seeded) =>
        db.ProductModels.Where(x => x.TenantId == seeded.TenantId && x.Id == seeded.ModelId && !db.Products.Any(product => product.TenantId == seeded.TenantId && product.FamilyId == x.Id));

    // The race: a save adds a card to the model (uncommitted), the reconcile's delete starts (its check sees no card) and
    // waits for the model row; the card commits; the key refuses the delete (no not_null_violation, no lost link), the delete
    // runs again inside a savepoint, now keeps the model, and the reconcile's other work in its transaction stands.
    [PostgreSqlFact]
    public async Task AModelThatGainsACardWhileItsDeleteRuns_IsKept_AndTheDeletersTransactionGoesOn()
    {
        var seeded = await SeedCardlessModelAsync();
        var cardId = Guid.CreateVersion7();

        await using var adder = fixture.CreateContext();
        await using var adding = await adder.Database.BeginTransactionAsync();
        await InsertCardAsync(adder, seeded, cardId);

        await using var deleter = fixture.CreateContext();
        await using var deleting = await deleter.Database.BeginTransactionAsync();
        // Other work of the reconcile's transaction, which must stand.
        await deleter.Database.ExecuteSqlInterpolatedAsync($"""UPDATE iam.tenants SET "DisplayName" = 'restrict-gone-on' WHERE "Id" = {seeded.TenantId}""");
        var deleterPid = (await deleter.Database.SqlQuery<int>($"""SELECT pg_backend_pid() AS "Value" """).ToListAsync()).Single();

        var delete = Task.Run(() => ProductModelLinks.DeleteModelsWithoutCardsAsync(deleter, Cardless(deleter, seeded), CancellationToken.None));
        await using (var watcher = fixture.CreateContext())
        {
            for (var i = 0; i < 200; i++)
            {
                var waiting = (await watcher.Database.SqlQuery<bool>($"""SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {deleterPid} AND wait_event_type = 'Lock') AS "Value" """).ToListAsync()).Single();
                if (waiting || delete.IsCompleted) break;
                await Task.Delay(25);
            }
        }
        Assert.False(delete.IsCompleted, "the delete should wait for the adder's uncommitted card");
        await adding.CommitAsync();

        Assert.Equal(0, await delete);
        await deleting.CommitAsync();

        await using var check = fixture.CreateContext();
        Assert.Equal(seeded.ModelId, (await check.Products.AsNoTracking().SingleAsync(x => x.Id == cardId)).FamilyId);
        Assert.True(await check.ProductModels.AnyAsync(x => x.Id == seeded.ModelId));
        Assert.Equal("restrict-gone-on", (await check.Tenants.AsNoTracking().SingleAsync(x => x.Id == seeded.TenantId)).DisplayName);
    }

    // Without a race the delete goes; a delete that keeps meeting the key stops the save with the Turkish message (the
    // caller's transaction rolls back to before it, so nothing half-done stays).
    [PostgreSqlFact]
    public async Task ACardlessModelIsDeleted_AndAModelTheKeyKeepsRefusing_StopsWithAClearMessage()
    {
        var seeded = await SeedCardlessModelAsync();
        await using (var db = fixture.CreateContext())
            Assert.Equal(1, await ProductModelLinks.DeleteModelsWithoutCardsAsync(db, Cardless(db, seeded), CancellationToken.None));

        var other = await SeedCardlessModelAsync();
        await using var holder = fixture.CreateContext();
        await InsertCardAsync(holder, other, Guid.CreateVersion7());
        await using var deleter = fixture.CreateContext();
        await using var transaction = await deleter.Database.BeginTransactionAsync();
        // A query without the card check: every attempt meets the key (as a race that never ends would).
        var refused = await Assert.ThrowsAsync<ModelRuleException>(() => ProductModelLinks.DeleteModelsWithoutCardsAsync(deleter, deleter.ProductModels.Where(x => x.Id == other.ModelId), CancellationToken.None));
        Assert.Equal(ModelRuleException.ModelGainedCardCode, refused.Code);
        Assert.Contains("Model silinemedi", refused.Message, StringComparison.Ordinal);
        // The transaction is still usable (each attempt rolled back to its savepoint).
        Assert.Equal(1, (await deleter.Database.SqlQuery<int>($"""SELECT 1 AS "Value" """).ToListAsync()).Single());
        await transaction.RollbackAsync();
        Assert.True(await holder.ProductModels.AnyAsync(x => x.Id == other.ModelId));
    }

    // The migration: only SQL; Down puts SET NULL ("FamilyId") back, Up makes it RESTRICT under the EF name with no
    // temporary key left, validated; both run again as no-ops; no row changes.
    [PostgreSqlFact]
    public async Task TheMigration_SwapsTheKeyBackAndForth_AndRunsAgain()
    {
        var seeded = await SeedCardlessModelAsync();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await InsertCardAsync(db, seeded, Guid.CreateVersion7());
        var migration = new MarketplaceHub.Infrastructure.Persistence.Migrations.ProductFamilyRestrict();
        Assert.All(migration.UpOperations.Concat(migration.DownOperations), operation => Assert.IsType<SqlOperation>(operation));
        async Task RunAsync(IEnumerable<MigrationOperation> operations)
        {
            foreach (var operation in operations.Cast<SqlOperation>()) await db.Database.ExecuteSqlRawAsync(operation.Sql);
        }
        async Task<string> KeysAsync() => string.Join(",", await db.Database.SqlQueryRaw<string>("""
            SELECT conname || ':' || confdeltype::text || ':' || convalidated::text || ':' || coalesce(array_to_string(confdelsetcols, ' '), '') AS "Value"
            FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND contype = 'f' AND confrelid = 'catalog.product_models'::regclass ORDER BY conname
            """).ToListAsync());
        var restrict = $"{ProductModelLinks.ProductFamilyKeyName}:r:true:";
        Assert.Equal(restrict, await KeysAsync());
        var rows = await RowsAsync();

        await RunAsync(migration.DownOperations);
        var setNull = await KeysAsync();
        Assert.StartsWith($"{ProductModelLinks.ProductFamilyKeyName}:n:true:", setNull, StringComparison.Ordinal);
        Assert.DoesNotContain(",", setNull, StringComparison.Ordinal);
        await RunAsync(migration.DownOperations);
        Assert.Equal(setNull, await KeysAsync());

        await RunAsync(migration.UpOperations);
        Assert.Equal(restrict, await KeysAsync());
        await RunAsync(migration.UpOperations);
        Assert.Equal(restrict, await KeysAsync());
        Assert.Equal(rows, await RowsAsync());
        await transaction.RollbackAsync();

        async Task<string> RowsAsync() => string.Join(",", (await db.Products.AsNoTracking().Where(x => x.TenantId == seeded.TenantId).Select(x => new { x.Id, x.FamilyId, x.Version }).ToListAsync()).Select(x => $"{x.Id}:{x.FamilyId}:{x.Version}"));
    }
}
