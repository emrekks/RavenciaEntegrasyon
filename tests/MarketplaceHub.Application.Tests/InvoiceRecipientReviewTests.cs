using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Only Trendyol states whether a corporate buyer is an e-Invoice taxpayer. A corporate buyer of another
// marketplace must not get an automatic e-Archive document that may have to be cancelled and issued again.
public sealed class InvoiceRecipientReviewTests
{
    private const string HepsiburadaCorporateInvoice = "{\"companyName\":\"Örnek A.Ş.\",\"taxOffice\":\"Kadıköy\",\"taxNumber\":\"1234567890\",\"city\":\"İstanbul\"}";
    private const string HepsiburadaIndividualInvoice = "{\"identityNo\":\"10987654321\",\"address\":{\"city\":\"İstanbul\",\"town\":\"Üsküdar\"}}";
    private const string HepsiburadaCustomer = "{\"name\":\"Ayşe Yılmaz\",\"marketplaceInvoiceStatus\":\"NOT_INVOICED\"}";

    [Fact]
    public void Hepsiburada_corporate_buyer_is_stopped_for_review_instead_of_getting_an_e_archive()
    {
        Assert.Equal("EARSIVFATURA", InvoiceAmounts.InvoiceDocumentType("HEPSIBURADA", HepsiburadaCustomer, HepsiburadaCorporateInvoice));
        Assert.True(InvoiceAmounts.RequiresEInvoiceRecipientReview("HEPSIBURADA", HepsiburadaCustomer, HepsiburadaCorporateInvoice));

        var refusal = InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", HepsiburadaCustomer, "HEPSIBURADA", HepsiburadaCorporateInvoice);

        Assert.NotNull(refusal);
        Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientReviewCode, refusal.Code);
        Assert.Equal("EINVOICE_RECIPIENT_REVIEW_REQUIRED", refusal.Code);
        Assert.Contains("e-Fatura mükellefi olabilir", refusal.Message);
    }

    [Theory]
    // A valid VKN alone identifies a company.
    [InlineData("{\"taxNumber\":\"0720039666\"}")]
    // A company name next to a personal identity number: a sole proprietor that may still be an e-Invoice taxpayer.
    [InlineData("{\"companyName\":\"Ali Veli Ticaret\",\"taxNumber\":\"10000000146\"}")]
    [InlineData("{\"companyName\":\"Ali Veli Ticaret\",\"identityNo\":\"10000000146\"}")]
    // The invoice envelope may still wrap the corporate fields.
    [InlineData("{\"invoice\":{\"companyName\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\"}}")]
    // n11 and Pazarama billing addresses mark a corporate buyer with invoiceType 2.
    [InlineData("{\"fullName\":\"Örnek A.Ş.\",\"invoiceType\":2}")]
    [InlineData("{\"fullName\":\"Örnek A.Ş.\",\"invoiceType\":\"2\"}")]
    public void Corporate_buyer_of_another_marketplace_needs_review(string invoiceAddress)
    {
        foreach (var platform in new[] { "HEPSIBURADA", "N11", "PAZARAMA", "SHOPIFY" })
        {
            Assert.Equal("EARSIVFATURA", InvoiceAmounts.InvoiceDocumentType(platform, "{}", invoiceAddress));
            Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientReviewCode, InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", "{}", platform, invoiceAddress)?.Code);
        }
    }

    [Theory]
    [InlineData(HepsiburadaCustomer, HepsiburadaIndividualInvoice)]
    // n11 individual billing address from the order feed.
    [InlineData("{}", "{\"address\":\"Reşitpaşa Mah Arı 3 D:8-9\",\"city\":\"İstanbul\",\"fullName\":\"N11 müşteri\",\"invoiceType\":1}")]
    // A free-text company field without any tax number (Shopify checkout) is not a corporate buyer.
    [InlineData("{}", "{\"company\":\"Ayşe'nin Dükkanı\",\"city\":\"İstanbul\"}")]
    // A ten-digit number that fails the VKN check digit alone does not make a company; the tax id gate handles it.
    [InlineData("{}", "{\"taxNumber\":\"1234567891\"}")]
    // The consumer placeholder identity is not a tax number.
    [InlineData("{}", "{\"companyName\":\"Ayşe Yılmaz\",\"identityNumber\":\"11111111111\"}")]
    [InlineData("{}", "{}")]
    [InlineData("not json", "not json")]
    public void Individual_buyer_of_another_marketplace_keeps_the_e_archive(string customer, string invoiceAddress)
    {
        Assert.Equal("EARSIVFATURA", InvoiceAmounts.InvoiceDocumentType("HEPSIBURADA", customer, invoiceAddress));
        Assert.False(InvoiceAmounts.RequiresEInvoiceRecipientReview("HEPSIBURADA", customer, invoiceAddress));
        Assert.Null(InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", customer, "HEPSIBURADA", invoiceAddress));
    }

    [Fact]
    public void An_explicit_e_invoice_flag_of_another_marketplace_decides_the_document()
    {
        // Pazarama copies billingAddress.isEInvoiceObliged into the customer snapshot as isEInvoice.
        const string obligedCustomer = "{\"companyName\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"invoiceType\":\"2\",\"isEInvoice\":true}";
        const string notObligedCustomer = "{\"companyName\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"invoiceType\":\"2\",\"isEInvoice\":false}";
        const string billing = "{\"nameSurname\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"invoiceType\":2}";

        Assert.Equal("TEMELFATURA", InvoiceAmounts.InvoiceDocumentType("PAZARAMA", obligedCustomer, billing));
        Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientCode, InvoiceIssuePolicy.Refusal("TEMELFATURA", "TRY", obligedCustomer, "PAZARAMA", billing)?.Code);
        Assert.Equal("TEMELFATURA", InvoiceAmounts.InvoiceDocumentType("PAZARAMA", "{}", "{\"isEInvoiceObliged\":true}"));
        // A draft saved as e-Archive before the flag was read is not sent either.
        Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientReviewCode, InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", obligedCustomer, "PAZARAMA", billing)?.Code);

        Assert.Equal("EARSIVFATURA", InvoiceAmounts.InvoiceDocumentType("PAZARAMA", notObligedCustomer, billing));
        Assert.False(InvoiceAmounts.RequiresEInvoiceRecipientReview("PAZARAMA", notObligedCustomer, billing));
        Assert.Null(InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", notObligedCustomer, "PAZARAMA", billing));
        // A true flag on either snapshot wins over a false one.
        Assert.Equal("TEMELFATURA", InvoiceAmounts.InvoiceDocumentType("PAZARAMA", notObligedCustomer, "{\"isEInvoiceObliged\":true}"));
    }

    [Theory]
    [InlineData("{\"commercial\":true}", "{\"company\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"eInvoiceAvailable\":true}")]
    [InlineData("{\"commercial\":true}", "{\"invoiceAddress\":{\"eInvoiceAvailable\":true}}")]
    [InlineData("{\"commercial\":true}", "{\"company\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"eInvoiceAvailable\":false}")]
    [InlineData("{\"commercial\":true}", "{\"company\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\"}")]
    [InlineData("{\"commercial\":false}", "{\"eInvoiceAvailable\":true}")]
    [InlineData("[]", "[]")]
    public void Trendyol_keeps_its_own_decision_and_is_never_held_for_review(string customer, string invoiceAddress)
    {
        var trendyol = InvoiceAmounts.TrendyolInvoiceType(customer, invoiceAddress);

        Assert.Equal(trendyol, InvoiceAmounts.InvoiceDocumentType("TRENDYOL", customer, invoiceAddress));
        Assert.Equal(trendyol, InvoiceAmounts.InvoiceDocumentType("trendyol", customer, invoiceAddress));
        Assert.False(InvoiceAmounts.RequiresEInvoiceRecipientReview("TRENDYOL", customer, invoiceAddress));
        Assert.Equal(InvoiceIssuePolicy.Refusal(trendyol, "TRY", customer)?.Code,
            InvoiceIssuePolicy.Refusal(trendyol, "TRY", customer, "TRENDYOL", invoiceAddress)?.Code);
    }

    [Fact]
    public void Trendyol_commercial_buyer_with_e_invoice_is_still_refused_as_an_e_invoice_recipient()
    {
        const string customer = "{\"commercial\":true}";
        const string address = "{\"company\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"eInvoiceAvailable\":true}";

        var type = InvoiceAmounts.InvoiceDocumentType("TRENDYOL", customer, address);

        Assert.Equal("TEMELFATURA", type);
        Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientCode, InvoiceIssuePolicy.Refusal(type, "TRY", customer, "TRENDYOL", address)?.Code);
    }

    [Fact]
    public void Export_refusal_comes_before_the_recipient_review()
    {
        Assert.Equal(InvoiceIssuePolicy.ExportFlowRequiredCode,
            InvoiceIssuePolicy.Refusal("EARSIVFATURA", "EUR", HepsiburadaCustomer, "HEPSIBURADA", HepsiburadaCorporateInvoice)?.Code);
    }

    [Fact]
    public void Review_code_has_turkish_operator_text()
    {
        Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientReview.Message,
            InvoiceIssuePolicy.DescribeValidationCode(InvoiceIssuePolicy.EInvoiceRecipientReviewCode));
        Assert.Contains("manuel belge", InvoiceIssuePolicy.EInvoiceRecipientReview.Message);
    }
}
