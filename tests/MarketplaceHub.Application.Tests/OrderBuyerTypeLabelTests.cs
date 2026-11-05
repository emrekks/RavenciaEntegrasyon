using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The order's Bireysel / Kurumsal label follows exactly the invoice's buyer rule (report item 12): the marketplace's own
// flag first, then the tax identity (VKN corporate, TCKN individual), then a company name (corporate, tax number asked
// when the invoice is issued), else individual. One case per marketplace and per reason.
public sealed class OrderBuyerTypeLabelTests
{
    private const string Individual = OrderBuyerTypeView.Individual;
    private const string Corporate = OrderBuyerTypeView.Corporate;
    private const string Missing = OrderBuyerTypeView.CorporateTaxIdMissing;

    [Theory]
    // Trendyol: commercial on the package.
    [InlineData("""{"commercial":true}""", """{"company":"Firma A.Ş.","taxNumber":"1234567890","taxOffice":"Kadıköy"}""", Corporate, "Pazaryeri kurumsal olarak işaretledi")]
    [InlineData("""{"commercial":true}""", """{"company":"Firma A.Ş.","taxNumber":""}""", Missing, "Pazaryeri kurumsal olarak işaretledi, vergi numarası yok — fatura kesilirken sorulacak")]
    [InlineData("""{"commercial":false}""", """{"company":"","taxNumber":"1234567890"}""", Individual, "Pazaryeri bireysel olarak işaretledi")]
    // n11: invoiceType inside the order's billing address.
    [InlineData("""{"billingAddress":{"invoiceType":2,"taxId":"1234567890","taxHouse":"Beşiktaş"}}""", "{}", Corporate, "Pazaryeri kurumsal olarak işaretledi")]
    [InlineData("""{"billingAddress":{"invoiceType":1,"tcId":"10000000146"}}""", "{}", Individual, "Pazaryeri bireysel olarak işaretledi")]
    // Pazarama: invoiceType kept on the invoice address.
    [InlineData("{}", """{"invoiceType":"2","companyName":"Firma","taxNumber":"1234567890"}""", Corporate, "Pazaryeri kurumsal olarak işaretledi")]
    [InlineData("{}", """{"invoiceType":"1","companyName":"X"}""", Individual, "Pazaryeri bireysel olarak işaretledi")]
    // PTT AVM: FaturaTip, kept as invoiceType.
    [InlineData("""{"invoiceType":"Kurumsal"}""", """{"taxNumber":"0720039666"}""", Corporate, "Pazaryeri kurumsal olarak işaretledi")]
    [InlineData("""{"invoiceType":"Bireysel"}""", "{}", Individual, "Pazaryeri bireysel olarak işaretledi")]
    // Hepsiburada: identityNo 11111111111 only for an individual buyer; otherwise its tax number decides.
    [InlineData("{}", """{"companyName":"Test User","identityNo":"11111111111","taxNumber":""}""", Individual, "Pazaryeri bireysel olarak işaretledi")]
    [InlineData("{}", """{"companyName":"Firma Ltd","taxNumber":"1234567890","taxOffice":"Şişli"}""", Corporate, "Vergi numarası var")]
    // Shopify (and any marketplace without a flag): the tax identity, then a company name.
    [InlineData("{}", """{"tcIdentityNumber":"10000000146"}""", Individual, "TC kimlik numarası var")]
    [InlineData("{}", """{"company":"Acme"}""", Missing, "Şirket adı var, vergi numarası yok — fatura kesilirken sorulacak")]
    [InlineData("{}", """{"firstName":"Ayşe","lastName":"Yılmaz"}""", Individual, "Bilgi yok, bireysel sayıldı")]
    public void Label_follows_the_invoice_buyer_rule(string customer, string address, string kind, string reason)
    {
        var buyer = TrendyolEFaturamCanonicalPayload.ResolveBuyerType(customer, address);
        Assert.Equal(new OrderBuyerTypeView(kind, reason), buyer);
        // The label never disagrees with the invoice's own decision.
        Assert.Equal(kind != Individual, TrendyolEFaturamCanonicalPayload.ResolveRecipientTaxIdentity(customer, address, "EARSIVFATURA").IsCorporate);
    }

