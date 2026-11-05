using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A past sale is costed at the cost price and desi its line kept from the variant when it was tied to it
// (OrderLineCostSnapshot), so editing the variant today does not move last month's report.
[Collection("postgres-integration")]
public sealed class ReportCostSnapshotPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset ReportNow = new(2026, 10, 20, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task A_past_periods_product_cost_does_not_change_when_the_variant_cost_is_edited_later()
    {
        var seed = Seed("cs-keep", lineMatched: true, costAtSale: 40m);
        await RunAsync(seed, async () =>
        {
            var before = await ReportAsync(seed.Tenant.Id);
            await using (var db = fixture.CreateContext())
                await db.ProductVariants.Where(item => item.Id == seed.Variant.Id).ExecuteUpdateAsync(setter => setter.SetProperty(item => item.CostPrice, 70m));
            var after = await ReportAsync(seed.Tenant.Id);

            Assert.Equal(80m, before.Cost.CostTotal);
            Assert.Equal(before.Cost.CostTotal, after.Cost.CostTotal);
            Assert.Equal(before.Cost.NetProfit, after.Cost.NetProfit);
        });
    }

    // A line whose variant had no cost when it was tied to it follows the variant's current cost.
    [PostgreSqlFact]
    public async Task A_line_that_kept_no_cost_takes_the_variants_current_cost()
    {
        var seed = Seed("cs-follow", lineMatched: true, costAtSale: null);
        await RunAsync(seed, async () =>
        {
            await using (var db = fixture.CreateContext())
                await db.ProductVariants.Where(item => item.Id == seed.Variant.Id).ExecuteUpdateAsync(setter => setter.SetProperty(item => item.CostPrice, 70m));
            Assert.Equal(140m, (await ReportAsync(seed.Tenant.Id)).Cost.CostTotal);
        });
    }

    // A line matched to its variant by an order sync keeps the variant's cost and desi of that moment.
    [PostgreSqlFact]
    public async Task A_line_matched_by_an_order_sync_keeps_the_variants_cost_and_desi_of_that_moment()
    {
        var seed = Seed("cs-match", lineMatched: false, costAtSale: null);
        await RunAsync(seed, async () =>
        {
            await using (var db = fixture.CreateContext())
            {
                var outcome = await new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider)
                    .RematchUnmatchedOrderLines(seed.Tenant.Id, seed.Connection.Id, CancellationToken.None);
                Assert.Equal(1, outcome.Matched);
            }
            await using (var db = fixture.CreateContext())
            {
                var line = await db.OrderLines.AsNoTracking().SingleAsync(item => item.Id == seed.Line.Id);
                Assert.Equal((seed.Variant.Id, 40m, 2m), (line.VariantId!.Value, line.UnitCostSnapshot!.Value, line.DesiSnapshot!.Value));
            }
            var before = await ReportAsync(seed.Tenant.Id);
            await using (var db = fixture.CreateContext())
                await db.ProductVariants.Where(item => item.Id == seed.Variant.Id).ExecuteUpdateAsync(setter => setter
                    .SetProperty(item => item.CostPrice, 70m).SetProperty(item => item.Desi, 9m));
            Assert.Equal((80m, 80m), (before.Cost.CostTotal, (await ReportAsync(seed.Tenant.Id)).Cost.CostTotal));
            await using (var db = fixture.CreateContext())
                Assert.Equal(2m, (await db.OrderLines.AsNoTracking().SingleAsync(item => item.Id == seed.Line.Id)).DesiSnapshot);
        });
    }

    // The migration fills the lines already tied to a variant with its values as they are now, so today's figures stay;
    // a cost of 0 (not entered) is left null and the line keeps following the variant.
    [PostgreSqlFact]
    public async Task The_migration_backfills_existing_lines_with_the_variants_current_values()
    {
        var seed = Seed("cs-fill", lineMatched: true, costAtSale: null);
        var zero = Seed("cs-zero", lineMatched: true, costAtSale: null, variantCost: 0m, variantDesi: null);
        await RunAsync(seed, () => RunAsync(zero, async () =>
        {
            var before = await ReportAsync(seed.Tenant.Id);
            await using (var db = fixture.CreateContext())
            {
                // Scoped to the two tenants so no other test's rows are written.
                var tenants = $"AND l.\"TenantId\" IN ('{seed.Tenant.Id}', '{zero.Tenant.Id}') AND (v.\"CostPrice\" > 0";
                await db.Database.ExecuteSqlRawAsync(AddOrderLineCostSnapshot.Backfill.Replace("AND (v.\"CostPrice\" > 0", tenants, StringComparison.Ordinal));
                // A second run changes nothing.
                Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(AddOrderLineCostSnapshot.Backfill.Replace("AND (v.\"CostPrice\" > 0", tenants, StringComparison.Ordinal)));
            }
            await using (var db = fixture.CreateContext())
            {
                var filled = await db.OrderLines.AsNoTracking().SingleAsync(item => item.Id == seed.Line.Id);
                Assert.Equal((40m, 2m), (filled.UnitCostSnapshot!.Value, filled.DesiSnapshot!.Value));
                var untouched = await db.OrderLines.AsNoTracking().SingleAsync(item => item.Id == zero.Line.Id);
                Assert.Equal(((decimal?)null, (decimal?)null), (untouched.UnitCostSnapshot, untouched.DesiSnapshot));
            }
            Assert.Equal(before.Cost.CostTotal, (await ReportAsync(seed.Tenant.Id)).Cost.CostTotal);
        }));
    }

    private sealed record Seeded(Tenant Tenant, PlatformConnection Connection, Product Product, ProductVariant Variant, Order Order, OrderLine Line);

    private static Seeded Seed(string code, bool lineMatched, decimal? costAtSale, decimal? variantCost = 40m, decimal? variantDesi = 2m)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..24], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = ReportNow, UpdatedAt = ReportNow, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"s-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kılıf", Description = "", Status = ProductStatus.Active, CreatedAt = ReportNow, UpdatedAt = ReportNow };
        var sku = $"CS-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, OptionSignature = "Renk: Siyah", CostPrice = variantCost, Desi = variantDesi, CreatedAt = ReportNow, UpdatedAt = ReportNow };
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ExternalOrderId = $"{code}-order",
            OrderNumber = $"{code}-order",
            Currency = "TRY",
            GrossAmount = 200m,
            NetAmount = 200m,
            OrderedAt = Noon,
            LastRemoteModifiedAt = Noon,
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = ReportNow,
            UpdatedAt = ReportNow,
            Version = 1
        };
        var line = new OrderLine
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            OrderId = order.Id,
            VariantId = lineMatched ? variant.Id : null,
            ExternalLineId = $"{code}-1",
            Sku = sku,
            TitleSnapshot = "Kılıf",
            SourceSnapshotJson = """{"commission":20}""",
            OrderedQuantity = 2m,
            UnitPrice = 100m,
            VatRate = 20m,
            RawStatus = "Delivered",
            UnitCostSnapshot = costAtSale,
            DesiSnapshot = costAtSale is null ? null : variantDesi
        };
        return new(tenant, connection, product, variant, order, line);
    }

    private async Task RunAsync(Seeded seed, Func<Task> test)
    {
        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(seed.Tenant);
            db.PlatformConnections.Add(seed.Connection);
            db.Products.Add(seed.Product);
            db.ProductVariants.Add(seed.Variant);
            db.Orders.Add(seed.Order);
            db.OrderLines.Add(seed.Line);
            await db.SaveChangesAsync();
        }
        try
        {
            await test();
        }
        finally
        {
            await using var db = fixture.CreateContext();
            var tenantId = seed.Tenant.Id;
            await db.StockLedgerEntries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.StockReservations.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ProductVariants.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.Products.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
        }
    }

    private async Task<ReportSalesView> ReportAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        var result = await new ReportsService(db, new FixedClock(ReportNow)).SalesAsync(tenantId, new ReportSalesQuery(Day, Day, null, null, null, false), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
