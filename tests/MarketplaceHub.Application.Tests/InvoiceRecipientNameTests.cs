using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceRecipientNameTests
{
    [Fact]
    public void Hepsiburada_invoice_is_issued_to_the_invoice_address_name_not_the_account_holder()
    {
        // The shape of a live Hepsiburada order: the account holder ordered for someone else, whose name is on the
        // invoice and shipment addresses.
        const string customer = """{"customerId":"c1","name":"sezen gündoğdu","marketplaceInvoiceStatus":"NOT_INVOICED"}""";
        const string invoiceAddress = """{"addressId":"a1","address":"Örnek Mah. No:1 Van","name":"MUSTAFA TOKAT","city":"Van","turkishIdentityNumber":"11111111111","taxNumber":"","taxOffice":null}""";

        Assert.Equal("MUSTAFA TOKAT", TrendyolEFaturamCanonicalPayload.ResolveRecipientName(customer, invoiceAddress, "EARSIVFATURA"));
    }

    [Fact]
    public void Structured_first_and_last_name_on_the_invoice_address_win()
    {
        const string invoiceAddress = """{"firstName":"Aylin","lastName":"Örnek","fullName":"Aylin Örnek"}""";

        Assert.Equal("Aylin Örnek", TrendyolEFaturamCanonicalPayload.ResolveRecipientName("""{"name":"Hesap Sahibi"}""", invoiceAddress, "EARSIVFATURA"));
    }

    [Fact]
    public void The_account_holder_is_used_only_when_the_invoice_address_has_no_name()
    {
        Assert.Equal("Aylin Örnek", TrendyolEFaturamCanonicalPayload.ResolveRecipientName("""{"name":"Aylin Örnek"}""", "{}", "EARSIVFATURA"));
    }

    [Fact]
    public void No_name_anywhere_is_unknown()
    {
        Assert.Null(TrendyolEFaturamCanonicalPayload.ResolveRecipientName("{}", "{}", "EARSIVFATURA"));
    }
}
