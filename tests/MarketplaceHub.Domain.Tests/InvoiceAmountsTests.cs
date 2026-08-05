using System.Text.Json;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Domain.Tests;

public sealed class InvoiceAmountsTests
{
    [Theory]
    [InlineData(120, 20, 100, 20)]
    [InlineData(125.50, 20, 104.58, 20.92)]
    [InlineData(100, 0, 100, 0)]
    public void Vat_included_amount_is_split_without_changing_payable(decimal payable, decimal rate, decimal exclusive, decimal vat)
    {
        var result = InvoiceAmounts.FromVatIncluded(payable, rate);
        Assert.Equal(exclusive, result.TaxExclusiveAmount);
        Assert.Equal(vat, result.VatAmount);
        Assert.Equal(payable, result.PayableAmount);
        Assert.Equal(result.PayableAmount, result.TaxExclusiveAmount + result.VatAmount);
    }

    [Theory]
    [InlineData(0, "YALNIZ: SIFIR TÜRK LİRASI")]
    [InlineData(125.50, "YALNIZ: YÜZ YİRMİ BEŞ TÜRK LİRASI ELLİ KURUŞ")]
    [InlineData(1000, "YALNIZ: BİN TÜRK LİRASI")]
    [InlineData(1001001.01, "YALNIZ: BİR MİLYON BİN BİR TÜRK LİRASI BİR KURUŞ")]
    public void Invoice_note_contains_only_the_amount_in_Turkish(decimal amount, string expected)
    {
        Assert.Equal(expected, InvoiceAmounts.TurkishInvoiceNote(amount));
    }

    [Theory]
    [InlineData(true, true, "TEMELFATURA")]
    [InlineData(true, false, "EARSIVFATURA")]
    [InlineData(false, true, "EARSIVFATURA")]
    [InlineData(false, false, "EARSIVFATURA")]
    public void Trendyol_commercial_and_einvoice_flags_determine_invoice_type(bool commercial, bool available, string expected)
    {
        var customer = JsonSerializer.Serialize(new { commercial });
        var address = JsonSerializer.Serialize(new { invoiceAddress = new { eInvoiceAvailable = available } });
        Assert.Equal(expected, InvoiceAmounts.TrendyolInvoiceType(customer, address));
    }

    [Fact]
    public void Configured_commercial_scenario_is_used_only_for_einvoice_eligible_order()
    {
        var customer = JsonSerializer.Serialize(new { commercial = true });
        var address = JsonSerializer.Serialize(new { invoiceAddress = new { eInvoiceAvailable = true } });
        Assert.Equal("TICARIFATURA", InvoiceAmounts.TrendyolInvoiceType(customer, address, "TICARIFATURA"));
    }

}
