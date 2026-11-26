using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan 25 Nov 2026 C-6: the invoice preview shows the buyer's tax number and kind the invoice itself uses. The invoice
// reads the address snapshot after NormalizeAddressSnapshot (one written as a JSON string is unwrapped); so does the preview.
public sealed class InvoicePreviewTaxIdentityTests
{
    [Fact]
    public void A_wrapped_address_snapshot_gives_the_preview_and_the_invoice_the_same_tax_number_and_kind()
    {
        // Made-up numbers: a company's tax number on the invoice address, a person's identity number on the account.
        var address = JsonSerializer.Serialize(JsonSerializer.Serialize(new { fullName = "Örnek Ltd", city = "İstanbul", taxNumber = "1234567890", taxOffice = "Kadıköy" }));
        var customer = JsonSerializer.Serialize(new { customerFirstName = "Ayşe", identityNumber = "10000000146" });

        var invoice = TrendyolEFaturamCanonicalPayload.ResolveRecipientTaxIdentity(customer, address, "EFATURA");
        var preview = InvoicingBillingService.ReadTaxIdentity(customer, address);

        Assert.Equal("1234567890", invoice.TaxId);
        Assert.Equal(invoice.TaxId, preview);
        Assert.Equal(invoice.IsCorporate, TrendyolEFaturamCanonicalPayload.IsCorporateRecipient(customer, address, preview));
    }

    // Scan 25 Nov 2026 C-11: a blank tax number field does not hide the identity number beside it.
    [Fact]
    public void A_blank_tax_number_field_does_not_hide_the_identity_number_of_the_same_address()
    {
        var address = JsonSerializer.Serialize(new { fullName = "Ayşe Örnek", city = "İstanbul", taxNumber = "", identityNumber = "10000000146" });
        var customer = JsonSerializer.Serialize(new { customerFirstName = "Ayşe" });

        Assert.Equal("10000000146", TrendyolEFaturamCanonicalPayload.RecipientTaxId(customer, address));
    }
}
