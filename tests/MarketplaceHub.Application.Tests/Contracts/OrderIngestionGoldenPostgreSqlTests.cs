using System.Net;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 3b (16 Nov 2026): the safety net for moving the order ingestion out of the job processor. Each
// marketplace's order-page contract fixture is mapped as its contract test maps it and stored through the processor's
// UpsertOrders into a fresh store whose catalog holds a variant for every line; the stored orders, lines, packages,
// stock ledger and issues are summed into a golden (no ids or times). The golden must not change while the code moves.
[Collection("postgres-integration")]
public sealed class OrderIngestionGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    public static TheoryData<string> Pages => new() { "Trendyol", "Hepsiburada", "N11", "Pazarama", "PttAvm", "Shopify" };

    [PostgreSqlTheory]
    [MemberData(nameof(Pages))]
    public async Task AnOrderPageStoresTheSameOrdersLinesPackagesAndStock(string marketplace)
    {
        var (platform, orders) = await MappedPageAsync(marketplace);
        Assert.NotEmpty(orders);
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"golden-{marketplace.ToLowerInvariant()}-{Guid.NewGuid():N}"[..30], DisplayName = "golden", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = marketplace, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            // One variant with ten in stock for every line code the page carries, so the lines match and move stock.
            var codes = orders.SelectMany(order => order.Lines).Select(line => (Sku: line.Sku.Trim(), Barcode: line.Barcode?.Trim()))
                .Where(code => code.Sku.Length > 0).DistinctBy(code => CatalogKeyNormalizer.Key(code.Sku)).ToList();
            foreach (var (sku, barcode) in codes)
            {
                var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = sku, Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
                var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = CatalogKeyNormalizer.Key(sku), Barcode = barcode, BarcodeNormalized = string.IsNullOrWhiteSpace(barcode) ? null : CatalogKeyNormalizer.Key(barcode), OptionSignature = sku, CreatedAt = now, UpdatedAt = now };
                seed.Products.Add(product);
                seed.ProductVariants.Add(variant);
                seed.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 10m, Available = 10m });
            }
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            await processor.UpsertOrders(tenant.Id, connection.Id, orders, CancellationToken.None);
        }

        await using var check = fixture.CreateContext();
        var stored = (await check.Orders.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync()).OrderBy(x => x.ExternalOrderId, StringComparer.Ordinal).ToList();
        var lines = await check.OrderLines.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
        var packages = await check.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
        var ledger = await check.StockLedgerEntries.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
        var inventory = await check.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
        var issues = await check.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Code).ToListAsync();
        var summary = new
        {
            Orders = stored.Select(order => new
            {
                order.ExternalOrderId,
                order.DerivedStatus,
                order.Currency,
                order.GrossAmount,
                order.DiscountAmount,
                order.NetAmount,
                order.StoreFrontCode,
                Lines = lines.Where(line => line.OrderId == order.Id).OrderBy(line => line.Sku, StringComparer.Ordinal).ThenBy(line => line.OrderedQuantity).ThenBy(line => line.VariantId is not null).ThenBy(line => line.CancelledQuantity).ThenBy(line => line.ShippedQuantity).ThenBy(line => line.DeliveredQuantity).ThenBy(line => line.ReturnedQuantity)
                    .Select(line => new { line.Sku, Matched = line.VariantId is not null, line.OrderedQuantity, line.CancelledQuantity, line.ShippedQuantity, line.DeliveredQuantity, line.ReturnedQuantity }).ToList(),
                Packages = packages.Where(package => package.OrderId == order.Id).Select(package => package.Status.ToString()).Order(StringComparer.Ordinal).ToList()
            }).ToList(),
            LineCount = lines.Count,
            PackageCount = packages.Count,
            Ledger = ledger.GroupBy(entry => entry.MovementType).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new { MovementType = group.Key, Entries = group.Count(), Quantity = group.Sum(entry => entry.QuantityDelta) }).ToList(),
            OnHand = inventory.Sum(item => item.OnHand),
            Reserved = inventory.Sum(item => item.Reserved),
            Available = inventory.Sum(item => item.Available),
            Issues = issues.Order(StringComparer.Ordinal).ToList()
        };
        ContractFixture.MatchesGolden($"{marketplace}/orders-ingest.golden.json", summary);
    }

    private static async Task<(string Platform, IReadOnlyList<RemoteOrder> Orders)> MappedPageAsync(string marketplace)
    {
        switch (marketplace)
        {
            case "Trendyol":
                return ("TRENDYOL", TrendyolJsonMapper.Orders(ContractFixture.Read("Trendyol/shipmentPackages-docs-sample.json")).Items);
            case "Hepsiburada":
                {
                    using var document = ContractFixture.Json("Hepsiburada/orders-paid-list.json");
                    var (items, _) = HepsiburadaJsonMapper.OrderPage(document.RootElement);
                    return ("HEPSIBURADA", items.Select(item => HepsiburadaJsonMapper.PaidOrderLine(item, out _)).Where(order => order is not null).Select(order => order!).ToList());
                }
            case "N11":
                return ("N11", N11OrderMapper.Packages(ContractFixture.Read("N11/shipmentPackages-docs-sample.json")).Items);
            case "Pazarama":
                {
                    using var document = ContractFixture.Json("Pazarama/orders-docs-sample.json");
                    return ("PAZARAMA", PazaramaOrderMapper.Page(document.RootElement, 100).Items);
                }
            case "PttAvm":
                {
                    var http = new CannedHttp(HttpStatusCode.OK, ContractFixture.Read("PttAvm/SiparisKontrolListesiV2.xml"), "text/xml");
                    var client = new PttAvmHttpClient(http, null!, Options.Create(new PttAvmOptions()), TimeProvider.System, NullLogger<PttAvmHttpClient>.Instance, new ConfigurationBuilder().Build());
                    var account = new PttAvmRequestContext(new PlatformConnection { Id = Guid.Parse("00000000-0000-4000-8000-000000000a01"), PlatformCode = "PTTAVM", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "PttAVM", ExternalStoreId = "900001", ApiVersion = "v1" }, "fake-user", "fake-password");
                    var result = await client.ReadOrdersAsync(account, new DateTime(2024, 10, 1), new DateTime(2024, 11, 5), CancellationToken.None);
                    return ("PTTAVM", result.Value!.Items);
                }
            default:
                {
                    using var document = ContractFixture.Json("Shopify/orders-page.json");
                    var nodes = document.RootElement.GetProperty("data").GetProperty("orders").GetProperty("nodes").EnumerateArray().ToList();
                    return ("SHOPIFY", nodes.Select(node => ShopifyHttpClient.MapOrder(node, "https://fake-shop.example.test")).ToList());
                }
        }
    }
}
