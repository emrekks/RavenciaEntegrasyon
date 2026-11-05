using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// FillVariantOptionRowsFromSignature on PostgreSQL: a colour card whose Mavi rows (live: MZ021Y-CD-25..27) carry
// "Renk: Mavi | Beden: …" but no option value row get their rows under the card's own options; a row whose size the card
// lacks, a row that already holds a row and a row without a signature stay as they are; a second run adds nothing.
[Collection("postgres-integration")]
public sealed class FillVariantOptionRowsFromSignaturePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Fills_the_rows_a_signature_names_once_and_leaves_the_rest()
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"sig-fill-{Guid.NewGuid():N}", DisplayName = "sig-fill", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var card = Guid.CreateVersion7();
        var renk = Guid.CreateVersion7();
        var beden = Guid.CreateVersion7();
        var mavi = Guid.CreateVersion7();
        var bordo = Guid.CreateVersion7();
        var l = Guid.CreateVersion7();
        var xl = Guid.CreateVersion7();
        ProductVariant Row(string sku, string signature) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card, Sku = sku, SkuNormalized = sku, Barcode = sku, BarcodeNormalized = sku, ModelCode = "MZ021Y-CD", OptionSignature = signature, Status = ProductStatus.Active, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 3 };
        var maviL = Row("MZ021Y-CD-25", "Renk: Mavi | Beden: L");
        var maviXl = Row("MZ021Y-CD-26", "Renk: mavi | Beden: XL");
        var maviNoSize = Row("MZ021Y-CD-30", "Renk: Mavi | Beden: 5XL");
        var bordoHalf = Row("MZ021Y-CD-02", "Renk: Bordo | Beden: XL");
        var plain = Row("MZ021Y-CD-99", "-");

        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.Products.Add(new Product { Id = card, TenantId = tenant.Id, Title = "Kadın İndigo Bluz", Description = "Bluz", Status = ProductStatus.Active, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 10 });
            db.ProductVariants.AddRange(maviL, maviXl, maviNoSize, bordoHalf, plain);
            db.ProductOptions.AddRange(
                new ProductOption { Id = renk, TenantId = tenant.Id, ProductId = card, Label = "Renk", NormalizedKey = "RENK", SortOrder = 0 },
                new ProductOption { Id = beden, TenantId = tenant.Id, ProductId = card, Label = "Beden", NormalizedKey = "BEDEN", SortOrder = 1 });
            db.ProductOptionValues.AddRange(
                new ProductOptionValue { Id = mavi, TenantId = tenant.Id, OptionId = renk, Label = "Mavi", NormalizedKey = "MAVI" },
                new ProductOptionValue { Id = bordo, TenantId = tenant.Id, OptionId = renk, Label = "Bordo", NormalizedKey = "BORDO", SortOrder = 1 },
                new ProductOptionValue { Id = l, TenantId = tenant.Id, OptionId = beden, Label = "L", NormalizedKey = "L" },
                new ProductOptionValue { Id = xl, TenantId = tenant.Id, OptionId = beden, Label = "XL", NormalizedKey = "XL", SortOrder = 1 });
            db.VariantOptionValues.Add(new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = bordoHalf.Id, OptionId = renk, OptionValueId = bordo });
            await db.SaveChangesAsync();
        }

        try
        {
            // Twice in one transaction, as in EF's migration transaction, then once more on its own.
            var migration = new FillVariantOptionRowsFromSignature();
            await using (var db = fixture.CreateContext())
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                for (var run = 0; run < 2; run++)
                    foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
                        await db.Database.ExecuteSqlRawAsync(operation.Sql);
                await transaction.CommitAsync();
            }
            await using (var db = fixture.CreateContext())
                foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
                    await db.Database.ExecuteSqlRawAsync(operation.Sql);

            await using var check = fixture.CreateContext();
            var rows = await check.VariantOptionValues.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
            string Options(ProductVariant variant) => string.Join(",", rows.Where(x => x.VariantId == variant.Id)
                .Select(x => $"{(x.OptionId == renk ? "Renk" : x.OptionId == beden ? "Beden" : "?")}={(x.OptionValueId == mavi ? "Mavi" : x.OptionValueId == bordo ? "Bordo" : x.OptionValueId == l ? "L" : x.OptionValueId == xl ? "XL" : "?")}")
                .Order(StringComparer.Ordinal));
            Assert.Equal("Beden=L,Renk=Mavi", Options(maviL));
            Assert.Equal("Beden=XL,Renk=Mavi", Options(maviXl));
            Assert.Equal("", Options(maviNoSize));
            Assert.Equal("Renk=Bordo", Options(bordoHalf));
            Assert.Equal("", Options(plain));
            Assert.Equal(5, rows.Count);

            // No option or value is added, and the rows themselves are untouched.
            Assert.Equal(2, await check.ProductOptions.CountAsync(x => x.TenantId == tenant.Id));
            Assert.Equal(4, await check.ProductOptionValues.CountAsync(x => x.TenantId == tenant.Id));
            var variants = await check.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id);
            Assert.All(variants.Values, variant => Assert.Equal(3L, variant.Version));
            Assert.Equal("Renk: mavi | Beden: XL", variants[maviXl.Id].OptionSignature);

            var audits = await check.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.Action == "VARIANT_OPTIONS_FILLED_FROM_SIGNATURE").Select(x => x.TargetId).ToListAsync();
            Assert.Equal(new[] { maviL.Id, maviXl.Id }.Select(x => x.ToString()).Order(), audits.Order());
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.AuditLogs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.VariantOptionValues.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.ProductOptionValues.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.ProductOptions.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.ProductVariants.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Products.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }
}
