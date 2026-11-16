using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-2 (15 Nov 2026): a catalog import whose marketplace reports no VAT (Shopify never does) keeps the offer's VAT
// instead of writing 0; a new offer takes the card's VAT, 0 only when neither is known.
public sealed class CatalogImportVatTests
{
    [Fact]
    public void A_missing_remote_vat_keeps_the_offers_vat()
    {
        Assert.Equal((20m, false), CatalogImportService.CatalogOfferVatRate(remote: null, offer: 20m, card: 10m));
    }

    [Fact]
    public void A_reported_remote_vat_wins()
    {
        Assert.Equal((10m, false), CatalogImportService.CatalogOfferVatRate(remote: 10m, offer: 20m, card: 20m));
        Assert.Equal((0m, false), CatalogImportService.CatalogOfferVatRate(remote: -1m, offer: 20m, card: null));
    }

    [Fact]
    public void A_new_offer_takes_the_cards_vat_and_is_unknown_only_without_one()
    {
        Assert.Equal((20m, false), CatalogImportService.CatalogOfferVatRate(remote: null, offer: null, card: 20m));
        Assert.Equal((0m, true), CatalogImportService.CatalogOfferVatRate(remote: null, offer: null, card: null));
    }
}

// ISLEYICI-2, the raw import / mapping path (SeedChannelOfferAsync): a new offer takes the marketplace's VAT, else the
// card's, else 0 with the one OFFER_VAT_UNKNOWN issue of the connection.
[Collection("postgres-integration")]
public sealed class CatalogImportVatPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task A_new_imported_offer_takes_the_remote_then_the_cards_vat_and_reports_only_an_unknown_one()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"vat-seed-{Guid.NewGuid():N}", DisplayName = "vat", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connectionId = Guid.CreateVersion7();
        var withVat = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Bluz", Description = "Penye", VatRate = 10m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var withoutVat = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Etek", Description = "Penye", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        ProductVariant Variant(Product product, string code) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = code, SkuNormalized = code, Barcode = code, BarcodeNormalized = code, OptionSignature = code, CreatedAt = now, UpdatedAt = now };
        var carded = Variant(withVat, $"VS-{Guid.NewGuid():N}"[..16]);
        var bare = Variant(withoutVat, $"VB-{Guid.NewGuid():N}"[..16]);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.AddRange(withVat, withoutVat);
            seed.ProductVariants.AddRange(carded, bare);
            await seed.SaveChangesAsync();
        }
        try
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), fixture.TimeProvider);

            Assert.Equal(20m, await processor.NewImportedOfferVatRateAsync(tenant.Id, connectionId, bare.Id, 20m, CancellationToken.None));
            Assert.Equal(10m, await processor.NewImportedOfferVatRateAsync(tenant.Id, connectionId, carded.Id, null, CancellationToken.None));
            Assert.Empty(db.OperationalIssues.Local);
            Assert.Equal(0m, await processor.NewImportedOfferVatRateAsync(tenant.Id, connectionId, bare.Id, null, CancellationToken.None));
            Assert.Equal(0m, await processor.NewImportedOfferVatRateAsync(tenant.Id, connectionId, bare.Id, null, CancellationToken.None));
            var issue = Assert.Single(db.OperationalIssues.Local);
            Assert.Equal("OFFER_VAT_UNKNOWN", issue.Code);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.OperationalIssues.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.ProductVariants.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Products.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }
}
