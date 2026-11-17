using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// 16 Nov 2026: Hepsiburada order 4476971250 belonged to a company (the seller panel showed Kurumsal Müşteri, a company
// name, a VKN and a tax office) and was issued an e-Archive to 11111111111, because the placeholder identity was read as
// "the marketplace says individual". Hepsiburada names no buyer kind; beside its identity field the tax number and tax
// office decide, and anything unclear waits for the owner. Every value here is synthetic.
public sealed class HepsiburadaCorporateBuyerTests
{
    // A made-up VKN with valid check digits (never a real one).
    internal const string Vkn = "9876543217";
    private const string Placeholder = "11111111111";
    private const string Individual = OrderBuyerTypeView.Individual;
    private const string Corporate = OrderBuyerTypeView.Corporate;
    private const string Waits = OrderBuyerTypeView.CorporateTaxIdMissing;

    // The order detail's shape, exactly the keys order 4476971250 holds on live (16 Nov 2026), with synthetic values.
    internal const string CorporateCustomer = """{"customerId":"c-synthetic","marketplaceInvoiceStatus":"NOT_INVOICED","name":"Hesap Sahibi Örnek"}""";
    internal const string CorporateInvoiceAddress = $$"""
        {"address":"Örnek Mah. Deneme Sk. No:1","addressId":"a-synthetic","alternatePhoneNumber":"","city":"İstanbul","countryCode":"TR",
         "directions":null,"district":"Örnek","email":"fatura@example.test","name":"ÖRNEK TİCARET LİMİTED ŞİRKETİ","phoneNumber":"",
         "postalCode":"34000","taxNumber":"{{Vkn}}","taxOffice":"ÖRNEKVD","town":"Kadıköy","turkishIdentityNumber":"{{Placeholder}}"}
        """;

    [Fact]
    public void The_synthetic_tax_number_is_a_valid_vkn() => Assert.True(TurkishTaxIdentity.IsValidVkn(Vkn));

    // Every shape the 37 live Hepsiburada orders had (16 Nov 2026), and the shapes the rule must still decide.
    [Theory]
    // Live, 34 orders: the placeholder, an empty tax number, no tax office.
    [InlineData($$"""{"name":"Ayşe Yılmaz","turkishIdentityNumber":"{{Placeholder}}","taxNumber":"","taxOffice":null}""", Individual, false, Placeholder)]
    // Live, 1 order: the placeholder alone.
    [InlineData($$"""{"name":"Ayşe Yılmaz","turkishIdentityNumber":"{{Placeholder}}"}""", Individual, false, Placeholder)]
    // The package list's documented individual sample: a "company name" that is the person, the placeholder, no tax number.
    [InlineData($$"""{"companyName":"Test User","identityNo":"{{Placeholder}}","taxNumber":"","taxOffice":null}""", Individual, false, Placeholder)]
    // Live, 2 orders: the placeholder next to a valid VKN and a tax office: a company.
    [InlineData($$"""{"name":"Örnek Ltd","turkishIdentityNumber":"{{Placeholder}}","taxNumber":"{{Vkn}}","taxOffice":"Kadıköy"}""", Corporate, false, Vkn)]
    [InlineData($$"""{"companyName":"Örnek Ltd","identityNo":"{{Placeholder}}","taxNumber":"{{Vkn}}","taxOffice":"Kadıköy"}""", Corporate, false, Vkn)]
    // Unclear: a VKN without a tax office.
    [InlineData($$"""{"name":"Örnek Ltd","turkishIdentityNumber":"{{Placeholder}}","taxNumber":"{{Vkn}}","taxOffice":""}""", Waits, true, null)]
    // Unclear: a tax office without a tax number.
    [InlineData($$"""{"name":"Örnek Ltd","turkishIdentityNumber":"{{Placeholder}}","taxNumber":"","taxOffice":"Kadıköy"}""", Waits, true, null)]
    // Unclear: a ten-digit number whose check digit fails.
    [InlineData($$"""{"name":"Örnek Ltd","turkishIdentityNumber":"{{Placeholder}}","taxNumber":"9876543210","taxOffice":"Kadıköy"}""", Waits, true, null)]
    // Unclear: an 11-digit (sole proprietor's TCKN-shaped) tax number with a tax office.
    [InlineData($$"""{"name":"Ahmet Esnaf","turkishIdentityNumber":"{{Placeholder}}","taxNumber":"10000000146","taxOffice":"Kadıköy"}""", Waits, true, null)]
    // Unclear: a real personal identity number next to a tax number.
    [InlineData($$"""{"name":"Örnek Ltd","turkishIdentityNumber":"10000000146","taxNumber":"{{Vkn}}","taxOffice":"Kadıköy"}""", Waits, true, null)]
    // A real personal identity number without tax details: the person (unchanged).
    [InlineData("""{"name":"Ayşe Yılmaz","turkishIdentityNumber":"10000000146","taxNumber":"","taxOffice":null}""", Individual, false, Placeholder)]
    public void The_buyer_kind_follows_the_tax_details_beside_the_identity(string invoiceAddress, string kind, bool waits, string? eArchiveTaxId)
    {
        const string customer = """{"customerId":"c1","marketplaceInvoiceStatus":"NOT_INVOICED","name":"Hesap Sahibi"}""";

        Assert.Equal(kind, TrendyolEFaturamCanonicalPayload.ResolveBuyerType(customer, invoiceAddress).Kind);
        Assert.Equal(kind, OrderBuyerTypes.Resolve(customer, invoiceAddress));
        Assert.Equal(waits, TrendyolEFaturamCanonicalPayload.IsRecipientKindAmbiguous(customer, invoiceAddress));
        var (taxId, isCorporate) = TrendyolEFaturamCanonicalPayload.ResolveRecipientTaxIdentity(customer, invoiceAddress, "EARSIVFATURA");
        // An unclear buyer is never the consumer e-Archive: it counts as corporate and is refused before issue.
        Assert.Equal(kind != Individual, isCorporate);
        if (eArchiveTaxId is not null) Assert.Equal(eArchiveTaxId, taxId);
        else Assert.NotEqual(Placeholder, taxId);
        // The taxpayer lookup is asked for every buyer that is not an individual.
        Assert.Equal(kind != Individual, TrendyolEFaturamCanonicalPayload.ExplicitBuyerKind(customer, invoiceAddress) ?? isCorporate);
        Assert.False(TrendyolEFaturamCanonicalPayload.SendsPlaceholderToCorporateTaxNumber(customer, invoiceAddress, "EARSIVFATURA"));
    }

