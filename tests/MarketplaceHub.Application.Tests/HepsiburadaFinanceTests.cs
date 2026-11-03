using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaFinanceTests
{
    // Shaped as the Kayıt Bazlı Muhasebe Servisi answers (hepsiburada developer docs, transactions/merchantid).
    private const string Page = """
        {"count":2,"items":[
          {"id":"a1","transactionType":"Payment","status":"WillBePaid","sku":"HBV0000058C3H","packageNumber":"5550001","orderNumber":"0019531668","invoiceNumber":null,
           "quantity":1,"amount":{"value":1180.00,"currencyCode":"949"},"taxAmount":{"value":180.00,"currencyCode":"949"},"netAmount":{"value":1000.00,"currencyCode":"949"},
           "orderDate":"2026-10-28T14:05:00","dueDate":"2026-11-10T00:00:00","paymentDate":null,"isInvoice":false,"isIncome":true,"transactionTypeCategory":"OrderBasedTransactions"},
          {"id":"a2","transactionType":"Commission","status":"Paid","orderNumber":"0019531668","invoiceNumber":"HD12018000104453",
           "amount":{"value":177.00,"currencyCode":"949"},"dueDate":"2026-10-30T00:00:00","paymentDate":"2026-11-02T00:00:00","invoiceDate":"2026-10-29T10:00:00","isInvoice":true,"isIncome":false}
        ]}
        """;

    [Fact]
    public void A_page_keeps_each_records_money_status_and_istanbul_days()
    {
        using var document = JsonDocument.Parse(Page);
        var page = HepsiburadaFinanceMapper.Page(document.RootElement);
        Assert.Equal(2, page.Count);
        var sale = page.Items[0];
        Assert.Equal("a1", sale.Id);
        Assert.Equal("WillBePaid", sale.Status);
        Assert.Equal(1180m, sale.Amount);
        Assert.Equal("TRY", sale.Currency);
        Assert.True(sale.IsIncome);
        // Istanbul midnight of 10 Nov is 21:00 UTC the day before.
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero), sale.DueDate);
        Assert.Null(sale.PaymentDate);
        Assert.Throws<JsonException>(() => HepsiburadaFinanceMapper.Page(JsonDocument.Parse("""{"items":[{"transactionType":"Payment"}]}""").RootElement));
    }

    [Fact]
    public void Income_is_a_credit_an_expense_a_debt_and_hepsiburadas_status_decides_paid()
    {
        using var document = JsonDocument.Parse(Page);
        var page = HepsiburadaFinanceMapper.Page(document.RootElement);
        var today = new DateOnly(2026, 11, 4);
        var rows = page.Items.Select(record =>
        {
            var row = new MarketplaceFinanceTransaction
            {
                PlatformCode = "HEPSIBURADA",
                Source = HepsiburadaFinanceSyncJobHandler.Source,
                ExternalId = record.Id,
                RequestedType = "x",
                Category = MarketplaceFinanceCategories.Other,
                RawJson = record.RawJson
            };
            HepsiburadaFinanceSyncJobHandler.Apply(row, record, DateTimeOffset.UtcNow);
            return row;
        }).ToList();
        Assert.Equal((1180m, 0m, MarketplaceFinanceCategories.Sale), (rows[0].Credit, rows[0].Debt, rows[0].Category));
        Assert.Equal((0m, 177m, MarketplaceFinanceCategories.Commission), (rows[1].Credit, rows[1].Debt, rows[1].Category));
        Assert.Equal("HD12018000104453", rows[1].InvoiceNumber);

        PayoutRow View(MarketplaceFinanceTransaction row) => new()
        {
            PlatformCode = row.PlatformCode,
            Source = row.Source,
            ExternalId = row.ExternalId,
            RequestedType = row.RequestedType,
            TransactionSubType = row.TransactionSubType,
            Category = row.Category,
            Credit = row.Credit,
            Debt = row.Debt,
            PaymentDate = row.PaymentDate
        };
        Assert.Equal(PayoutBuilder.Expected, PayoutBuilder.Status(View(rows[0]), today));
        Assert.Equal(1180m, PayoutBuilder.Net(View(rows[0])));
        Assert.Equal(PayoutBuilder.Paid, PayoutBuilder.Status(View(rows[1]), today));
        Assert.Equal(-177m, PayoutBuilder.Net(View(rows[1])));
        Assert.Contains("Offset=200&Limit=100&Status=WillBePaid&DueDateStart=2026-11-01T00%3A00%3A00",
            HepsiburadaFinanceRequestContract.Path("m-1", "WillBePaid", true, new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 30, 21, 0, 0, TimeSpan.Zero), 200));
    }
}
