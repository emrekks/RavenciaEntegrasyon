using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// C-11.9 (owner, 27 Nov 2026): one micro-export rule (MicroExportRule) for the invoice, the order list's label, row flag,
// filters and card. Three answers; a missing field or signals that disagree are Unknown, never a guess. Every case runs
// through the rule in memory (Classify), the same rule as an expression (StateRule, compiled) and the database conditions
// the list uses (OrderMicroExportRules), which must all give the expected answer.
public sealed class MicroExportRuleTests
{
    private const MicroExportState D = MicroExportState.Domestic;
    private const MicroExportState M = MicroExportState.MicroExport;
    private const MicroExportState U = MicroExportState.Unknown;

    // (marketplace, storefront, customer snapshot, expected)
    internal static readonly (string? Platform, string? Storefront, string Json, MicroExportState Expected)[] Cases =
    [
        // Documented: micro at the package's top level (docs ~l.403, l.802, l.4100).
        ("TRENDYOL", "TR", """{"micro":false,"3pByTrendyol":false}""", D),
        ("TRENDYOL", "SA", """{"micro":true,"3pByTrendyol":false}""", M),
        ("TRENDYOL", null, """{"micro":true}""", M),
        ("TRENDYOL", null, """{"micro":false}""", D),
        ("TRENDYOL", "TR", """{"micro": false , "x":1}""", D),
        // Documented: 3pByTrendyol true, micro then false (docs ~l.3822): the export partnership, in either field order.
        ("TRENDYOL", "RO", """{"micro":false,"3pByTrendyol":true}""", M),
        ("TRENDYOL", "RO", """{"3pByTrendyol":true,"micro":false}""", M),
        ("TRENDYOL", null, """{"3pByTrendyol":true}""", M),
        // Storefront: a micro-export country or TR contradicts the opposite flag; another storefront says nothing.
        ("TRENDYOL", "TR", """{"micro":true,"3pByTrendyol":false}""", U),
        ("TRENDYOL", "SA", """{"micro":false,"3pByTrendyol":false}""", U),
        ("TRENDYOL", "CZ", """{"micro":false}""", U),
        ("TRENDYOL", "DE", """{"micro":false}""", D),
        ("TRENDYOL", "DE", """{"micro":true}""", M),
        ("TRENDYOL", "AZ", """{"micro":true,"countryCode":"TR"}""", M),
        // Nested only, or nested beside the top level: never read as the answer.
        ("TRENDYOL", null, """{"lines":[{"micro":true}]}""", U),
        ("TRENDYOL", "TR", """{"lines":[{"micro":false}]}""", U),
        ("TRENDYOL", "TR", """{"micro":false,"lines":[{"micro":true}]}""", U),
        ("TRENDYOL", "SA", """{"micro":true,"invoiceAddress":{"micro":false}}""", U),
        ("TRENDYOL", "TR", """{"micro":false,"packageHistories":[{"3pByTrendyol":true}]}""", U),
        // Contradictory flags, or an undocumented indicator against micro = false.
        ("TRENDYOL", "RO", """{"micro":true,"3pByTrendyol":true}""", U),
        ("TRENDYOL", "TR", """{"micro":false,"shipmentPackageType":"MICRO_EXPORT"}""", U),
        ("TRENDYOL", "TR", """{"micro":false,"orderType":"Mikro İhracat"}""", U),
        ("TRENDYOL", "TR", """{"micro":false,"customerFirstName":"PM3","customerLastName":"Arvato Export"}""", U),
        ("TRENDYOL", "SA", """{"micro":true,"orderType":"MICRO"}""", M),
        ("TRENDYOL", "TR", """{"micro":false,"microExport":true}""", U),
        ("TRENDYOL", "TR", """{"micro":false,"MICRO":true}""", U),
        ("TRENDYOL", "TR", """{"micro":false,"micro":true}""", U),
        // Missing or unreadable: unknown stays unknown.
        ("TRENDYOL", "TR", "{}", U),
        ("TRENDYOL", null, "", U),
        ("TRENDYOL", null, "{x", U),
        ("TRENDYOL", null, "[1]", U),
        ("TRENDYOL", "TR", """{"3pByTrendyol":false}""", U),
        ("TRENDYOL", null, """{"claimId":"c1","_ravenciaReadModelSource":"RETURN_CLAIM"}""", U),
        ("TRENDYOL", "TR", """{"customerFirstName":"PM3","customerLastName":"ARVATO EXPORT"}""", U),
        // A value other than true / false.
        ("TRENDYOL", "TR", """{"micro":"true"}""", U),
        ("TRENDYOL", "TR", """{"micro":"false"}""", U),
        ("TRENDYOL", "TR", """{"micro":1}""", U),
        ("TRENDYOL", "TR", """{"micro":null}""", U),
        ("TRENDYOL", "TR", """{"micro":false,"3pByTrendyol":"true"}""", U),
        // Not a text search over the package: a company or a product with the words, or the words inside a text value.
        ("TRENDYOL", "TR", """{"micro":false,"invoiceAddress":{"company":"ABC İTHALAT İHRACAT LTD."},"lines":[{"productName":"MICRO FIBER"}]}""", D),
        ("TRENDYOL", "TR", """{"micro":false,"note":"\"micro\":true"}""", D),
        ("TRENDYOL", "TR", """{"micro":false,"microscope":true}""", D),
        // Every other marketplace has no micro-export concept: Domestic, whatever its package holds.
        ("HEPSIBURADA", null, "{}", D),
        ("HEPSIBURADA", null, """{"micro":true}""", D),
        ("N11", null, "{}", D),
        ("PAZARAMA", null, "{}", D),
        ("PTTAVM", null, "{}", D),
        ("SHOPIFY", null, """{"orderType":"MICRO"}""", D),
        // No marketplace known (the order's connection is gone): unknown (in memory only, no order can lack its connection).
        (null, "TR", """{"micro":false}""", U),
    ];

