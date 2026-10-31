using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A corporate buyer may be an e-Invoice taxpayer, so the E-Faturam taxpayer lookup decides its document type:
// registered gets an e-Invoice to its alias, not registered an e-Archive, and an unknown answer gets no invoice.
public sealed class InvoiceRecipientReviewTests
{
    private const string HepsiburadaCorporateInvoice = "{\"companyName\":\"Örnek A.Ş.\",\"taxOffice\":\"Kadıköy\",\"taxNumber\":\"1234567890\",\"city\":\"İstanbul\"}";
    private const string HepsiburadaIndividualInvoice = "{\"identityNo\":\"10987654321\",\"address\":{\"city\":\"İstanbul\",\"town\":\"Üsküdar\"}}";
    private const string HepsiburadaCustomer = "{\"name\":\"Ayşe Yılmaz\",\"marketplaceInvoiceStatus\":\"NOT_INVOICED\"}";

    private static EInvoiceTaxpayerLookup Registered(params string[] aliases) =>
        new(EInvoiceTaxpayerState.Registered, "ÖRNEK A.Ş.", aliases.Select(alias => new EInvoiceTaxpayerAlias(alias, "PK")).ToArray());

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
    [InlineData(HepsiburadaCorporateInvoice)]
    public void Corporate_buyer_is_looked_up(string invoiceAddress)
    {
        Assert.True(InvoiceAmounts.IsCorporateBuyer("{}", invoiceAddress));
        Assert.True(EInvoiceRecipientPolicy.RequiresLookup("EARSIVFATURA", corporateRecipient: true));
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
    public void Individual_buyer_keeps_the_e_archive_without_a_lookup(string customer, string invoiceAddress)
    {
        Assert.Equal("EARSIVFATURA", InvoiceAmounts.InvoiceDocumentType("HEPSIBURADA", customer, invoiceAddress));
        Assert.False(InvoiceAmounts.IsCorporateBuyer(customer, invoiceAddress));

        var decision = EInvoiceRecipientPolicy.Decide("EARSIVFATURA", corporateRecipient: false, lookup: null);

        Assert.Equal("EARSIVFATURA", decision.InvoiceType);
        Assert.Null(decision.TaxpayerState);
        Assert.Null(InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", customer, decision));
    }

    [Fact]
    public void Registered_buyer_gets_an_e_invoice_to_its_alias()
    {
        var decision = EInvoiceRecipientPolicy.Decide("EARSIVFATURA", corporateRecipient: true, Registered("urn:mail:defaultpk@ornek.com.tr"));

        Assert.Equal("TEMELFATURA", decision.InvoiceType);
        Assert.Equal("urn:mail:defaultpk@ornek.com.tr", decision.TargetAlias);
        Assert.Equal("ÖRNEK A.Ş.", decision.TaxpayerTitle);
        Assert.Null(InvoiceIssuePolicy.Refusal("TEMELFATURA", "TRY", HepsiburadaCustomer, decision));
        // An e-Archive draft saved before the lookup is not sent: its type no longer matches.
        Assert.Equal(InvoiceIssuePolicy.EInvoiceTaxpayerChangedCode, InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", HepsiburadaCustomer, decision)?.Code);
    }

    [Theory]
    [InlineData(new[] { "urn:mail:satispk@ornek.com.tr" }, "urn:mail:satispk@ornek.com.tr")]
    [InlineData(new[] { "urn:mail:satispk@ornek.com.tr", "urn:mail:defaultpk@ornek.com.tr" }, "urn:mail:defaultpk@ornek.com.tr")]
    [InlineData(new[] { "urn:mail:defaultpk@ornek.com.tr", "URN:MAIL:DEFAULTPK@ORNEK.COM.TR" }, "urn:mail:defaultpk@ornek.com.tr")]
    [InlineData(new[] { "urn:mail:satispk@ornek.com.tr", "urn:mail:muhasebepk@ornek.com.tr" }, null)]
    [InlineData(new[] { "urn:mail:defaultpk@a.com.tr", "urn:mail:defaultpk@b.com.tr" }, null)]
    public void Alias_is_the_single_one_or_the_single_default(string[] aliases, string? expected)
    {
        var decision = EInvoiceRecipientPolicy.Decide("EARSIVFATURA", corporateRecipient: true, Registered(aliases));

        Assert.Equal(expected, decision.TargetAlias);
        Assert.Equal(expected is null ? InvoiceIssuePolicy.EInvoiceTargetAliasUnresolvedCode : null, decision.Refusal?.Code);
    }

    [Fact]
    public void Not_registered_buyer_gets_an_e_archive()
    {
        var decision = EInvoiceRecipientPolicy.Decide("EARSIVFATURA", corporateRecipient: true, EInvoiceTaxpayerLookup.NotRegistered());

        Assert.Equal("EARSIVFATURA", decision.InvoiceType);
        Assert.Equal(EInvoiceTaxpayerState.NotRegistered, decision.TaxpayerState);
        Assert.Null(InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", HepsiburadaCustomer, decision));
    }

    [Fact]
    public void Marketplace_flag_and_lookup_disagreeing_stops_the_invoice()
    {
        // Pazarama copies billingAddress.isEInvoiceObliged into the customer snapshot as isEInvoice.
        const string obligedCustomer = "{\"companyName\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"invoiceType\":\"2\",\"isEInvoice\":true}";
        const string billing = "{\"nameSurname\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"invoiceType\":2}";
        var marketplaceType = InvoiceAmounts.InvoiceDocumentType("PAZARAMA", obligedCustomer, billing);

        var decision = EInvoiceRecipientPolicy.Decide(marketplaceType, corporateRecipient: true, EInvoiceTaxpayerLookup.NotRegistered());

        Assert.Equal("TEMELFATURA", marketplaceType);
        Assert.Equal(InvoiceIssuePolicy.EInvoiceTaxpayerConflictCode, InvoiceIssuePolicy.Refusal(decision.InvoiceType, "TRY", obligedCustomer, decision)?.Code);
    }

    [Theory]
    [InlineData("EARSIVFATURA")]
    [InlineData("TEMELFATURA")]
    public void Unknown_lookup_never_issues_a_guessed_type(string marketplaceType)
    {
        foreach (var lookup in new EInvoiceTaxpayerLookup?[] { null, EInvoiceTaxpayerLookup.Unknown("EFATURAM_TIMEOUT") })
        {
            var decision = EInvoiceRecipientPolicy.Decide(marketplaceType, corporateRecipient: true, lookup);

            Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientReviewCode, decision.Refusal?.Code);
            Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientReviewCode, InvoiceIssuePolicy.Refusal(marketplaceType, "TRY", "{}", decision)?.Code);
        }
    }

    [Fact]
    public void Trendyol_commercial_buyer_flagged_for_e_invoice_is_looked_up_too()
    {
        const string customer = "{\"commercial\":true}";
        const string address = "{\"company\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"eInvoiceAvailable\":true}";

        var type = InvoiceAmounts.InvoiceDocumentType("TRENDYOL", customer, address);

        Assert.Equal("TEMELFATURA", type);
        Assert.True(EInvoiceRecipientPolicy.RequiresLookup(type, corporateRecipient: false));
        Assert.Equal("TEMELFATURA", EInvoiceRecipientPolicy.Decide(type, false, Registered("urn:mail:defaultpk@ornek.com.tr")).InvoiceType);
    }

    [Fact]
    public void Export_refusal_comes_before_the_recipient_decision()
    {
        var decision = EInvoiceRecipientPolicy.Decide("EARSIVFATURA", corporateRecipient: true, EInvoiceTaxpayerLookup.Unknown("EFATURAM_TIMEOUT"));

        Assert.Equal(InvoiceIssuePolicy.ExportFlowRequiredCode, InvoiceIssuePolicy.Refusal("EARSIVFATURA", "EUR", HepsiburadaCustomer, decision)?.Code);
    }

    [Fact]
    public void Recipient_codes_have_turkish_operator_text()
    {
        foreach (var refusal in new[] { InvoiceIssuePolicy.EInvoiceRecipientReview, InvoiceIssuePolicy.EInvoiceTargetAliasUnresolved, InvoiceIssuePolicy.EInvoiceTaxpayerConflict, InvoiceIssuePolicy.EInvoiceTaxpayerChanged })
        {
            Assert.Equal(refusal.Message, InvoiceIssuePolicy.DescribeValidationCode(refusal.Code));
            Assert.Contains("kesilmedi", refusal.Message.Replace("gönderilmedi", "kesilmedi", StringComparison.Ordinal));
        }
        Assert.Contains("manuel belge", InvoiceIssuePolicy.EInvoiceRecipientReview.Message);
    }
}
