using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceIssueGateTests
{
    [Theory]
    [InlineData("10000000146", true)]
    [InlineData("12345678950", true)]
    [InlineData("12345678901", false)]
    [InlineData("01234567890", false)]
    [InlineData("1000000014A", false)]
    public void Validates_tckn_check_digits(string value, bool expected) => Assert.Equal(expected, TurkishTaxIdentity.IsValidTckn(value));

    [Theory]
    [InlineData("1234567890", true)]
    [InlineData("0720039666", true)]
    [InlineData("2650701090", true)]
    [InlineData("1234567891", false)]
    [InlineData("123456789", false)]
    public void Validates_vkn_check_digit(string value, bool expected) => Assert.Equal(expected, TurkishTaxIdentity.IsValidVkn(value));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1234567890", true)]
    [InlineData("10000000146", true)]
    [InlineData("123456789012", false)]
    public void Chooses_the_algorithm_by_length(string? value, bool expected) => Assert.Equal(expected, TurkishTaxIdentity.IsValid(value));

    [Fact]
    public void Refuses_export_orders_but_not_domestic_e_archive_or_e_invoice()
    {
        static MicroExportDecision Trendyol(string json) => MicroExportRule.Classify("TRENDYOL", null, json);
        Assert.Null(InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", Trendyol("{\"commercial\":false,\"micro\":false}")));
        // The e-Invoice decision belongs to the taxpayer lookup (InvoiceRecipientReviewTests).
        Assert.Null(InvoiceIssuePolicy.Refusal("TEMELFATURA", "TRY", Trendyol("{\"micro\":false}")));
        Assert.Null(InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", MicroExportRule.Classify("HEPSIBURADA", null, "{}")));
        Assert.Equal(InvoiceIssuePolicy.ExportFlowRequiredCode, InvoiceIssuePolicy.Refusal("EARSIVFATURA", "EUR", Trendyol("{\"micro\":false}"))?.Code);
        Assert.Equal(InvoiceIssuePolicy.ExportFlowRequiredCode, InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", Trendyol("{\"micro\":true}"))?.Code);
        Assert.Equal(InvoiceIssuePolicy.ExportFlowRequiredCode, InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", Trendyol("{\"3pByTrendyol\":true}"))?.Code);
        Assert.Equal(InvoiceIssuePolicy.ExportFlowRequiredCode, InvoiceIssuePolicy.Refusal("EARSIVFATURA", null, Trendyol("{\"micro\":false}"))?.Code);
    }

    // The delivery's "micro" mark is as before for a clear order (false domestic, true micro export) and takes the
    // stricter micro-export form (a GİB number required) for an unknown one.
    [Fact]
    public void The_delivery_micro_mark_is_unchanged_for_clear_orders()
    {
        Assert.False(InvoiceIssuePolicy.NeedsMicroExportInvoiceFields(MicroExportRule.Classify("TRENDYOL", "TR", "{\"micro\":false,\"3pByTrendyol\":false}")));
        Assert.False(InvoiceIssuePolicy.NeedsMicroExportInvoiceFields(MicroExportRule.Classify("HEPSIBURADA", null, "{}")));
        Assert.True(InvoiceIssuePolicy.NeedsMicroExportInvoiceFields(MicroExportRule.Classify("TRENDYOL", "SA", "{\"micro\":true,\"3pByTrendyol\":false}")));
        Assert.True(InvoiceIssuePolicy.NeedsMicroExportInvoiceFields(MicroExportRule.Classify("TRENDYOL", "TR", "{}")));
    }

    // C-11.9 (owner, 27 Nov 2026): an order the rule cannot call micro export or domestic gets no type by guess; the
    // refusal names why, and the stored code reads back as the generic text.
    [Theory]
    [InlineData("{}", "micro) yok")]
    [InlineData("{\"micro\":false,\"lines\":[{\"micro\":true}]}", "üst düzeyi dışında")]
    [InlineData("{\"micro\":true,\"3pByTrendyol\":true}", "ikisi de true")]
    [InlineData("{\"micro\":\"false\"}", "dışında bir değerle")]
    public void Stops_an_order_whose_micro_export_kind_is_unknown(string json, string reason)
    {
        var refusal = InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", MicroExportRule.Classify("TRENDYOL", null, json));
        Assert.Equal(InvoiceIssuePolicy.MicroExportUnknownCode, refusal?.Code);
        Assert.Contains(reason, refusal!.Message, StringComparison.Ordinal);
        Assert.Contains("tahmin edilmedi", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(InvoiceIssuePolicy.MicroExportUnknown.Message, InvoiceIssuePolicy.DescribeValidationCode(InvoiceIssuePolicy.MicroExportUnknownCode));
        // A foreign currency stops it as before, with the export reason.
        Assert.Equal(InvoiceIssuePolicy.ExportFlowRequiredCode, InvoiceIssuePolicy.Refusal("EARSIVFATURA", "EUR", MicroExportRule.Classify("TRENDYOL", null, json))?.Code);
    }

    [Fact]
    public void Validation_codes_have_turkish_operator_text()
    {
        Assert.Contains("kontrol hanesi", InvoiceIssuePolicy.DescribeValidationCode(InvoiceIssuePolicy.RecipientTaxIdInvalidCode), StringComparison.Ordinal);
        Assert.Contains("e-Fatura", InvoiceIssuePolicy.DescribeValidationCode(InvoiceIssuePolicy.EInvoiceRecipientCode), StringComparison.Ordinal);
        Assert.Equal("INVOICE_LINES_INVALID", InvoiceIssuePolicy.DescribeValidationCode("INVOICE_LINES_INVALID"));
    }

    [Theory]
    // Consumer e-Archive: the payload carries 11111111111, nothing to check.
    [InlineData("EARSIVFATURA", "{\"customerFirstName\":\"Ali\"}", "{\"identityNumber\":\"12345678901\"}", true)]
    // Corporate by company details, VKN with a wrong check digit.
    [InlineData("EARSIVFATURA", "{}", "{\"company\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567891\"}", false)]
    [InlineData("EARSIVFATURA", "{}", "{\"company\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\"}", true)]
    // Trendyol commercial buyer (sole proprietor) with a TCKN.
    [InlineData("EARSIVFATURA", "{\"commercial\":true}", "{\"taxNumber\":\"10000000146\"}", true)]
    [InlineData("EARSIVFATURA", "{\"commercial\":true}", "{\"taxNumber\":\"12345678901\"}", false)]
    public void Recipient_tax_identity_validation_uses_the_identifier_the_payload_sends(string invoiceType, string customer, string address, bool expected) =>
        Assert.Equal(expected, InvoicingBillingService.RecipientTaxIdentityIsValid(invoiceType, customer, address));

    [Fact]
    public void Trendyol_commercial_flag_marks_a_corporate_recipient_and_keeps_its_tckn()
    {
        Assert.True(TrendyolEFaturamCanonicalPayload.IsCorporateRecipient("{\"commercial\":true}", "{}", "10000000146"));
        Assert.False(TrendyolEFaturamCanonicalPayload.IsCorporateRecipient("{\"commercial\":false}", "{}", "10000000146"));

        const string canonical = """
        {
          "Id":"invoice-commercial",
          "InvoiceType":"EARSIVFATURA",
          "Currency":"TRY",
          "Note":"Satış faturası",
          "IssuedAt":"2026-10-05T12:00:00+03:00",
          "Order":{
            "OrderNumber":"4220000001",
            "OrderedAt":"2026-09-26T18:52:26+03:00",
            "CustomerSnapshotJson":"{\"commercial\":true,\"customerFirstName\":\"Ali\",\"customerLastName\":\"Veli\"}",
            "InvoiceAddressSnapshotJson":"{\"taxNumber\":\"10000000146\",\"fullAddress\":\"Test adres\",\"city\":\"İstanbul\",\"district\":\"Şişli\",\"company\":\"Ali Veli Ticaret\"}"
          },
          "Package":{"CargoProviderExternalId":"HepsiJet","StatusOccurredAt":"2026-09-29T10:00:00+03:00"},
          "Lines":[{"DescriptionSnapshot":"Ürün","UnitSnapshot":"ADET","Quantity":1,"UnitPrice":100,"LineTotal":120,"VatAmount":20,"VatRate":20,"DiscountAmount":0}]
        }
        """;

        using var payload = JsonDocument.Parse(TrendyolEFaturamCanonicalPayload.Create(new(1, 2, null), canonical));
        Assert.Equal("10000000146", payload.RootElement.GetProperty("recipientInfo").GetProperty("taxId").GetString());
    }

    [Fact]
    public async Task Adjustment_invoice_request_is_refused_before_any_fiscal_work()
    {
        // The refusal happens before the first query, so a metadata-only context is enough.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;
        await using var db = new AppDbContext(options);
        var dataProtection = new EphemeralDataProtectionProvider();
        var service = new InvoicingBillingService(db, new CursorCodec(dataProtection, TimeProvider.System), dataProtection, null!,
            new ConfigurationBuilder().Build(), TimeProvider.System, null!);

        var result = await service.CreateDraftAsync(Guid.NewGuid(),
            new CreateInvoiceCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), true),
            "adjustment-request", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(InvoiceIssuePolicy.AdjustmentFlowUnavailableCode, result.Error!.Code);
        Assert.Equal(422, result.Error.Status);
    }
}
