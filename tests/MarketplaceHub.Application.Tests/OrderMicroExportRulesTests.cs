using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// SIPARIS-STOK-3 (owner, 16 Nov 2026): one micro-export rule, the "Mikro İhracat" label's. OrderMicroExportRules.InDatabase
// (the filters and the card, in PostgreSQL) and OrderMicroExportRules.Matches (the label and the row flag, in memory) are the
// same expression; these tests hold the two to the same answer for every way a package can say it, and to the expected one.
public sealed class OrderMicroExportRulesTests
{
    // (customer snapshot, micro export)
    internal static readonly (string Json, bool Micro)[] Cases =
    [
        ("{}", false),
        ("""{"micro":true}""", true),
        ("""{"micro": true , "x":1}""", true),
        ("""{"micro":false}""", false),
        ("""{"micro":"true"}""", true),
        ("""{"micro":" True "}""", true),
        ("""{"micro":"1"}""", true),
        ("""{"micro":1}""", true),
        ("""{"micro":10}""", false),
        ("""{"micro":"YES"}""", true),
        ("""{"micro":"EVET"}""", true),
        ("""{"micro":"yes"}""", false),
        ("""{"micro":"no"}""", false),
        ("""{"micro":null}""", false),
        ("""{"MICRO":true}""", true),
        ("""{"microExport":true}""", true),
        // Any positive indicator, in either field order (the label read them by first match before: this pair was domestic).
        ("""{"micro":false,"3pByTrendyol":true}""", true),
        ("""{"3pByTrendyol":true,"micro":false}""", true),
        ("""{"micro":true,"3pByTrendyol":false}""", true),
        ("""{"3pByTrendyol":false,"micro":false}""", false),
        ("""{"3pByTrendyol":false}""", false),
        ("""{"lines":[{"micro":true}]}""", true),
        ("""{"microscope":true}""", false),
        ("""{"shipmentPackageType":"MICRO_EXPORT"}""", true),
        ("""{"orderType":"Mikro İhracat"}""", true),
        ("""{"orderType":"MİKRO İHRACAT"}""", true),
        ("""{"orderType":"İhracat"}""", true),
        ("""{"orderType":"ihracat"}""", false),
        ("""{"orderType":"micro"}""", true),
        ("""{"orderType":"Normal"}""", false),
        // Not a text search over the package: a company or a product with the words is no export.
        ("""{"invoiceAddress":{"company":"ABC İTHALAT İHRACAT LTD."},"lines":[{"productName":"MICRO FIBER"}]}""", false),
        ("""{"note":"micro\":true"}""", false),
        // Trendyol's former export partner as the buyer: both words, in the name fields.
        ("""{"customerFirstName":"PM3","customerLastName":"Arvato Export"}""", true),
        ("""{"customerFullName":"pm3 arvato"}""", true),
        ("""{"customerFirstName":"PM3","customerLastName":"Yılmaz"}""", false),
        ("""{"customerFirstName":"Ali","shipmentAddress":{"address1":"PM3 ARVATO depo"}}""", false),
    ];

    public static TheoryData<int> CaseIndexes()
    {
        var data = new TheoryData<int>();
        for (var index = 0; index < Cases.Length; index++) data.Add(index);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void The_rule_in_memory_gives_the_expected_answer(int index)
    {
        var (json, micro) = Cases[index];
        Assert.Equal(micro, OrderMicroExportRules.Matches(json));
        // The label (Customer) and the row flag (RowMarks) read the same rule.
        Assert.Equal(micro, OrderSnapshotReader.Customer(json, "{}", "{}").IsMicroExport);
        Assert.Equal(micro, OrderSnapshotReader.RowMarks("TRENDYOL", "RO", json, "{}")?.MicroExport == true);
    }

    [PostgreSqlTheory]
    [MemberData(nameof(CaseIndexes))]
    public async Task PostgreSQL_reads_the_patterns_as_the_rule_in_memory(int index)
    {
        var (json, micro) = Cases[index];
        // Only the server's regex engine is under test: the maintenance database answers it, so the test never waits for (or
        // races) the creation of the test database.
        var target = new Npgsql.NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")) { Database = "postgres" };
        await using var connection = new Npgsql.NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT @json ~ @flag OR @json ~ @type OR (@json ~ @pm3 AND @json ~ @arvato)", connection);
        command.Parameters.AddWithValue("json", json);
        command.Parameters.AddWithValue("flag", OrderMicroExportRules.FlagPattern);
        command.Parameters.AddWithValue("type", OrderMicroExportRules.TypePattern);
        command.Parameters.AddWithValue("pm3", OrderMicroExportRules.PartnerPm3Pattern);
        command.Parameters.AddWithValue("arvato", OrderMicroExportRules.PartnerArvatoPattern);
        Assert.Equal(micro, (bool)(await command.ExecuteScalarAsync())!);
    }

    // The order condition EF writes is the same rule: every case as an order, filtered in the database.
    [PostgreSqlFact]
    public async Task The_order_condition_in_the_database_is_the_rule_in_memory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")).Options;
        await using var db = new AppDbContext(options);
        var sql = db.Orders.Where(OrderMicroExportRules.InDatabase).ToQueryString();
        Assert.Contains(" ~ ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("~*", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKE", sql, StringComparison.Ordinal);
        var notSql = db.Orders.Where(OrderMicroExportRules.NotInDatabase).ToQueryString();
        Assert.NotEqual(sql, notSql);
        Assert.Contains("!~", notSql, StringComparison.Ordinal);
    }
}
