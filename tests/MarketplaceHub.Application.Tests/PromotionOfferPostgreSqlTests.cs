using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The promotion offers end to end on PostgreSQL: a file is stored by its kind, a new one of the same kind replaces it,
// and the detail prices each offer with the panel's cost and the commission tariff in force.
[Collection("postgres-integration")]
public sealed class PromotionOfferPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task OffersAreStoredByKindAndPricedWithTheCostAndTheTariff()
    {
        var (tenantId, connectionId) = await SeedAsync();
        try
        {
            var advantage = CommissionTariffTests.Xlsx(["ÜRÜN İSMİ", "BARKOD", "BEDEN", "MODEL KODU", "STOK", "KOMİSYON TARİFESİ", "1 YILDIZ ÜST FİYAT", "1 YILDIZ ALT FİYAT", "2 YILDIZ ÜST FİYAT", "2 YILDIZ ALT FİYAT", "3 YILDIZ ÜST FİYAT", "MÜŞTERİNİN GÖRDÜĞÜ FİYAT"],
                [["Bluz", "RYP00101", "M", "RYP001", "11.0", "Var", "470,63", "435,98", "435,97", "388,64", "388,63", "465,92"]]);
            var plus = (string limit) => CommissionTariffTests.Xlsx(["Barkod", "Satıcı Stok Kodu", "Beden", "Model Kodu", "Komisyona Esas Fiyat", "Güncel Komisyon", "Plus Fiyat Üst Limiti", "Tarih Aralığı (7 Gün)", "Plus Komisyon Teklifi"],
                [["RYP00101", "RYP00101", "M", "RYP001", "465.92", "20.6", limit, "22 Ekim 08.00-29 Ekim 07.59", "14.1"]]);
            await using (var db = fixture.CreateContext())
            {
                var service = new PromotionOfferService(db, TimeProvider.System);
                var uploaded = await service.UploadAsync(tenantId, null, connectionId, "avantajli.xlsx", new MemoryStream(advantage), "t-1", CancellationToken.None);
                Assert.Null(uploaded.Error);
                Assert.Equal((PromotionOfferKinds.Advantage, 1, 1), (uploaded.Value!.Offer.Kind, uploaded.Value.Offer.RowCount, uploaded.Value.Offer.MatchedRowCount));
                Assert.Null((await service.UploadAsync(tenantId, null, connectionId, "plus-1.xlsx", new MemoryStream(plus("359.02")), "t-2", CancellationToken.None)).Error);
                Assert.Null((await service.UploadAsync(tenantId, null, connectionId, "plus-2.xlsx", new MemoryStream(plus("400.00")), "t-3", CancellationToken.None)).Error);
            }
            await using (var db = fixture.CreateContext())
            {
                var service = new PromotionOfferService(db, TimeProvider.System);
                var list = (await service.ListAsync(tenantId, connectionId, CancellationToken.None)).Value!;
                Assert.Equal(["ADVANTAGE", "PLUS"], list.Select(x => x.Kind).Order());
                Assert.Equal(2, await db.PromotionOfferFiles.CountAsync(x => x.TenantId == tenantId));

                // The second Plus file replaced the first: 400 at %14,1 pays 56,40; over a 160 cost, 98,34 cargo, 13,19 fee,
                // 3,64 withholding tax and −6,18 net VAT it leaves 74,61.
                var plusRow = Assert.Single((await service.DetailAsync(tenantId, connectionId, PromotionOfferKinds.Plus, CancellationToken.None)).Value!.Plus!);
                Assert.Equal((400m, 56.40m, 74.61m), (plusRow.Plus!.Price, plusRow.Plus.Commission!.Value, plusRow.Plus.Remaining!.Value));
                Assert.Equal((98.34m, 3.64m, -6.18m), (plusRow.Plus.Costs!.Cargo, plusRow.Plus.Costs.Stoppage, plusRow.Plus.Costs.NetVat));
                Assert.Equal("Siyah", plusRow.Color);

                // Each star is priced with the band of the tariff in force: 1 star at 470,63 is band 2 (%20,6).
                var star = Assert.Single((await service.DetailAsync(tenantId, connectionId, PromotionOfferKinds.Advantage, CancellationToken.None)).Value!.Advantage!);
                Assert.Equal(1, star.CurrentStars);
                Assert.Equal([20.6m, 19.4m, 17.5m], star.Stars.Select(x => x.Price.Rate!.Value));
                Assert.Equal(160m, star.CostPrice);

                Assert.True((await service.DeleteAsync(tenantId, null, connectionId, PromotionOfferKinds.Plus, "t-4", CancellationToken.None)).Value);
                Assert.Equal(404, (await service.DetailAsync(tenantId, connectionId, PromotionOfferKinds.Plus, CancellationToken.None)).Error!.Status);
            }
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    private async Task<(Guid TenantId, Guid ConnectionId)> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"promo-{Guid.NewGuid():N}"[..16], DisplayName = "promo", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "promo", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "v1" };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Bluz", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, SortOrder = 0, Sku = "RYP00101", SkuNormalized = "RYP00101", Barcode = "RYP00101", ModelCode = "RYP001", OptionSignature = "Renk: Siyah | Beden: M", CostPrice = 160m, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var tariff = new CommissionTariff { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, PlatformCode = "TRENDYOL", PeriodStart = now.AddDays(-1), PeriodEnd = now.AddDays(6), PeriodText = "hafta", FileName = "tarife.xlsx", FileHash = Guid.NewGuid().ToString("N"), UploadedAt = now, RowCount = 1 };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ProductVariants.Add(variant);
        db.CommissionTariffs.Add(tariff);
        // Its offer's VAT rate and a cargo invoice of the store billing a parcel at 98,34 (the store's cargo tariff).
        db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 500m, SalePrice = 465.92m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE" });
        db.MarketplaceFinanceTransactions.Add(new MarketplaceFinanceTransaction { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, PlatformCode = "TRENDYOL", Source = "OTHER_FINANCIALS", ExternalId = "KRG-1", RequestedType = "DeductionInvoices", Category = MarketplaceFinanceCategories.Cargo, TransactionDate = now.AddDays(-5), RawJson = "{}", FirstSeenAt = now.AddDays(-5), UpdatedAt = now.AddDays(-5) });
        db.MarketplaceCargoInvoiceItems.Add(new MarketplaceCargoInvoiceItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, InvoiceSerialNumber = "KRG-1", LineNo = 1, ShipmentPackageType = "Gönderi Kargo Bedeli", OrderNumber = "o-1", Amount = 98.34m, RawJson = "{}", FetchedAt = now.AddDays(-5) });
        db.CommissionTariffRows.Add(new CommissionTariffRow { Id = Guid.CreateVersion7(), TenantId = tenant.Id, TariffId = tariff.Id, Barcode = "RYP00101", Tier1Min = 470.64m, Tier2Max = 470.63m, Tier2Min = 435.98m, Tier3Max = 435.97m, Tier3Min = 388.64m, Tier4Max = 388.63m, Rate1 = 21.5m, Rate2 = 20.6m, Rate3 = 19.4m, Rate4 = 17.5m });
        await db.SaveChangesAsync();
        return (tenant.Id, connection.Id);
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PromotionOfferFiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceCargoInvoiceItems.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceFinanceTransactions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ChannelOffers.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.CommissionTariffRows.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.CommissionTariffs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductVariants.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Products.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
