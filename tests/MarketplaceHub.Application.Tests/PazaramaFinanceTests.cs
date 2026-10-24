using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PazaramaFinanceTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Answer = """
        {"data":{"transactionList":[
          {"orderId":541235638,"trxCode":"26095TyzB07114893","trxId":"8276c824","amount":1450,"installmentNumber":0,"commissionAmount":-174,"couponDiscount":0,
           "allowanceAmount":1137.1,"status":"Satış","transactionDate":"2026-04-05T00:00:00","transferredDate":"2026-05-21T00:00:00","integrationAmount":-8.28,"merchantCargoDebt":-117.44,"stoppageAmount":-13.18},
          {"orderId":999,"trxCode":"OTHER","amount":5,"status":"Satış"},
          {"orderId":541235638,"trxCode":"REFUND1","amount":-1450,"commissionAmount":174,"allowanceAmount":-1137.1,"status":"İade","transactionDate":"2026-04-20T00:00:00","transferredDate":null}],
         "commissionInvoiceDetails":[
          {"orderNumber":541235638,"totalAmountKDV":-180.9,"commissionInvoiceNumber":"E052026000041171","commissionInvoiceDate":"2026-04-30T00:00:00","operationStatus":"Satış"},
          {"orderNumber":1,"commissionInvoiceNumber":"X"}],
         "totalAmount":0,"totalCommission":0,"totalAllowance":0},"success":true}
        """;

    [Fact]
    public void Only_the_transactions_of_the_asked_order_are_kept_in_date_order()
    {
        var finance = PazaramaFinanceMapper.Finance(Parse(Answer), 541235638);

        Assert.Equal(["26095TyzB07114893", "REFUND1"], finance.Transactions.Select(x => x.TransactionCode));
    }

    // The finance job stores a sale and its refund; their commission nets out, so a returned order costs none.
    [Fact]
    public void The_finance_job_stores_the_commission_as_a_cost_that_a_refund_gives_back()
    {
        var finance = PazaramaFinanceMapper.Finance(Parse(Answer), 541235638);
        var now = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
        var rows = finance.Transactions.Select(movement =>
        {
            var row = new MarketplaceFinanceTransaction
            {
                PlatformCode = "PAZARAMA",
                Source = PazaramaFinanceSyncJobHandler.Source,
                ExternalId = movement.TransactionCode,
                RequestedType = PazaramaFinanceSyncJobHandler.RequestedType,
                Category = "",
                RawJson = "{}"
            };
            PazaramaFinanceSyncJobHandler.Fill(row, movement, "541235638", now);
            return row;
        }).ToList();

        Assert.Equal((MarketplaceFinanceCategories.Sale, 1450m, 0m, 174m), (rows[0].Category, rows[0].Credit, rows[0].Debt, rows[0].CommissionAmount));
        Assert.Equal((MarketplaceFinanceCategories.Return, 0m, 1450m, -174m), (rows[1].Category, rows[1].Credit, rows[1].Debt, rows[1].CommissionAmount));
        Assert.Equal(0m, rows.Sum(row => row.CommissionAmount));
        Assert.All(rows, row => Assert.Equal("541235638", row.OrderNumber));
    }

    [Fact]
    public void The_finance_job_runs_daily_beside_trendyols()
    {
        Assert.Contains(MarketplaceJobTypes.PazaramaFinanceSync, MarketplaceJobTypes.ProcessorJobTypes);
        Assert.Equal("finance", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.PazaramaFinanceSync));
        Assert.Equal(JobPriorityPolicy.Reconciliation, JobPriorityPolicy.For(MarketplaceJobTypes.PazaramaFinanceSync, userInitiated: false));
        Assert.True(ScheduledJobProducer.SupportsPazaramaScheduledPolicy("FINANCE"));
    }

    [Fact]
    public void A_sale_carries_every_money_field()
    {
        var sale = PazaramaFinanceMapper.Finance(Parse(Answer), 541235638).Transactions[0];

        Assert.Equal("Satış", sale.Status);
        Assert.Equal(1450m, sale.Amount);
        Assert.Equal(-174m, sale.Commission);
        Assert.Equal(1137.1m, sale.Allowance);
        Assert.Equal(-8.28m, sale.IntegrationAmount);
        Assert.Equal(-117.44m, sale.CargoDebt);
        Assert.Equal(-13.18m, sale.Stoppage);
        Assert.Equal(0, sale.Installments);
    }

    [Fact]
    public void Dates_are_turkey_time_stored_as_utc_and_an_untransferred_refund_has_no_transfer_date()
    {
        var finance = PazaramaFinanceMapper.Finance(Parse(Answer), 541235638);

        Assert.Equal(new DateTimeOffset(2026, 4, 4, 21, 0, 0, TimeSpan.Zero), finance.Transactions[0].TransactionDate);
        Assert.Equal(TimeSpan.Zero, finance.Transactions[0].TransferredDate!.Value.Offset);
        Assert.Null(finance.Transactions[1].TransferredDate);
    }

    [Fact]
    public void Commission_invoices_are_filtered_by_order_number()
    {
        var invoice = Assert.Single(PazaramaFinanceMapper.Finance(Parse(Answer), 541235638).CommissionInvoices);

        Assert.Equal("E052026000041171", invoice.InvoiceNumber);
        Assert.Equal(-180.9m, invoice.VatAmount);
        Assert.Equal("Satış", invoice.OperationStatus);
    }

    [Theory]
    [InlineData("""{"data":null,"success":true}""")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"transactionList":[],"commissionInvoiceDetails":[]}}""")]
    public void An_empty_answer_has_no_movements(string json)
    {
        var finance = PazaramaFinanceMapper.Finance(Parse(json), 1);

        Assert.Empty(finance.Transactions);
        Assert.Empty(finance.CommissionInvoices);
    }

    [Theory]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":{"transactionList":{}}}""")]
    public void A_different_shape_is_a_contract_error(string json) => Assert.Throws<JsonException>(() => PazaramaFinanceMapper.Finance(Parse(json), 1));

    [Fact]
    public void The_request_names_the_order_and_a_turkey_time_range()
    {
        var body = JsonSerializer.Serialize(PazaramaFinanceMapper.Body(541235638, new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 4, 21, 20, 59, 59, 999, TimeSpan.Zero)));

        Assert.Contains("\"startDate\":\"2026-04-01T03:00:00.000\"", body);
        Assert.Contains("\"endDate\":\"2026-04-21T23:59:59.999\"", body);
        Assert.Contains("\"orderId\":541235638", body);
        Assert.Contains("\"allowanceStartDate\":null", body);
        Assert.Equal("order/paymentAgreement", PazaramaHttpClient.PaymentAgreement);
    }

    // ---- error advice ----

    [Theory]
    [InlineData("İndirimli fiyat, liste fiyatı ve stok sayısı önceki değerleriyle aynı olamaz", "değişen")]
    [InlineData("895102324UE467 İndirimli satış fiyatı  boş olmamalıdır!", "satış fiyatını girin")]
    [InlineData("Product'a ait attribute bulunamadı!", "Eşleştirmeler")]
    [InlineData("Ürün bulunamadı", "barkodu")]
    public void Known_pazarama_sentences_get_a_hint_after_them(string message, string hint)
    {
        var advised = PazaramaErrorAdvice.WithAdvice(message);

        Assert.StartsWith(message, advised);
        Assert.Contains(hint, advised);
    }

    [Theory]
    [InlineData("Bilinmeyen bir hata")]
    [InlineData("")]
    public void Unknown_sentences_are_left_alone(string message) => Assert.Equal(message, PazaramaErrorAdvice.WithAdvice(message));

    [Fact]
    public void An_envelope_refusal_carries_the_hint()
    {
        var error = PazaramaHttpClient.EnvelopeError(Parse("""{"success":false,"userMessage":"İndirimli fiyat, liste fiyatı ve stok sayısı önceki değerleriyle aynı olamaz","messageCode":"X"}"""));

        Assert.NotNull(error);
        Assert.Contains("önceki değerleriyle aynı olamaz", error.SafeMessage);
        Assert.Contains("yalnız değişen", error.SafeMessage);
    }
}