    public static TheoryData<int> CaseIndexes()
    {
        var data = new TheoryData<int>();
        for (var index = 0; index < Cases.Length; index++) data.Add(index);
        return data;
    }

    private static readonly Func<string?, string?, string?, MicroExportState> CompiledRule = MicroExportRule.StateRule.Compile();

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void The_rule_gives_the_expected_answer_in_memory(int index)
    {
        var (platform, storefront, json, expected) = Cases[index];
        var decision = MicroExportRule.Classify(platform, storefront, json);
        Assert.Equal(expected, decision.State);
        Assert.Equal(expected == U, decision.Reason is not null);
        // The expression the database runs, compiled, is the same rule.
        Assert.Equal(expected, CompiledRule(platform, storefront, json));
        // The label and the row flag read it: only a micro export is labelled.
        Assert.Equal(expected == M, OrderSnapshotReader.IsMicroExport(platform, storefront, json));
        Assert.Equal(expected == M, OrderSnapshotReader.RowMarks(platform, storefront, json, "{}")?.MicroExport == true);
    }

    [Fact]
    public void An_unknown_order_names_its_reason()
    {
        Assert.Contains("micro) yok", MicroExportRule.Classify("TRENDYOL", "TR", "{}").Reason, StringComparison.Ordinal);
        Assert.Contains("okunamadı", MicroExportRule.Classify("TRENDYOL", null, "{x").Reason, StringComparison.Ordinal);
        Assert.Contains("üst düzeyi dışında", MicroExportRule.Classify("TRENDYOL", null, """{"micro":false,"lines":[{"micro":true}]}""").Reason, StringComparison.Ordinal);
        Assert.Contains("(SA)", MicroExportRule.Classify("TRENDYOL", "SA", """{"micro":false}""").Reason, StringComparison.Ordinal);
        Assert.Contains("(TR)", MicroExportRule.Classify("TRENDYOL", "TR", """{"micro":true}""").Reason, StringComparison.Ordinal);
        Assert.Contains("ikisi de true", MicroExportRule.Classify("TRENDYOL", null, """{"micro":true,"3pByTrendyol":true}""").Reason, StringComparison.Ordinal);
        Assert.Contains("başka bir alan", MicroExportRule.Classify("TRENDYOL", "TR", """{"micro":false,"orderType":"MICRO"}""").Reason, StringComparison.Ordinal);
        Assert.Contains("dışında bir değerle", MicroExportRule.Classify("TRENDYOL", "TR", """{"micro":"true"}""").Reason, StringComparison.Ordinal);
        Assert.Contains("pazaryeri bilinmiyor", MicroExportRule.Classify(null, null, "{}").Reason, StringComparison.Ordinal);
    }
}

// The list's database conditions run the same rule: every case as an order of its marketplace, filtered in PostgreSQL.
[Collection("postgres-integration")]
public sealed class MicroExportRulePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task The_database_conditions_give_the_rule_in_memory_for_every_case()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"mx-{Guid.NewGuid():N}"[..16], DisplayName = "mx", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var cases = MicroExportRuleTests.Cases.Where(item => item.Platform is not null).ToArray();
        var connections = cases.Select(item => item.Platform!).Distinct()
            .ToDictionary(code => code, code => new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = code, Environment = "STAGE", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" });
        var orders = cases.Select((item, index) => new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connections[item.Platform!].Id,
            ExternalOrderId = $"mx-{index}",
            OrderNumber = $"MX{index}",
            Currency = "TRY",
            OrderedAt = now.AddMinutes(-index),
            LastRemoteModifiedAt = now,
            StoreFrontCode = item.Storefront,
            CustomerSnapshotJson = item.Json,
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
                db.PlatformConnections.AddRange(connections.Values);
                db.Orders.AddRange(orders);
                await db.SaveChangesAsync();
            }
            await using var read = fixture.CreateContext();
            var tenantOrders = read.Orders.AsNoTracking().Where(order => order.TenantId == tenant.Id);
            var micro = (await tenantOrders.Where(OrderMicroExportRules.InDatabase(read)).Select(order => order.Id).ToListAsync()).ToHashSet();
            var domestic = (await tenantOrders.Where(OrderMicroExportRules.DomesticInDatabase(read)).Select(order => order.Id).ToListAsync()).ToHashSet();
            Assert.Equal(orders.Count, await tenantOrders.CountAsync());
            Assert.Empty(micro.Intersect(domestic));
            Assert.All(orders.Select((order, index) => (order, index)), pair =>
            {
                var expected = cases[pair.index].Expected;
                var inDatabase = micro.Contains(pair.order.Id) ? MicroExportState.MicroExport : domestic.Contains(pair.order.Id) ? MicroExportState.Domestic : MicroExportState.Unknown;
                Assert.True(expected == inDatabase, $"case {pair.index} ({cases[pair.index].Platform}, {cases[pair.index].Storefront}, {cases[pair.index].Json}): expected {expected}, the database said {inDatabase}");
            });
            // The condition is the jsonb top-level read and the key count, never a text search alone.
            var sql = read.Orders.Where(OrderMicroExportRules.InDatabase(read)).ToQueryString();
            Assert.Contains("pg_input_is_valid", sql, StringComparison.Ordinal);
            Assert.Contains("jsonb_extract_path", sql, StringComparison.Ordinal);
            Assert.Contains("regexp_count", sql, StringComparison.Ordinal);
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
