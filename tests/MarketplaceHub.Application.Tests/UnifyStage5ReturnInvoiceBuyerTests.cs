using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Report items 9, 10, 11, 12 and 18 (stage 5: returns, invoices, buyer): one rule per item, a case per marketplace.
public sealed class UnifyStage5ReturnInvoiceBuyerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    // Item 9: every status word a marketplace sends today is mapped on purpose; anything else is Unknown.
    [Theory]
    [InlineData("WaitingInAction", ReturnClaimStatus.ActionRequired)]       // Trendyol
    [InlineData("InAnalysis", ReturnClaimStatus.ActionRequired)]            // Trendyol
    [InlineData("WaitingFraudCheck", ReturnClaimStatus.ActionRequired)]     // Trendyol
    [InlineData("Created", ReturnClaimStatus.Requested)]                    // Trendyol
    [InlineData("Unresolved", ReturnClaimStatus.Disputed)]                  // Trendyol
    [InlineData("NewRequest", ReturnClaimStatus.Requested)]                 // Hepsiburada
    [InlineData("AwaitingAction", ReturnClaimStatus.ActionRequired)]        // Hepsiburada
    [InlineData("AwaitingPreApproval", ReturnClaimStatus.ActionRequired)]   // Hepsiburada
    [InlineData("InDispute", ReturnClaimStatus.Disputed)]                   // Hepsiburada
    [InlineData("Refunded", ReturnClaimStatus.Completed)]                   // Hepsiburada
    [InlineData("REQUESTED", ReturnClaimStatus.ActionRequired)]             // Shopify
    [InlineData("OPEN", ReturnClaimStatus.Approved)]                        // Shopify
    [InlineData("DECLINED", ReturnClaimStatus.Rejected)]                    // Shopify
    [InlineData("CLOSED", ReturnClaimStatus.Completed)]                     // Shopify
    [InlineData("CANCELED", ReturnClaimStatus.Cancelled)]                   // Shopify
    [InlineData("Completed", ReturnClaimStatus.Completed)]                  // PTT AVM
    [InlineData("SOMETHING_NEW", ReturnClaimStatus.Unknown)]
    [InlineData("", ReturnClaimStatus.Unknown)]
    [InlineData(null, ReturnClaimStatus.Unknown)]
    public void Return_status_words_are_mapped_explicitly(string? raw, ReturnClaimStatus expected) =>
        Assert.Equal(expected, MarketplaceReturnStatus.Canonicalize(raw));

    [Theory]
    [InlineData("REQUESTED", ReturnClaimStatus.Requested)]
    [InlineData("PENDING", ReturnClaimStatus.ActionRequired)]
    [InlineData("PENDED", ReturnClaimStatus.ActionRequired)]
    [InlineData("APPROVAL_WAITING", ReturnClaimStatus.ActionRequired)]
    [InlineData("APPROVED", ReturnClaimStatus.Approved)]
    [InlineData("DENIED", ReturnClaimStatus.Rejected)]
    [InlineData("CANCELLED", ReturnClaimStatus.Cancelled)]
    [InlineData("MANUAL_REFUND", ReturnClaimStatus.Completed)]
    [InlineData("SOMETHING_NEW", ReturnClaimStatus.Unknown)]
    public void N11_return_states_are_mapped_explicitly(string n11Status, ReturnClaimStatus expected) =>
        Assert.Equal(expected, MarketplaceReturnStatus.Canonicalize(N11ReturnMapper.CanonicalStatus(n11Status)));

    [Theory]
    [InlineData(1, ReturnClaimStatus.ActionRequired)]
    [InlineData(2, ReturnClaimStatus.Approved)]
    [InlineData(3, ReturnClaimStatus.Rejected)]
    [InlineData(7, ReturnClaimStatus.Cancelled)]
    [InlineData(9, ReturnClaimStatus.Unknown)]
    [InlineData(null, ReturnClaimStatus.Unknown)]
    public void Pazarama_return_codes_are_mapped_explicitly(int? code, ReturnClaimStatus expected) =>
        Assert.Equal(expected, MarketplaceReturnStatus.Canonicalize(PazaramaReturnMapper.CanonicalStatus(code)));

    [Fact]
    public void An_unknown_line_offers_no_decision_and_an_open_claim_can_become_unknown()
    {
        Assert.False(ReturnLineStatusPolicy.IsActionable("SOMETHING_NEW"));
        Assert.True(ReturnClaimStateMachine.CanTransition(ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Unknown));
        Assert.True(ReturnClaimStateMachine.CanTransition(ReturnClaimStatus.Unknown, ReturnClaimStatus.ActionRequired));
        // A decided claim keeps its known outcome.
        Assert.False(ReturnClaimStateMachine.CanTransition(ReturnClaimStatus.Approved, ReturnClaimStatus.Unknown));
        Assert.False(ReturnClaimStateMachine.CanTransition(ReturnClaimStatus.Completed, ReturnClaimStatus.Unknown));
    }

    // Item 10: quantity and product code are read; a missing quantity is unknown (0), never 1.
    [Fact]
    public void N11_claim_without_quantity_is_unknown_and_names_its_product()
    {
        var element = XElement.Parse("<claimReturn><claimReturnId>1</claimReturnId><orderNumber>2</orderNumber><status>PENDING</status><productId>4455</productId></claimReturn>");
        var line = Assert.Single(N11ReturnMapper.Claim(element)!.Lines);
        Assert.Equal(0m, line.Quantity);
        Assert.Equal("4455", line.ProductCode);
    }

    [Fact]
    public void Pazarama_refund_names_its_product_and_reads_a_quantity_when_sent()
    {
        var row = JsonDocument.Parse("""{"refundId":"ef2affaf-fdfc-4128-b5bc-3a4c1129e662","orderNumber":1,"refundStatus":1,"productCode":"B1","ProductStockCode":"S1","quantity":2}""").RootElement;
        var line = Assert.Single(PazaramaReturnMapper.Claim(row, Now)!.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("B1", line.ProductCode);
        Assert.Equal("S1", line.StockCode);

        var withoutQuantity = JsonDocument.Parse("""{"refundId":"ef2affaf-fdfc-4128-b5bc-3a4c1129e662","orderNumber":1,"refundStatus":1,"refundAmount":{"value":30}}""").RootElement;
        Assert.Equal(0m, Assert.Single(PazaramaReturnMapper.Claim(withoutQuantity, Now)!.Lines).Quantity);
        Assert.Equal(30m, PazaramaReturnMapper.RefundAmount(withoutQuantity));
    }

    [Theory]
    [InlineData(2, null, 10, 3, 2)]     // sent
    [InlineData(0, null, 10, 1, 1)]     // a line of one unit
    [InlineData(0, 30, 15, 3, 2)]       // refund over unit price, whole
    [InlineData(0, 25, 15, 3, null)]    // not whole: unknown
    [InlineData(0, 60, 15, 3, null)]    // more than the line: unknown
    [InlineData(0, null, 15, 3, null)]  // nothing to go on: unknown
    public void Pazarama_quantity_is_read_or_worked_out_or_unknown(int sent, int? refund, int unitPrice, int lineQuantity, int? expected) =>
        Assert.Equal(expected is null ? null : (decimal)expected.Value, PazaramaReturnMapper.ResolveQuantity(sent, refund, unitPrice, lineQuantity));

    [Fact]
    public void Shopify_unverified_return_line_is_kept_to_be_reported()
    {
        using var item = JsonDocument.Parse("""{"id":"gid://shopify/Return/1","status":"REQUESTED","createdAt":"2026-11-01T10:00:00Z"}""");
        using var order = JsonDocument.Parse("""{"id":"gid://shopify/Order/9","createdAt":"2026-10-30T10:00:00Z"}""");
        using var verified = JsonDocument.Parse("""{"id":"gid://shopify/ReturnLineItem/1","quantity":1,"fulfillmentLineItem":{"id":"gid://shopify/FulfillmentLineItem/5","lineItem":{"id":"gid://shopify/LineItem/7"}}}""");
        using var unverified = JsonDocument.Parse("""{"id":"gid://shopify/UnverifiedReturnLineItem/2","quantity":1}""");

        var claim = ShopifyHttpClient.MapShopifyReturn(item.RootElement, order.RootElement, [verified.RootElement, unverified.RootElement]);

        Assert.Equal(2, claim.Lines.Count);
        Assert.Equal("", claim.Lines.Single(line => line.ExternalLineId == "2").ExternalOrderLineId);
        Assert.Equal(ReturnClaimStatus.ActionRequired, MarketplaceReturnStatus.Canonicalize(claim.RawStatus));
    }

    // Item 11: an uncertain delivery goes to the automatic retry schedule on n11, Pazarama and PTT AVM only.
    [Theory]
    [InlineData("N11", true, true)]
    [InlineData("PAZARAMA", true, true)]
    [InlineData("PTTAVM", false, true)]
    [InlineData("TRENDYOL", false, false)]
    [InlineData("HEPSIBURADA", false, false)]
    [InlineData("SHOPIFY", false, false)]
    public void Uncertain_delivery_platforms(string platform, bool replacesLink, bool retried)
    {
        Assert.Equal(replacesLink, InvoiceDeliveryAutoRetryPolicy.ResendReplacesLink(platform));
        Assert.Equal(retried, InvoiceDeliveryAutoRetryPolicy.RetriesUncertainDelivery(platform));
    }

    [Fact]
    public void Uncertain_resend_follows_the_backoff_and_stops_after_the_last_attempt()
    {
        var since = Now.AddMinutes(-10);
        Assert.Equal(InvoiceDeliveryAutoRetryPolicy.UncertainResend.Wait, InvoiceDeliveryAutoRetryPolicy.UncertainResendDue(0, since, Now.AddDays(-1), Now));
        Assert.Equal(InvoiceDeliveryAutoRetryPolicy.UncertainResend.Now, InvoiceDeliveryAutoRetryPolicy.UncertainResendDue(0, Now.AddMinutes(-16), Now.AddDays(-1), Now));
        Assert.Equal(InvoiceDeliveryAutoRetryPolicy.UncertainResend.Wait, InvoiceDeliveryAutoRetryPolicy.UncertainResendDue(2, Now.AddMinutes(-30), Now.AddDays(-1), Now));
        Assert.Equal(InvoiceDeliveryAutoRetryPolicy.UncertainResend.Exhausted, InvoiceDeliveryAutoRetryPolicy.UncertainResendDue(InvoiceDeliveryAutoRetryPolicy.MaxAttempts, Now.AddDays(-2), Now.AddDays(-3), Now));
    }

    [Fact]
    public void Uncertain_delivery_is_retried_and_offered_to_the_owner_only_where_a_resend_settles_it()
    {
        // n11 / Pazarama / PTT AVM: the scheduler's path and the owner's Tekrar Gönder.
        Assert.Equal(InvoiceDeliveryRetryNeed.Deliver, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.MarketplacePending, true, true, ["UNKNOWN"], true, null, uncertainRetried: true));
        Assert.Equal(InvoiceDeliveryRetryNeed.Deliver, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.MarketplacePending, true, true, ["UNKNOWN"], true, null, owner: true, uncertainRetried: true));
        // Elsewhere an uncertain delivery stays with the read-back.
        Assert.Equal(InvoiceDeliveryRetryNeed.None, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.MarketplacePending, true, true, ["UNKNOWN"], true, null));
        Assert.Equal(InvoiceDeliveryRetryNeed.None, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.MarketplacePending, true, true, ["UNKNOWN"], true, null, owner: true));
        // A delivery that is still in flight is never written over.
        Assert.Equal(InvoiceDeliveryRetryNeed.None, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.MarketplacePending, true, true, ["UNKNOWN", "SUBMITTED"], true, null, uncertainRetried: true));
    }

    [Fact]
    public void Owner_documents_reach_n11_pazarama_and_pttavm_under_their_rules()
    {
        foreach (var platform in new[] { "TRENDYOL", "HEPSIBURADA", "SHOPIFY", "N11", "PAZARAMA", "PTTAVM" })
            Assert.True(ManualInvoiceDeliveryPolicy.SupportsManualDocument(platform));
        Assert.True(ManualInvoiceDeliveryPolicy.RequiresDocumentExtension("N11"));
        Assert.False(ManualInvoiceDeliveryPolicy.RequiresDocumentExtension("TRENDYOL"));
        Assert.True(ManualInvoiceDeliveryPolicy.HasDocumentExtension("https://example.com/f/invoice.PDF"));
        Assert.False(ManualInvoiceDeliveryPolicy.HasDocumentExtension("https://example.com/f/invoice"));
        // An uploaded PDF is sent under its .pdf link.
        Assert.Equal("https://panel.example.com/api/v1/public/manual-invoice-documents/tok/invoice.pdf",
            InvoiceDocumentLink.PdfAlias("https://panel.example.com/api/v1/public/manual-invoice-documents/tok/content"));
    }

    // Item 12: the buyer kind the marketplace states decides; without it the tax identity; Shopify company asks.
    [Theory]
    [InlineData("""{"commercial":true}""", """{"companyName":"Firma"}""", true)]                                 // Trendyol corporate
    [InlineData("""{"commercial":false}""", """{"companyName":"Firma","taxNumber":"1234567890"}""", false)]      // Trendyol individual
    [InlineData("""{"billingAddress":{"invoiceType":2}}""", "{}", true)]                                           // n11 corporate
    [InlineData("""{"invoiceType":"1","companyName":"X"}""", "{}", false)]                                         // Pazarama individual
    [InlineData("""{"invoiceType":"Kurumsal"}""", "{}", true)]                                                     // PTT AVM corporate
    [InlineData("{}", """{"companyName":"Test User","identityNo":"11111111111","taxNumber":""}""", false)]        // Hepsiburada individual
    [InlineData("{}", """{"taxNumber":"1234567890"}""", true)]                                                     // no flag: VKN
    [InlineData("{}", """{"tcIdentityNumber":"12345678901"}""", false)]                                            // no flag: TCKN
    [InlineData("{}", """{"company":"Acme"}""", true)]                                                             // Shopify company without tax id: owner asked
    [InlineData("{}", """{"firstName":"Ayşe"}""", false)]                                                          // Shopify, no company
    public void Buyer_kind_follows_the_marketplace_flag_then_the_tax_identity(string customer, string address, bool corporate) =>
        Assert.Equal(corporate, TrendyolEFaturamCanonicalPayload.ResolveRecipientTaxIdentity(customer, address, "EARSIVFATURA").IsCorporate);

    // Item 18: the internet sale info has one structure everywhere; Shopify fills it from its own facts.
    [Theory]
    [InlineData("N11", "https://www.n11.com")]
    [InlineData("PAZARAMA", "https://www.pazarama.com")]
    [InlineData("PTTAVM", "https://www.pttavm.com")]
    [InlineData("TRENDYOL", "https://www.trendyol.com")]
    [InlineData("HEPSIBURADA", "https://www.hepsiburada.com")]
    public void Marketplace_sales_are_paid_through_the_marketplace(string platform, string site)
    {
        var payment = TrendyolEFaturamCanonicalPayload.InternetSalePayment(platform, "{}", Now, out var problem);
        Assert.Null(problem);
        Assert.Equal(site, payment!.PurchaseUrl);
        Assert.Equal("MEDIATOR", payment.PaymentMeans);
    }

    [Fact]
    public void Shopify_sale_uses_the_shop_domain_and_its_payment_gateway_or_turns_the_option_off()
    {
        var payment = TrendyolEFaturamCanonicalPayload.InternetSalePayment("SHOPIFY",
            """{"shopPrimaryDomainUrl":"https://magaza.example.com","paymentGatewayNames":["shopify_payments"]}""", Now, out var problem);
        Assert.Null(problem);
        Assert.Equal("https://magaza.example.com", payment!.PurchaseUrl);
        Assert.Equal("CREDIT_CARD", payment.PaymentMeans);

        Assert.Null(TrendyolEFaturamCanonicalPayload.InternetSalePayment("SHOPIFY", """{"paymentGatewayNames":["manual"]}""", Now, out problem));
        Assert.Equal(TrendyolEFaturamCanonicalPayload.ShopifyPaymentInfoMissing, problem);
    }

    [Theory]
    [InlineData("Cash on Delivery (COD)", "ON_DELIVERY")]
    [InlineData("Bank Deposit", "EFT")]
    [InlineData("iyzico", "CREDIT_CARD")]
    [InlineData("manual", "OTHER")]
    public void Shopify_payment_gateways_name_the_payment_means(string gateway, string means) =>
        Assert.Equal(means, TrendyolEFaturamCanonicalPayload.ShopifyPaymentMeans([gateway]));

    [Fact]
    public void Shopify_order_keeps_its_gateways_and_shop_domain_on_the_customer_snapshot()
    {
        using var order = JsonDocument.Parse("""{"paymentGatewayNames":["shopify_payments"]}""");
        var snapshot = ShopifyHttpClient.WithInternetSaleFacts("{}", order.RootElement, "https://magaza.example.com");
        Assert.Contains("\"shopPrimaryDomainUrl\":\"https://magaza.example.com\"", snapshot);
        Assert.Contains("shopify_payments", snapshot);
        Assert.Equal("{}", ShopifyHttpClient.WithInternetSaleFacts("{}", JsonDocument.Parse("{}").RootElement, null));
    }

    [Fact]
    public void Internet_sale_info_is_off_unless_the_owner_turns_it_on()
    {
        Assert.False(new CreateInvoiceCommand(Guid.NewGuid(), null, Guid.NewGuid(), null).IncludeInternetSalesInfo);
        Assert.False(new InvoiceWorkspacePreviewRequest([]).IncludeInternetSalesInfo);
    }
}