    [Fact]
    public void Order_4476971250_shape_is_a_corporate_buyer_and_its_invoice_goes_to_the_company()
    {
        var buyer = TrendyolEFaturamCanonicalPayload.ResolveBuyerType(CorporateCustomer, CorporateInvoiceAddress);
        Assert.Equal(new OrderBuyerTypeView(Corporate, "Vergi numarası var"), buyer);
        Assert.Equal(Corporate, OrderBuyerTypes.Resolve(CorporateCustomer, CorporateInvoiceAddress));
        // The stored Individual of an order not re-labelled yet is shown as stored (the migration clears it for the backfill).
        Assert.Equal(Individual, OrderBuyerTypes.Label(Individual, CorporateCustomer, CorporateInvoiceAddress).Kind);
        Assert.Equal(Corporate, OrderBuyerTypes.Label(null, CorporateCustomer, CorporateInvoiceAddress).Kind);

        // The preview's recipient (InvoicingWorkspaceWorkflow) and the payload agree: the company, its VKN.
        Assert.True(TrendyolEFaturamCanonicalPayload.IsCorporateRecipient(CorporateCustomer, CorporateInvoiceAddress, Vkn));
        Assert.Equal((Vkn, true), TrendyolEFaturamCanonicalPayload.ResolveRecipientTaxIdentity(CorporateCustomer, CorporateInvoiceAddress, "EARSIVFATURA"));
        Assert.Equal((Vkn, true), TrendyolEFaturamCanonicalPayload.ResolveRecipientTaxIdentity(CorporateCustomer, CorporateInvoiceAddress, "TEMELFATURA"));
        Assert.Equal("ÖRNEK TİCARET LİMİTED ŞİRKETİ", TrendyolEFaturamCanonicalPayload.ResolveRecipientName(CorporateCustomer, CorporateInvoiceAddress, "EARSIVFATURA"));
        Assert.True(TrendyolEFaturamCanonicalPayload.ExplicitBuyerKind(CorporateCustomer, CorporateInvoiceAddress));
        Assert.Null(InvoicingBillingService.RecipientIdentityRefusal("EARSIVFATURA", CorporateCustomer, CorporateInvoiceAddress));
        Assert.True(InvoicingBillingService.RecipientTaxIdentityIsValid("EARSIVFATURA", CorporateCustomer, CorporateInvoiceAddress));

        using var payload = JsonDocument.Parse(TrendyolEFaturamCanonicalPayload.Create(new(1, 2, null), Canonical("EARSIVFATURA", CorporateCustomer, CorporateInvoiceAddress)));
        var recipient = payload.RootElement.GetProperty("recipientInfo");
        Assert.Equal(Vkn, recipient.GetProperty("taxId").GetString());
        Assert.Equal("ÖRNEK TİCARET LİMİTED ŞİRKETİ", recipient.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Order_4476971250_shape_asks_the_taxpayer_lookup_with_its_vkn()
    {
        var registered = new LookupOnlyProvider(new(EInvoiceTaxpayerState.Registered, "ÖRNEK TİCARET", [new("urn:mail:defaultpk@example.test", "PK")]));
        var eInvoice = await Resolve(registered, "HEPSIBURADA", CorporateCustomer, CorporateInvoiceAddress);
        Assert.Equal(Vkn, Assert.Single(registered.TaxIds));
        Assert.Equal(("TEMELFATURA", "urn:mail:defaultpk@example.test"), (eInvoice.InvoiceType, eInvoice.TargetAlias));
        Assert.Null(eInvoice.Refusal);

        var notRegistered = await Resolve(new LookupOnlyProvider(EInvoiceTaxpayerLookup.NotRegistered()), "HEPSIBURADA", CorporateCustomer, CorporateInvoiceAddress);
        Assert.Equal("EARSIVFATURA", notRegistered.InvoiceType);
        Assert.Null(notRegistered.Refusal);

        // A failed lookup stops the invoice (owner's rule: the type is automatic, a lookup failure stops).
        var unknown = await Resolve(null, "HEPSIBURADA", CorporateCustomer, CorporateInvoiceAddress);
        Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientReviewCode, unknown.Refusal?.Code);
    }

    [Fact]
    public async Task An_unclear_hepsiburada_buyer_is_refused_without_a_lookup()
    {
        var address = CorporateInvoiceAddress.Replace("\"taxOffice\":\"ÖRNEKVD\"", "\"taxOffice\":\"\"", StringComparison.Ordinal);
        Assert.NotEqual(CorporateInvoiceAddress, address);
        var provider = new LookupOnlyProvider(EInvoiceTaxpayerLookup.NotRegistered());

        var decision = await Resolve(provider, "HEPSIBURADA", CorporateCustomer, address);

        Assert.Empty(provider.TaxIds);
        Assert.Equal(InvoiceIssuePolicy.RecipientKindUnknown, decision.Refusal);
        Assert.Equal(InvoiceIssuePolicy.RecipientKindUnknown, InvoiceIssuePolicy.Refusal(decision.InvoiceType, "TRY", CorporateCustomer, decision));
        Assert.Equal(InvoiceIssuePolicy.RecipientKindUnknown, InvoicingBillingService.RecipientIdentityRefusal("EARSIVFATURA", CorporateCustomer, address));
        Assert.Equal(InvoiceIssuePolicy.RecipientKindUnknownCode,
            Assert.Throws<JsonException>(() => TrendyolEFaturamCanonicalPayload.Create(new(1, 2, null), Canonical("EARSIVFATURA", CorporateCustomer, address))).Message);
        Assert.Contains("fatura kesilmedi", InvoiceIssuePolicy.DescribeValidationCode(InvoiceIssuePolicy.RecipientKindUnknownCode), StringComparison.Ordinal);
    }

    // Item 4, every marketplace: a company's valid VKN on the order and 11111111111 on the invoice is refused at the
    // preview and draft (the resolver), at the submit gates and at the payload itself. Here a marketplace that states an
    // individual buyer (customerType) while the invoice address carries a VKN.
    [Fact]
    public async Task A_vkn_on_the_order_never_goes_out_as_11111111111()
    {
        const string customer = """{"customerType":"Bireysel Müşteri"}""";
        const string address = $$"""{"address":"Örnek Cadde No:1","name":"Ayşe Örnek","city":"Konya","district":"Selçuklu","taxNumber":"{{Vkn}}"}""";

        Assert.True(TrendyolEFaturamCanonicalPayload.SendsPlaceholderToCorporateTaxNumber(customer, address, "EARSIVFATURA"));
        var decision = await Resolve(new LookupOnlyProvider(EInvoiceTaxpayerLookup.NotRegistered()), "SHOPIFY", customer, address);
        Assert.Equal(InvoiceIssuePolicy.RecipientPlaceholderForCorporate, decision.Refusal);
        Assert.Equal(InvoiceIssuePolicy.RecipientPlaceholderForCorporate, InvoiceIssuePolicy.Refusal("EARSIVFATURA", "TRY", customer, decision));
        Assert.Equal(InvoiceIssuePolicy.RecipientPlaceholderForCorporate, InvoicingBillingService.RecipientIdentityRefusal("EARSIVFATURA", customer, address));
        Assert.Equal(InvoiceIssuePolicy.RecipientPlaceholderForCorporateCode,
            Assert.Throws<JsonException>(() => TrendyolEFaturamCanonicalPayload.Create(new(1, 2, null), Canonical("EARSIVFATURA", customer, address))).Message);

        // The guard does not touch an individual without a VKN, nor a TCKN.
        const string person = """{"address":"Örnek Cadde No:1","name":"Ayşe Örnek","city":"Konya","taxNumber":"10000000146"}""";
        Assert.False(TrendyolEFaturamCanonicalPayload.SendsPlaceholderToCorporateTaxNumber(customer, person, "EARSIVFATURA"));
        Assert.Null(InvoicingBillingService.RecipientIdentityRefusal("EARSIVFATURA", customer, person));
    }

    private static Task<EInvoiceRecipientDecision> Resolve(IInvoiceProviderPort? provider, string platform, string customer, string address) =>
        EInvoiceRecipientResolver.ResolveAsync(provider, new AdapterContext(Guid.NewGuid(), Guid.NewGuid(), "test", "test", DateTimeOffset.UtcNow.AddMinutes(1)),
            platform, customer, address, fresh: true, CancellationToken.None);

    private static string Canonical(string invoiceType, string customer, string address) => JsonSerializer.Serialize(new
    {
        Id = "invoice-synthetic",
        InvoiceType = invoiceType,
        Currency = "TRY",
        Note = "Satış faturası",
        IssuedAt = "2026-11-16T12:00:00+03:00",
        Order = new { OrderNumber = "1000000001", OrderedAt = "2026-11-11T12:38:27+03:00", CustomerSnapshotJson = customer, InvoiceAddressSnapshotJson = address },
        Package = new { CargoProviderExternalId = "HepsiJet", StatusOccurredAt = "2026-11-12T13:03:01+03:00" },
        Lines = new[] { new { DescriptionSnapshot = "Ürün", UnitSnapshot = "ADET", Quantity = 1, UnitPrice = 100, LineTotal = 120, VatAmount = 20, VatRate = 20, DiscountAmount = 0 } }
    });

}

// An invoice provider that only answers the taxpayer lookup (it never issues anything), and records what it was asked.
internal sealed class LookupOnlyProvider(EInvoiceTaxpayerLookup answer) : IInvoiceProviderPort
{
    public List<string> TaxIds { get; } = [];

    public Task<EInvoiceTaxpayerLookup> LookupTaxpayerAsync(AdapterContext context, string taxId, bool fresh, CancellationToken cancellationToken)
    {
        lock (TaxIds) TaxIds.Add(taxId);
        return Task.FromResult(answer);
    }

    public Task<AdapterResult<ConnectionIdentity>> TestConnectionAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<AdapterResult<InvoiceSubmissionResult>> SubmitAsync(AdapterContext context, InvoiceSubmission submission, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<AdapterResult<InvoiceRemoteStatus>> QueryStatusAsync(AdapterContext context, ExternalInvoiceReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<AdapterResult<RemoteInvoiceDocument>> GetDocumentAsync(AdapterContext context, ExternalInvoiceReference reference, string documentKind, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<AdapterResult<InvoiceCancellationResult>> CancelAsync(AdapterContext context, InvoiceCancellation command, CancellationToken cancellationToken) => throw new NotSupportedException();
}
