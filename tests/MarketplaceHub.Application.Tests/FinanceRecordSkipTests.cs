using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Item 4 of the cross-marketplace report: a finance record without an id, a readable main amount or a date is not
// stored with a made-up 0 or "now"; it is named (Skipped, then an issue) and the page's other records are kept.
public sealed class FinanceRecordSkipTests
{
    [Fact]
    public void A_trendyol_page_keeps_its_complete_records_and_names_the_ones_without_id_date_or_amount()
    {
        const string json = """
            {"page":0,"totalPages":1,"totalElements":5,"content":[
              {"id":"ok-1","transactionDate":1761900000000,"transactionType":"Satış","orderNumber":"111","debt":0,"credit":100.5},
              {"id":"ok-2","transactionDate":1761900000000,"transactionType":"Komisyon","debt":12.3},
              {"transactionDate":1761900000000,"transactionType":"Satış","orderNumber":"222","debt":0,"credit":5},
              {"id":"no-date","transactionType":"Satış","debt":0,"credit":5},
              {"id":"no-amount","transactionDate":1761900000000,"transactionType":"İade","orderNumber":"333","debt":null,"credit":"1234,50"}
            ]}
            """;

        var page = TrendyolFinanceMapper.Page(json, TrendyolFinanceRequestContract.Settlements, "Sale", null);

        Assert.Equal(["ok-1", "ok-2"], page.Items.Select(x => x.ExternalId));
        Assert.Equal((0m, 100.5m), (page.Items[0].Debt, page.Items[0].Credit));
        // A credit left out beside a readable debt is 0: the record says which side it is on.
        Assert.Equal((12.3m, 0m), (page.Items[1].Debt, page.Items[1].Credit));
        Assert.Equal(3, page.Skipped.Count);
        Assert.StartsWith("#", page.Skipped[0].Key);
        Assert.Contains("kimliksiz kayıt (sipariş 222, Satış): kimliği yok", page.Skipped[0].Description);
        Assert.Equal(("no-date", "kayıt no-date (Satış): tarihi yok ya da okunamadı"), (page.Skipped[1].Key, page.Skipped[1].Description));
        Assert.Equal(("no-amount", "kayıt no-amount (sipariş 333, İade): tutarı yok ya da okunamadı"), (page.Skipped[2].Key, page.Skipped[2].Description));
    }

    [Fact]
    public void A_hepsiburada_page_keeps_its_complete_records_and_names_the_ones_without_id_amount_or_day()
    {
        using var document = JsonDocument.Parse("""
            {"count":4,"items":[
              {"id":"a1","transactionType":"Payment","orderNumber":"0019","amount":{"value":1180.00,"currencyCode":"949"},"dueDate":"2026-11-10T00:00:00","isIncome":true},
              {"transactionType":"Payment","orderNumber":"0020","amount":{"value":5,"currencyCode":"949"},"dueDate":"2026-11-10T00:00:00"},
              {"id":"a3","transactionType":"Commission","orderNumber":"0021","dueDate":"2026-11-10T00:00:00"},
              {"id":"a4","transactionType":"Commission","amount":{"value":7,"currencyCode":"949"}}
            ]}
            """);

        var page = HepsiburadaFinanceMapper.Page(document.RootElement);

        Assert.Equal("a1", Assert.Single(page.Items).Id);
        Assert.Equal(4, page.Count);
        Assert.Equal(3, page.Skipped.Count);
        Assert.Contains("kimliği yok", page.Skipped[0].Description);
        Assert.Equal(("a3", "kayıt a3 (sipariş 0021, Commission): tutarı yok ya da okunamadı"), (page.Skipped[1].Key, page.Skipped[1].Description));
        Assert.Equal(("a4", "kayıt a4 (Commission): tarihi yok ya da okunamadı"), (page.Skipped[2].Key, page.Skipped[2].Description));
    }