    [Fact]
    public void A_corporate_tax_number_with_wrong_check_digits_is_asked_like_a_missing_one()
    {
        var buyer = TrendyolEFaturamCanonicalPayload.ResolveBuyerType("""{"commercial":true}""", """{"company":"Firma","taxNumber":"1234567891"}""");
        Assert.Equal(Missing, buyer.Kind);
        Assert.Contains("geçersiz", buyer.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("not json", "{}")]
    public void Unreadable_snapshots_count_as_individual_as_they_do_for_the_invoice(string? customer, string? address) =>
        Assert.Equal(new OrderBuyerTypeView(Individual, "Bilgi yok, bireysel sayıldı"), TrendyolEFaturamCanonicalPayload.ResolveBuyerType(customer, address));

    [Theory]
    [InlineData("""{"commercial":true}""", """{"company":"Firma"}""", false, "KURUMSAL")]
    [InlineData("{}", """{"firstName":"Ayşe"}""", false, "BIREYSEL")]
    // Trendyol sends empty company fields on individual orders: no longer KURUMSAL.
    [InlineData("""{"commercial":false}""", """{"company":"","companyName":"","taxOffice":""}""", false, "BIREYSEL")]
    [InlineData("""{"commercial":true}""", """{"company":"Firma"}""", true, "MIKRO_IHRACAT")]
    public void Order_type_follows_the_same_buyer_rule_after_micro_export(string customer, string address, bool micro, string orderType) =>
        Assert.Equal(orderType, MarketplaceSalesService.BuyerType(customer, address, micro).OrderType);

    // The "Fatura Türü" filter reads Order.BuyerType; the label reads the same stored value, with the rule's reason.
    [Fact]
    public void The_label_shows_the_stored_kind_with_the_rules_reason_and_the_rule_when_nothing_is_stored()
    {
        const string customer = """{"commercial":true}""";
        const string address = """{"company":"Firma A.Ş.","taxNumber":"1234567890"}""";
        Assert.Equal(new OrderBuyerTypeView(Corporate, "Pazaryeri kurumsal olarak işaretledi"), MarketplaceSalesService.BuyerType(customer, address, false, Corporate).BuyerType);
        Assert.Equal(new OrderBuyerTypeView(Corporate, "Pazaryeri kurumsal olarak işaretledi"), MarketplaceSalesService.BuyerType(customer, address, false, null).BuyerType);
        // A stored kind the current rule no longer gives (the rule changed and the column was not cleared) still decides
        // the label and the order type, so the row never contradicts the filter it matched.
        var stale = MarketplaceSalesService.BuyerType(customer, address, false, Individual);
        Assert.Equal(Individual, stale.BuyerType.Kind);
        Assert.Equal("BIREYSEL", stale.OrderType);
    }

    [Fact]
    public void Every_save_of_a_new_order_or_a_changed_snapshot_keeps_the_kind_by_the_rule()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        var added = TestOrder("""{"commercial":true}""", """{"company":"Firma","taxNumber":""}""");
        db.Orders.Add(added);
        var changed = TestOrder("{}", """{"firstName":"Ayşe"}""", Individual);
        db.Orders.Attach(changed);
        changed.InvoiceAddressSnapshotJson = """{"company":"Firma Ltd","taxNumber":"1234567890"}""";
        var untouched = TestOrder("{}", """{"company":"Firma Ltd","taxNumber":"1234567890"}""", Individual);
        db.Orders.Attach(untouched);
        untouched.DerivedStatus = "SHIPPED";
        var notYetFilled = TestOrder("""{"invoiceType":"Bireysel"}""", "{}");
        db.Orders.Attach(notYetFilled);
        notYetFilled.DerivedStatus = "SHIPPED";
        db.ChangeTracker.DetectChanges();

        OrderBuyerTypes.Apply(db.ChangeTracker);

        Assert.Equal(Missing, added.BuyerType);
        Assert.Equal(Corporate, changed.BuyerType);
        // A save that touches no snapshot leaves the stored kind alone.
        Assert.Equal(Individual, untouched.BuyerType);
        Assert.False(db.Entry(untouched).Property(x => x.BuyerType).IsModified);
        Assert.Equal(Individual, notYetFilled.BuyerType);
    }

    private static Order TestOrder(string customer, string address, string? buyerType = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        ConnectionId = Guid.CreateVersion7(),
        ExternalOrderId = Guid.NewGuid().ToString("N"),
        OrderNumber = "1",
        Currency = "TRY",
        CustomerSnapshotJson = customer,
        ShipmentAddressSnapshotJson = "{}",
        InvoiceAddressSnapshotJson = address,
        DerivedStatus = "NEW",
        BuyerType = buyerType
    };
}
