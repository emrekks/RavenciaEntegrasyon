using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// VERITABANI-DAGITIM-10: the attribute and picture-place uniques are NULLS NOT DISTINCT (ScanFixesSchema), so a product-level
// row (VariantId null) or a free-text attribute (ValueId null) cannot be stored twice; the editor save that loses that race
// answers "saved at the same moment" instead of the stock code / barcode message. Rows go in without their parents
// (session_replication_role = replica, local to a rolled-back transaction): unique indexes still hold there.
[Collection("postgres-integration")]
public sealed class CatalogNullsNotDistinctPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ASecondProductLevelTextAttributeIsRefusedWith23505()
    {
        var tenant = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var attribute = Guid.CreateVersion7();
        var duplicate = await InsertTwiceAsync(id => $"""
            INSERT INTO catalog.product_attribute_assignments ("Id", "TenantId", "ProductId", "VariantId", "AttributeId", "ValueId", "TextValue", "SortOrder", "Version")
            VALUES ('{id}', '{tenant}', '{product}', NULL, '{attribute}', NULL, 'Pamuk', 0, 1)
            """);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("product_attribute_assignments", duplicate.TableName);
        AssertConcurrentSaveMessage(duplicate);
    }

    [PostgreSqlFact]
    public async Task ASecondProductLevelPictureInTheSamePlaceIsRefusedWith23505()
    {
        var tenant = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var duplicate = await InsertTwiceAsync(id => $"""
            INSERT INTO catalog.product_media ("Id", "TenantId", "ProductId", "VariantId", "FileAssetId", "MediaRole", "SortOrder", "Status")
            VALUES ('{id}', '{tenant}', '{product}', NULL, '{Guid.CreateVersion7()}', 'GALLERY', 1, 'ACTIVE')
            """);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("product_media", duplicate.TableName);
        AssertConcurrentSaveMessage(duplicate);
    }

    [Fact]
    public void AStockCodeTakenElsewhereKeepsItsOwnMessage()
    {
        var taken = new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, tableName: "product_variants");
        var result = CatalogService.SaveConflict<ProductView>(new DbUpdateException("save", taken));
        Assert.NotNull(result);
        Assert.Equal("VARIANT_CODE_CONFLICT_REVIEW_REQUIRED", result.Error?.Code);
    }

    private static void AssertConcurrentSaveMessage(PostgresException duplicate)
    {
        var result = CatalogService.SaveConflict<ProductView>(new DbUpdateException("save", duplicate));
        Assert.NotNull(result);
        Assert.Equal("SAVE_CONFLICT_RETRY", result.Error?.Code);
        Assert.Equal(409, result.Error?.Status);
        Assert.Contains("aynı anda", result.Error?.Message, StringComparison.Ordinal);
    }

    private async Task<PostgresException> InsertTwiceAsync(Func<Guid, string> insert)
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
        await db.Database.ExecuteSqlRawAsync(insert(Guid.CreateVersion7()));
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(insert(Guid.CreateVersion7())));
        await transaction.RollbackAsync();
        return duplicate;
    }
}
