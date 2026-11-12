using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A newly uploaded file replaces the one before it on the Promosyon page, in the same save as its import: a commission
// tariff replaces the connection's tariffs of the same days and those whose days have passed (with their rows, picks
// and kept file), a promotion offer the connection's offer of the same kind. A failed import keeps the old file, and
// another connection or another kind is never touched.
[Collection("postgres-integration")]
public sealed class PromotionFileReplacePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly string[] TariffHeaders = ["ÜRÜN İSMİ", "BARKOD", "SATICI STOK KODU", "1.Fiyat Alt Limit", "2.Fiyat Üst Limiti", "2.Fiyat Alt Limit", "3.Fiyat Üst Limiti", "3.Fiyat Alt Limit", "4.Fiyat Üst Limiti", "Tarih aralığı (7 Gün)", "1.KOMİSYON", "2.KOMİSYON", "3.KOMİSYON", "4.KOMİSYON", "KOMİSYONA ESAS FİYAT", "GÜNCEL KOMİSYON", "GÜNCEL TSF"];

    private static byte[] Tariff(string period, string rate) => CommissionTariffTests.Xlsx(TariffHeaders,
        [["Bluz", "RYP00101", "RYP00101", "470.64", "470.63", "435.98", "435.97", "388.64", "388.63", period, rate, "20.6", "19.4", "17.5", "519.75", "21.5", "525.0"]]);

    private static byte[] Plus(string limit) => CommissionTariffTests.Xlsx(["Barkod", "Satıcı Stok Kodu", "Beden", "Model Kodu", "Komisyona Esas Fiyat", "Güncel Komisyon", "Plus Fiyat Üst Limiti", "Tarih Aralığı (7 Gün)", "Plus Komisyon Teklifi"],
        [["RYP00101", "RYP00101", "M", "RYP001", "465.92", "20.6", limit, "22 Ekim 08.00-29 Ekim 07.59", "14.1"]]);

    private static readonly byte[] Advantage = CommissionTariffTests.Xlsx(["ÜRÜN İSMİ", "BARKOD", "BEDEN", "MODEL KODU", "STOK", "KOMİSYON TARİFESİ", "1 YILDIZ ÜST FİYAT", "1 YILDIZ ALT FİYAT", "2 YILDIZ ÜST FİYAT", "2 YILDIZ ALT FİYAT", "3 YILDIZ ÜST FİYAT", "MÜŞTERİNİN GÖRDÜĞÜ FİYAT"],
        [["Bluz", "RYP00101", "M", "RYP001", "11.0", "Var", "470,63", "435,98", "435,97", "388,64", "388,63", "465,92"]]);

    private static readonly byte[] NotAnOfferFile = CommissionTariffTests.Xlsx(["Ürün", "Fiyat"], [["a", "1"]]);

    [PostgreSqlFact]
    public async Task CommissionTariff_ANewFileReplacesTheSameDaysAndThePastOnes_AFailedImportKeepsTheOld()
    {
        var (tenantId, first, second) = await SeedAsync();
        try
        {
            var week = new FixedClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
            Guid oldId;
            await using (var db = fixture.CreateContext())
            {
                var service = new CommissionTariffService(db, week);
                var a = await service.UploadAsync(tenantId, null, first, "a.xlsx", new MemoryStream(Tariff("22 Ekim 08.00-29 Ekim 07.59", "21.5")), "t-1", CancellationToken.None);
                Assert.True(a.Succeeded, a.Error?.Message);
                oldId = Assert.Single(a.Value!.Tariffs).Id;
                Assert.True((await service.UploadAsync(tenantId, null, second, "a.xlsx", new MemoryStream(Tariff("22 Ekim 08.00-29 Ekim 07.59", "21.5")), "t-2", CancellationToken.None)).Succeeded);

                // A file that does not read changes nothing.
                Assert.Equal("COMMISSION_TARIFF_FILE_INVALID", (await service.UploadAsync(tenantId, null, first, "bozuk.xlsx", new MemoryStream(NotAnOfferFile), "t-3", CancellationToken.None)).Error?.Code);
            }
            await using (var db = fixture.CreateContext())
            {
                Assert.True(await db.CommissionTariffRows.AnyAsync(x => x.TenantId == tenantId && x.TariffId == oldId));
                Assert.True(await db.CommissionTariffSourceFiles.AnyAsync(x => x.TenantId == tenantId && x.TariffId == oldId));

                // The week's tariff issued again replaces the first one with its rows and kept file.
                var b = await new CommissionTariffService(db, week).UploadAsync(tenantId, null, first, "b.xlsx", new MemoryStream(Tariff("22 Ekim 08.00-29 Ekim 07.59", "22.0")), "t-4", CancellationToken.None);
                Assert.True(b.Succeeded, b.Error?.Message);
                Assert.Contains(b.Value!.Warnings, x => x.Contains("a.xlsx", StringComparison.Ordinal));
            }
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(["b.xlsx"], await db.CommissionTariffs.Where(x => x.TenantId == tenantId && x.ConnectionId == first).Select(x => x.FileName).ToListAsync());
                Assert.False(await db.CommissionTariffRows.AnyAsync(x => x.TenantId == tenantId && x.TariffId == oldId));
                Assert.False(await db.CommissionTariffSourceFiles.AnyAsync(x => x.TenantId == tenantId && x.TariffId == oldId));
                Assert.Equal(22.0m, (await db.CommissionTariffRows.SingleAsync(x => x.TenantId == tenantId && db.CommissionTariffs.Any(t => t.Id == x.TariffId && t.ConnectionId == first))).Rate1);
                // The other connection keeps its own.
                Assert.Equal(["a.xlsx"], await db.CommissionTariffs.Where(x => x.TenantId == tenantId && x.ConnectionId == second).Select(x => x.FileName).ToListAsync());
                Assert.True(await db.AuditLogs.AnyAsync(x => x.TenantId == tenantId && x.Action == "COMMISSION_TARIFF_REPLACED"));

                // Next week's file uploaded while this week's is in force leaves this week's.
                Assert.True((await new CommissionTariffService(db, week).UploadAsync(tenantId, null, first, "c.xlsx", new MemoryStream(Tariff("29 Ekim 08.00-5 Kasım 07.59", "21.5")), "t-5", CancellationToken.None)).Succeeded);
            }
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(["b.xlsx", "c.xlsx"], await db.CommissionTariffs.Where(x => x.TenantId == tenantId && x.ConnectionId == first).OrderBy(x => x.PeriodStart).Select(x => x.FileName).ToListAsync());

                // A file uploaded once their days have passed replaces both.
                var later = new FixedClock(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
                Assert.True((await new CommissionTariffService(db, later).UploadAsync(tenantId, null, first, "d.xlsx", new MemoryStream(Tariff("5 Kasım 08.00-12 Kasım 07.59", "21.5")), "t-6", CancellationToken.None)).Succeeded);
            }
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(["d.xlsx"], await db.CommissionTariffs.Where(x => x.TenantId == tenantId && x.ConnectionId == first).Select(x => x.FileName).ToListAsync());
                Assert.Equal(1, await db.CommissionTariffRows.CountAsync(x => x.TenantId == tenantId && db.CommissionTariffs.Any(t => t.Id == x.TariffId && t.ConnectionId == first)));
                Assert.Equal(["a.xlsx"], await db.CommissionTariffs.Where(x => x.TenantId == tenantId && x.ConnectionId == second).Select(x => x.FileName).ToListAsync());
            }
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    [PostgreSqlFact]
    public async Task PromotionOffer_ANewFileReplacesTheSameKindOnly_AFailedImportKeepsTheOld()
    {
        var (tenantId, first, second) = await SeedAsync();
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var service = new PromotionOfferService(db, TimeProvider.System);
                Assert.Null((await service.UploadAsync(tenantId, null, first, "plus-a.xlsx", new MemoryStream(Plus("359.02")), "t-1", CancellationToken.None)).Error);
                Assert.Null((await service.UploadAsync(tenantId, null, first, "avantaj.xlsx", new MemoryStream(Advantage), "t-2", CancellationToken.None)).Error);
                Assert.Null((await service.UploadAsync(tenantId, null, second, "plus-a.xlsx", new MemoryStream(Plus("359.02")), "t-3", CancellationToken.None)).Error);
                Assert.Equal("PROMOTION_OFFER_FILE_INVALID", (await service.UploadAsync(tenantId, null, first, "bozuk.xlsx", new MemoryStream(NotAnOfferFile), "t-4", CancellationToken.None)).Error?.Code);
            }
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(["avantaj.xlsx", "plus-a.xlsx"], await db.PromotionOfferFiles.Where(x => x.TenantId == tenantId && x.ConnectionId == first).OrderBy(x => x.FileName).Select(x => x.FileName).ToListAsync());
                Assert.Null((await new PromotionOfferService(db, TimeProvider.System).UploadAsync(tenantId, null, first, "plus-b.xlsx", new MemoryStream(Plus("400.00")), "t-5", CancellationToken.None)).Error);
            }
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(["avantaj.xlsx", "plus-b.xlsx"], await db.PromotionOfferFiles.Where(x => x.TenantId == tenantId && x.ConnectionId == first).OrderBy(x => x.FileName).Select(x => x.FileName).ToListAsync());
                Assert.Equal(["plus-a.xlsx"], await db.PromotionOfferFiles.Where(x => x.TenantId == tenantId && x.ConnectionId == second).Select(x => x.FileName).ToListAsync());
            }
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    private async Task<(Guid TenantId, Guid First, Guid Second)> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"repl-{Guid.NewGuid():N}"[..16], DisplayName = "replace", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        PlatformConnection Connection(string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = name, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "v1" };
        var first = Connection("first");
        var second = Connection("second");
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(first, second);
        await db.SaveChangesAsync();
        return (tenant.Id, first.Id, second.Id);
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PromotionOfferFiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.CommissionTariffRows.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.CommissionTariffSourceFiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.CommissionTariffs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
