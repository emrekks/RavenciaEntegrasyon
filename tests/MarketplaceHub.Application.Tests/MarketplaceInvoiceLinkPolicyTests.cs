using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner decision 17 Nov 2026: the marketplaces get E-Faturam's permanent document URL as the invoice link. The shape is
// the one seen on live (efaturam.md); each adapter is fed the delivery payload the invoicing job builds.
public sealed class MarketplaceInvoiceLinkPolicyTests
{
    private const string ProviderUrl = "https://apigateway.trendyolecozum.com/api/invoice/documents/download/5072747";
    private const string PanelLink = "https://panel.ravencia.com/api/v1/public/invoice-documents/tok-EN_1/content";
    private const string InvoiceNumber = "DAP2026000000777";
    private static readonly long IssuedAt = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Theory]
    [InlineData("TRENDYOL", null, true)]
    [InlineData("HEPSIBURADA", "Provider", true)]
    [InlineData("PTTAVM", "", true)]
    [InlineData("SHOPIFY", "provider", true)]
    [InlineData("TRENDYOL", "something-else", true)]
    [InlineData("N11", null, false)]
    [InlineData("PAZARAMA", "Provider", false)]
    [InlineData("TRENDYOL", "Panel", false)]
    [InlineData("HEPSIBURADA", " panel ", false)]
    [InlineData(null, null, false)]
    public void TheProviderLinkIsTheDefaultExceptNElevenPazaramaAndThePanelSetting(string? platformCode, string? setting, bool provider) =>
        Assert.Equal(provider, MarketplaceInvoiceLinkPolicy.UsesProviderLink(platformCode, setting));

    [Theory]
    [InlineData(null, MarketplaceInvoiceLinkPolicy.ProviderLinkMissingCode)]
    [InlineData(" ", MarketplaceInvoiceLinkPolicy.ProviderLinkMissingCode)]
    [InlineData("http://apigateway.trendyolecozum.com/api/invoice/documents/download/1", MarketplaceInvoiceLinkPolicy.ProviderLinkInvalidCode)]
    [InlineData("https://user:secret@apigateway.trendyolecozum.com/x", MarketplaceInvoiceLinkPolicy.ProviderLinkInvalidCode)]
    [InlineData("https://apigateway.trendyolecozum.com:8443/x", MarketplaceInvoiceLinkPolicy.ProviderLinkInvalidCode)]
    [InlineData("https://apigateway.trendyolecozum.com/x#frag", MarketplaceInvoiceLinkPolicy.ProviderLinkInvalidCode)]
    [InlineData("/api/invoice/documents/download/1", MarketplaceInvoiceLinkPolicy.ProviderLinkInvalidCode)]
    public void AMissingOrUnsafeProviderUrlIsRefusedWithACodeAndNoUrlInTheReason(string? url, string code)
    {
        var rejection = MarketplaceInvoiceLinkPolicy.ProviderLinkRejection("TRENDYOL", url);
        Assert.NotNull(rejection);
        Assert.Equal(code, rejection.Value.Code);
        Assert.Contains("Trendyol", rejection.Value.Message);
        if (!string.IsNullOrWhiteSpace(url)) Assert.DoesNotContain(url, rejection.Value.Message);
    }

    [Fact]
    public void AnOverlongProviderUrlAndAnExtensionOnlyMarketplaceAreRefused()
    {
        Assert.Equal(MarketplaceInvoiceLinkPolicy.ProviderLinkInvalidCode,
            MarketplaceInvoiceLinkPolicy.ProviderLinkRejection("HEPSIBURADA", "https://apigateway.trendyolecozum.com/" + new string('a', 2100))?.Code);
        Assert.Equal(MarketplaceInvoiceLinkPolicy.ProviderLinkInvalidCode, MarketplaceInvoiceLinkPolicy.ProviderLinkRejection("N11", ProviderUrl)?.Code);
        Assert.Null(MarketplaceInvoiceLinkPolicy.ProviderLinkRejection("PTTAVM", ProviderUrl));
    }

    [Fact]
    public void TrendyolSendsTheProviderUrlAsTheInvoiceLink()
    {
        using var payload = JsonDocument.Parse(Payload());
        var body = TrendyolHttpClient.InvoiceLinkBody(payload.RootElement);
        Assert.NotNull(body);
        Assert.Equal(ProviderUrl, body["invoiceLink"]);
    }

    [Fact]
    public void HepsiburadaSendsTheProviderUrlAsTheInvoiceLink()
    {
        var command = new InvoiceDeliveryCommand("123456", "LINK", Payload(new Dictionary<string, object?>
        {
            ["arrangementDate"] = "2026-11-17T12:00:00+03:00",
            ["contentType"] = "application/pdf",
            ["orderNumber"] = "order-1"
        }), "hash");
        Assert.True(HepsiburadaInvoiceDeliveryPolicy.TryCreate(command, out var request, out var error), error);
        using var body = JsonDocument.Parse(HepsiburadaInvoiceDeliveryPolicy.Body(request!));
        Assert.Contains(ProviderUrl, body.RootElement.GetRawText().Replace("\\u0026", "&"));
        Assert.Equal(ProviderUrl, request!.InvoiceLink.AbsoluteUri);
    }

    [Fact]
    public void PttAvmSendsTheProviderUrlAsTheInvoiceLink()
    {
        var request = PttAvmInvoiceMapper.Request(Payload(new Dictionary<string, object?> { ["orderNumber"] = "PTT-1", ["lineItemIds"] = new[] { 101L, 102L } }));
        Assert.Null(request.Error);
        Assert.Equal(ProviderUrl, request.Link);
        Assert.Equal(ProviderUrl, request.StoredLink);
    }

    // Why n11 and Pazarama keep the panel link: their adapters take only links whose path ends in a document extension,
    // which E-Faturam's URL lacks; the panel link is sent as its .pdf alias.
    [Fact]
    public void NElevenAndPazaramaRefuseTheProviderUrlAndTakeThePanelLinkAsPdf()
    {
        var n11Provider = N11HttpClient.InvoiceLinkRequest(Payload(new Dictionary<string, object?> { ["orderNumber"] = "205412345678" }));
        Assert.NotNull(n11Provider.Error);
        var n11Panel = N11HttpClient.InvoiceLinkRequest(Payload(new Dictionary<string, object?> { ["orderNumber"] = "205412345678" }, PanelLink));
        Assert.Null(n11Panel.Error);
        Assert.Equal(InvoiceDocumentLink.PdfAlias(PanelLink), n11Panel.Link);

        var pazarama = new Dictionary<string, object?> { ["pazaramaOrderId"] = "0d145804-5d05-4e4a-a4a0-921e45c8a5e3", ["orderNumber"] = "PZ-1" };
        Assert.NotNull(PazaramaInvoiceMapper.Request(Payload(pazarama)).Error);
        var pazaramaPanel = PazaramaInvoiceMapper.Request(Payload(pazarama, PanelLink));
        Assert.Null(pazaramaPanel.Error);
        Assert.Equal(InvoiceDocumentLink.PdfAlias(PanelLink), pazaramaPanel.Link);
    }

    // Reconciliation: either of the invoice's own links (E-Faturam's URL for new deliveries, the panel link and its .pdf
    // alias for older ones) proves the delivery; another link does not, and a missing link is never "not invoiced".
    [Theory]
    [InlineData(ProviderUrl, true)]
    [InlineData(PanelLink, true)]
    [InlineData("https://panel.ravencia.com/api/v1/public/invoice-documents/tok-EN_1/invoice.pdf", true)]
    [InlineData("https://apigateway.trendyolecozum.com/api/invoice/documents/download/1", false)]
    public void EitherOwnLinkProvesTheDelivery(string shownLink, bool proven)
    {
        var own = InvoiceDeliveredLinks.Expand([ProviderUrl, PanelLink]);
        var shown = new InvoiceDeliveryStatus("pkg-1", "Invoiced", true, InvoiceNumber, shownLink, ReferencesAreAuthoritative: true);
        Assert.Equal(proven, InvoiceDeliveryProofPolicy.HasExactProof(shown, InvoiceNumber, own));
    }

    [Fact]
    public void NoOwnLinkFallsBackToTheNumberRuleAndProvesNothingByLink()
    {
        var numberOnly = new InvoiceDeliveryStatus("pkg-1", "Invoiced", true, InvoiceNumber, null);
        Assert.True(InvoiceDeliveryProofPolicy.HasExactProof(numberOnly, InvoiceNumber, Array.Empty<string>()));
        var authoritative = numberOnly with { VerifiedInvoiceLink = ProviderUrl, ReferencesAreAuthoritative = true };
        Assert.False(InvoiceDeliveryProofPolicy.HasExactProof(authoritative, InvoiceNumber, Array.Empty<string>()));
    }

    private static string Payload(Dictionary<string, object?>? extra = null, string link = ProviderUrl)
    {
        var values = new Dictionary<string, object?>
        {
            ["shipmentPackageId"] = "123456",
            ["invoiceLink"] = link,
            ["invoiceDateTime"] = IssuedAt,
            ["invoiceNumber"] = InvoiceNumber,
            ["micro"] = false
        };
        foreach (var (key, value) in extra ?? []) values[key] = value;
        return JsonSerializer.Serialize(values);
    }
}