    [Fact]
    public void A_pazarama_movement_without_code_amount_allowance_or_date_is_named_and_a_missing_commission_stays_unknown()
    {
        using var document = JsonDocument.Parse("""
            {"data":{"transactionList":[
              {"orderId":5,"trxCode":"T1","amount":100,"allowanceAmount":80,"status":"Satış","transactionDate":"2026-04-05T00:00:00"},
              {"orderId":5,"amount":100,"allowanceAmount":80,"status":"Satış","transactionDate":"2026-04-05T00:00:00"},
              {"orderId":5,"trxCode":"T3","amount":100,"status":"Satış","transactionDate":"2026-04-05T00:00:00"},
              {"orderId":5,"trxCode":"T4","amount":100,"allowanceAmount":80,"status":"İade"}
            ]},"success":true}
            """);

        var finance = PazaramaFinanceMapper.Finance(document.RootElement, 5);

        var sale = Assert.Single(finance.Transactions);
        Assert.Equal("T1", sale.TransactionCode);
        // Sub-amounts not sent were not applied (0); the commission not sent is unknown.
        Assert.Equal((0m, 0m, 0m), (sale.CargoDebt, sale.Stoppage, sale.CouponDiscount));
        Assert.Null(sale.Commission);
        var row = new MarketplaceFinanceTransaction { PlatformCode = "PAZARAMA", Source = PazaramaFinanceSyncJobHandler.Source, ExternalId = "T1", RequestedType = "x", Category = "", RawJson = "{}" };
        PazaramaFinanceSyncJobHandler.Fill(row, sale, "5", DateTimeOffset.UtcNow);
        Assert.Null(row.CommissionAmount);
        Assert.Equal(new DateTimeOffset(2026, 4, 4, 21, 0, 0, TimeSpan.Zero), row.TransactionDate);

        Assert.Equal(3, finance.Skipped.Count);
        Assert.Contains("kimliksiz kayıt (sipariş 5, Satış): kimliği yok", finance.Skipped[0].Description);
        Assert.Equal(("T3", "kayıt T3 (sipariş 5, Satış): tutarı yok ya da okunamadı"), (finance.Skipped[1].Key, finance.Skipped[1].Description));
        Assert.Equal(("T4", "kayıt T4 (sipariş 5, İade): tarihi yok ya da okunamadı"), (finance.Skipped[2].Key, finance.Skipped[2].Description));
    }

    [Fact]
    public void An_n11_line_without_id_or_settlement_amount_or_with_an_unreadable_one_is_named_and_the_days_other_lines_kept()
    {
        var detail = XElement.Parse("""
            <GetSettlementDetailResponse xmlns="http://www.n11.com/ws/schemas"><settlementDetail><settlement><status>ODENDI</status></settlement>
              <orderItemSettlementDataList><orderItemSettlements>
                <orderItemSettlement><orderItemId>1</orderItemId><orderNumber>9001</orderNumber><settlementAmount>80</settlementAmount><paymentAmount>100</paymentAmount></orderItemSettlement>
                <orderItemSettlement><orderNumber>9002</orderNumber><settlementAmount>80</settlementAmount></orderItemSettlement>
                <orderItemSettlement><orderItemId>3</orderItemId><orderNumber>9003</orderNumber><paymentAmount>100</paymentAmount></orderItemSettlement>
                <orderItemSettlement><orderItemId>4</orderItemId><orderNumber>9004</orderNumber><settlementAmount>80</settlementAmount><adFee>1234,50</adFee></orderItemSettlement>
              </orderItemSettlements><metadata><pageCount>1</pageCount></metadata></orderItemSettlementDataList></settlementDetail>
            </GetSettlementDetailResponse>
            """);

        var page = N11SettlementMapper.Items(detail, 0);

        var line = Assert.Single(page.Items);
        Assert.Equal(("1", 80m, 0m), (line.OrderItemId, line.SettlementAmount, line.Commission));
        Assert.Equal(3, page.Skipped.Count);
        Assert.Contains("kimliksiz kayıt (sipariş 9002): kimliği yok", page.Skipped[0].Description);
        Assert.Equal(("3", "kayıt 3 (sipariş 9003): tutarı yok ya da okunamadı"), (page.Skipped[1].Key, page.Skipped[1].Description));
        Assert.Equal("4", page.Skipped[2].Key);
    }
}

