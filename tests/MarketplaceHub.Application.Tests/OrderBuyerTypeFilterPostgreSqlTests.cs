using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The order list's "Fatura Türü" filter gives exactly the answer of the row's Bireysel / Kurumsal label: both read the
// buyer kind stored on the order by the invoice's rule. Orders saved before the column existed are filled in by the
// maintenance backfill, in code, by the same rule.
[Collection("postgres-integration")]
public sealed class OrderBuyerTypeFilterPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task TheInvoiceTypeFilterMatchesTheRowLabelOnEveryMarketplace()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"bt-{Guid.NewGuid():N}"[..16], DisplayName = "bt", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connections = new[] { "TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY" }
            .ToDictionary(code => code, code => new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = code, Environment = "STAGE", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" });
        // (marketplace, customer snapshot, invoice address snapshot, the label's kind)
        var cases = new (string Platform, string Customer, string Address, string Kind)[]
        {
            // Trendyol: commercial decides; empty company fields on an individual order are not corporate.
            ("TRENDYOL", """{"commercial":true}""", """{"company":"Firma A.Ş.","taxNumber":"1234567890","taxOffice":"Kadıköy"}""", OrderBuyerTypeView.Corporate),
            ("TRENDYOL", """{"commercial":false}""", """{"company":"","companyName":"","taxOffice":""}""", OrderBuyerTypeView.Individual),
            ("TRENDYOL", """{"commercial":true}""", """{"company":"Firma A.Ş.","taxNumber":""}""", OrderBuyerTypeView.CorporateTaxIdMissing),
            // Hepsiburada: 11111111111 for an individual even with a company name; a tax number otherwise.
            ("HEPSIBURADA", "{}", """{"companyName":"Test User","identityNo":"11111111111","taxNumber":""}""", OrderBuyerTypeView.Individual),
            ("HEPSIBURADA", "{}", """{"companyName":"Firma Ltd","taxNumber":"1234567890","taxOffice":"Şişli"}""", OrderBuyerTypeView.Corporate),
            // n11, Pazarama, PTT AVM: the marketplace's invoice type.
            ("N11", """{"billingAddress":{"invoiceType":2,"taxId":"1234567890","taxHouse":"Beşiktaş"}}""", "{}", OrderBuyerTypeView.Corporate),
            ("PAZARAMA", "{}", """{"invoiceType":"1","companyName":"X"}""", OrderBuyerTypeView.Individual),
            ("PTTAVM", """{"invoiceType":"Kurumsal"}""", "{}", OrderBuyerTypeView.CorporateTaxIdMissing),
            // Shopify: a company name without a tax number is corporate, its tax number asked at invoicing.
            ("SHOPIFY", "{}", """{"company":"Acme"}""", OrderBuyerTypeView.CorporateTaxIdMissing),
            ("SHOPIFY", "{}", """{"firstName":"Ayşe","lastName":"Yılmaz","tcIdentityNumber":"10000000146"}""", OrderBuyerTypeView.Individual),
        };
        var orders = cases.Select((item, index) => new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connections[item.Platform].Id,
            ExternalOrderId = $"bt-{index}",
            OrderNumber = $"BT{index}",
            Currency = "TRY",
            OrderedAt = now.AddMinutes(-index),
            LastRemoteModifiedAt = now,
            CustomerSnapshotJson = item.Customer,
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = item.Address,
            DerivedStatus = "NEW",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        }).ToList();
        try
        {
            await using (var db = fixture.CreateContext())
            {
                db.Tenants.Add(tenant);
                db.PlatformConnections.AddRange(connections.Values);
                db.Orders.AddRange(orders);
                await db.SaveChangesAsync();
            }
            await using (var db = fixture.CreateContext())
            {
                // A new order gets its kind as it is saved.
                var saved = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id, x => x.BuyerType);
                Assert.All(orders.Select((order, index) => (order, index)), pair => Assert.Equal(cases[pair.index].Kind, saved[pair.order.Id]));
                // Orders saved before the column existed: empty until the backfill fills them by the same rule.
                await db.Orders.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.BuyerType, (string?)null));
                Assert.True(await OrderBuyerTypes.BackfillAsync(db, CancellationToken.None) >= orders.Count);
                Assert.Equal(0, await OrderBuyerTypes.BackfillAsync(db, CancellationToken.None));
                var filled = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id, x => x.BuyerType);
                Assert.All(orders.Select((order, index) => (order, index)), pair => Assert.Equal(cases[pair.index].Kind, filled[pair.order.Id]));
            }

            await using var service = fixture.CreateContext();
            var sales = new MarketplaceSalesService(service, new CursorCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);
            var all = await sales.OrdersAsync(tenant.Id, 100, 1, null, new OrderListQuery(), CancellationToken.None);
            Assert.Equal(cases.Length, all.Items.Count);
            Assert.All(all.Items, row => Assert.Equal(cases[orders.FindIndex(order => order.Id == row.Id)].Kind, row.BuyerType!.Kind));
            var individual = await sales.OrdersAsync(tenant.Id, 100, 1, null, new OrderListQuery(InvoiceType: "BIREYSEL"), CancellationToken.None);
            var corporate = await sales.OrdersAsync(tenant.Id, 100, 1, null, new OrderListQuery(InvoiceType: "KURUMSAL"), CancellationToken.None);
            var taxIdMissing = await sales.OrdersAsync(tenant.Id, 100, 1, null, new OrderListQuery(InvoiceType: "KURUMSAL_VERGI_NO_YOK"), CancellationToken.None);

            Assert.Equal(all.Items.Where(row => row.BuyerType!.Kind == OrderBuyerTypeView.Individual).Select(row => row.Id).Order(), individual.Items.Select(row => row.Id).Order());
            Assert.Equal(all.Items.Where(row => row.BuyerType!.Kind != OrderBuyerTypeView.Individual).Select(row => row.Id).Order(), corporate.Items.Select(row => row.Id).Order());
            Assert.Equal(all.Items.Where(row => row.BuyerType!.Kind == OrderBuyerTypeView.CorporateTaxIdMissing).Select(row => row.Id).Order(), taxIdMissing.Items.Select(row => row.Id).Order());
            Assert.Equal(4, individual.TotalCount);
            Assert.Equal(6, corporate.TotalCount);
            Assert.Equal(3, taxIdMissing.TotalCount);
            Assert.All(individual.Items, row => Assert.Equal("BIREYSEL", row.OrderType));
            Assert.All(corporate.Items, row => Assert.Equal("KURUMSAL", row.OrderType));

            // A changed invoice address moves the order to its new kind in the filter and the label together.
            var moved = orders[1];
            await using (var db = fixture.CreateContext())
            {
                var order = await db.Orders.SingleAsync(x => x.TenantId == tenant.Id && x.Id == moved.Id);
                order.InvoiceAddressSnapshotJson = """{"company":"Yeni Firma","taxNumber":"1234567890"}""";
                order.CustomerSnapshotJson = """{"commercial":true}""";
                await db.SaveChangesAsync();
            }
            service.ChangeTracker.Clear();
            var corporateAfter = await sales.OrdersAsync(tenant.Id, 100, 1, null, new OrderListQuery(InvoiceType: "KURUMSAL"), CancellationToken.None);
            var row = Assert.Single(corporateAfter.Items, item => item.Id == moved.Id);
            Assert.Equal(OrderBuyerTypeView.Corporate, row.BuyerType!.Kind);
            Assert.DoesNotContain((await sales.OrdersAsync(tenant.Id, 100, 1, null, new OrderListQuery(InvoiceType: "BIREYSEL"), CancellationToken.None)).Items, item => item.Id == moved.Id);
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Orders.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    // 16 Nov 2026: the migration RecomputeHepsiburadaBuyerType clears only Hepsiburada orders' stored kind, and the
    // maintenance backfill stores the new rule's answer: the company with 11111111111 beside its VKN becomes Corporate,
    // the individual stays Individual, another marketplace's stored kind and every order's version stay as they were.
    [PostgreSqlFact]
    public async Task TheHepsiburadaRelabelClearsOnlyHepsiburadaOrdersAndTheBackfillStoresTheNewRule()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"hr-{Guid.NewGuid():N}"[..16], DisplayName = "hr", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var hepsiburada = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "HEPSIBURADA", Environment = "STAGE", DisplayName = "hb", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var trendyol = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "ty", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var cases = new (PlatformConnection Connection, string Customer, string Address, string Expected)[]
        {
            (hepsiburada, HepsiburadaCorporateBuyerTests.CorporateCustomer, HepsiburadaCorporateBuyerTests.CorporateInvoiceAddress, OrderBuyerTypeView.Corporate),
            (hepsiburada, "{}", """{"name":"Ayşe Yılmaz","turkishIdentityNumber":"11111111111","taxNumber":"","taxOffice":null}""", OrderBuyerTypeView.Individual),
            // Stored Individual on purpose (not what the rule gives): the migration must leave it alone.
            (trendyol, """{"commercial":true}""", """{"company":"Firma A.Ş.","taxNumber":"1234567890","taxOffice":"Kadıköy"}""", OrderBuyerTypeView.Individual),
        };
        var orders = cases.Select((item, index) => new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = item.Connection.Id,
            ExternalOrderId = $"hr-{index}",
            OrderNumber = $"HR{index}",
            Currency = "TRY",
            OrderedAt = now.AddMinutes(-index),
            LastRemoteModifiedAt = now,
            CustomerSnapshotJson = item.Customer,
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = item.Address,
            DerivedStatus = "NEW",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        }).ToList();
        try
        {
            await using (var db = fixture.CreateContext())
            {
                db.Tenants.Add(tenant);
                db.PlatformConnections.AddRange(hepsiburada, trendyol);
                db.Orders.AddRange(orders);
                await db.SaveChangesAsync();
                // As live held them before the fix: every order stored Individual.
                await db.Orders.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.BuyerType, OrderBuyerTypeView.Individual));
            }
            await using (var db = fixture.CreateContext())
            {
                var versions = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id, x => x.Version);
                // The migration's statement, kept to this test's tenant so parallel tests are not touched.
                Assert.Equal(2, await db.Database.ExecuteSqlRawAsync(
                    MarketplaceHub.Infrastructure.Persistence.Migrations.RecomputeHepsiburadaBuyerType.Clear + " AND o.\"TenantId\" = {0}", tenant.Id));
                var cleared = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id, x => x.BuyerType);
                Assert.Equal([null, null, OrderBuyerTypeView.Individual], orders.Select(order => cleared[order.Id]));

                Assert.True(await OrderBuyerTypes.BackfillAsync(db, CancellationToken.None) >= 2);
                var after = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id, x => new { x.BuyerType, x.Version });
                Assert.Equal(cases.Select(item => item.Expected), orders.Select(order => after[order.Id].BuyerType));
                Assert.All(orders, order => Assert.Equal(versions[order.Id], after[order.Id].Version));
            }
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Orders.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    // SIPARIS-STOK-3: "Fatura Bölgesi" follows the row's micro-export flag (micro = true), not a text search over the
    // package: a domestic company named "... İthalat İhracat Ltd." or a "MICRO FIBER" product stays in Türkiye.
    [PostgreSqlFact]
    public async Task TheInvoiceRegionFilterMatchesTheRowMicroExportFlag()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"rg-{Guid.NewGuid():N}"[..16], DisplayName = "rg", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "rg", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var customers = new[]
        {
            """{"micro":false,"invoiceAddress":{"company":"ABC TEKSTİL İTHALAT İHRACAT LTD. ŞTİ."},"lines":[{"productName":"MICRO FIBER HAVLU"}]}""",
            """{"micro":true,"countryCode":"AZ"}""",
        };
        var orders = customers.Select((customer, index) => new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ExternalOrderId = $"rg-{index}",
            OrderNumber = $"RG{index}",
            Currency = "TRY",
            OrderedAt = now.AddMinutes(-index),
            LastRemoteModifiedAt = now,
            CustomerSnapshotJson = customer,
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "NEW",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        }).ToList();
        try
        {
            await using (var db = fixture.CreateContext())
            {
                db.Tenants.Add(tenant);
                db.PlatformConnections.Add(connection);
                db.Orders.AddRange(orders);
                await db.SaveChangesAsync();
            }
            await using var service = fixture.CreateContext();
            var sales = new MarketplaceSalesService(service, new CursorCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);
            var domestic = await sales.OrdersAsync(tenant.Id, 100, 1, null, new OrderListQuery(InvoiceRegion: "TR"), CancellationToken.None);
            var micro = await sales.OrdersAsync(tenant.Id, 100, 1, null, new OrderListQuery(InvoiceRegion: "MICRO_EXPORT"), CancellationToken.None);
            var microCard = await sales.OrdersAsync(tenant.Id, 100, 1, null, new OrderListQuery(MicroExport: true), CancellationToken.None);

            Assert.Equal(orders[0].Id, Assert.Single(domestic.Items).Id);
            Assert.Equal(orders[1].Id, Assert.Single(micro.Items).Id);
            Assert.Equal(micro.Items.Select(row => row.Id), microCard.Items.Select(row => row.Id));
            Assert.True(Assert.Single(micro.Items).Marks?.MicroExport);
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Orders.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }
}