// The finance jobs against a real database: one bad record neither stops the job nor is stored; it opens an issue that
// the record's later, complete reading resolves.
[Collection("postgres-integration")]
public sealed class FinanceRecordSkipPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task A_hepsiburada_record_without_an_amount_is_left_out_named_and_resolved_once_complete()
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"hbskip-{Guid.NewGuid():N}"[..20], DisplayName = "hb", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = Now, UpdatedAt = Now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "hb", ExternalStoreId = $"m-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            await seed.SaveChangesAsync();
        }
        try
        {
            var port = new Port { Complete = false };
            var outcome = await RunAsync(tenant.Id, connection.Id, port);
            Assert.True(outcome.Result.Succeeded, outcome.Result.ErrorSummary);
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(["good"], await db.MarketplaceFinanceTransactions.Where(x => x.TenantId == tenant.Id).Select(x => x.ExternalId).ToListAsync());
                var issue = await db.OperationalIssues.SingleAsync(x => x.TenantId == tenant.Id && x.Code == FinanceRecordIssues.Code);
                Assert.Equal(FinanceRecordIssues.Prefix(connection.Id) + "bad", issue.DedupeKey);
                Assert.Equal(IssueStatus.Open, issue.Status);
                Assert.Contains("kayıt bad", issue.Summary);
            }

            port.Complete = true;
            outcome = await RunAsync(tenant.Id, connection.Id, port);
            Assert.True(outcome.Result.Succeeded, outcome.Result.ErrorSummary);
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(2, await db.MarketplaceFinanceTransactions.CountAsync(x => x.TenantId == tenant.Id));
                Assert.Equal(IssueStatus.Resolved, (await db.OperationalIssues.SingleAsync(x => x.TenantId == tenant.Id && x.Code == FinanceRecordIssues.Code)).Status);
            }
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.MarketplaceFinanceTransactions.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.OperationalIssues.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    private async Task<JobOutcome> RunAsync(Guid tenantId, Guid connectionId, Port port)
    {
        await using var db = fixture.CreateContext();
        var handler = new HepsiburadaFinanceSyncJobHandler(db, port, new FixedClock(Now), new ConfigurationBuilder().Build());
        return await handler.HandleAsync(new JobRequest(tenantId, connectionId, MarketplaceJobTypes.HepsiburadaFinanceSync, "{}", "test", null), CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Every window answers the same two records; "bad" has no amount until Complete.
    private sealed class Port : IHepsiburadaFinancePort
    {
        public bool Complete { get; set; }

        public Task<AdapterResult<RemoteFinanceRecordPage>> GetFinanceRecordsAsync(AdapterContext context, string status, bool byDueDate, DateTimeOffset start, DateTimeOffset end, int offset, CancellationToken cancellationToken)
        {
            var amount = Complete ? """{"value":5,"currencyCode":"949"}""" : "null";
            using var document = JsonDocument.Parse($$"""
                {"count":2,"items":[
                  {"id":"good","transactionType":"Payment","amount":{"value":10,"currencyCode":"949"},"dueDate":"2026-11-10T00:00:00","isIncome":true},
                  {"id":"bad","transactionType":"Commission","amount":{{amount}},"dueDate":"2026-11-10T00:00:00","isIncome":false}
                ]}
                """);
            return Task.FromResult(AdapterResult<RemoteFinanceRecordPage>.Success(HepsiburadaFinanceMapper.Page(document.RootElement)));
        }
    }
}
