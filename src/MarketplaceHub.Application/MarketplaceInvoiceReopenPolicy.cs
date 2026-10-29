namespace MarketplaceHub.Application;

/// <summary>
/// n11, Pazarama and PTT AVM cannot tell which invoice a package holds and offer no way to remove a link, so the
/// invoice state of a package whose invoice was cancelled is set back by hand: "Fatura Bekliyor" reopens the package
/// for a new invoice. Only the evidence a cancelled invoice of this panel left (its delivered link) is set aside; an
/// invoice the marketplace shows from elsewhere is never overridden.
///
/// Whether the marketplace takes a second link for the same order (repo research, 29 Oct 2026, no live call):
/// - n11: yes. SellerInvoiceService/SaveLinkSellerInvoice keeps one active link per order and a new link replaces the
///   previous one (docs/N11-ENTEGRASYON-PLANI.md "Fatura linki"; N11Invoices.cs). The new invoice's link is sent as usual.
/// - Pazarama: yes. POST order/invoice-link with no carrier or tracking number attaches the link to the whole order and
///   a new link for the order replaces the earlier one (PazaramaInvoices.cs); there is no delete. Sent as usual.
/// - PTT AVM: unknown. SOAP SaveInvoince is the only invoice call and its read-back is a per-line IsInvoice flag that
///   stays true after the first link, so neither a refusal nor an overwrite of a second link is documented or provable
///   (docs/PTTAVM-ENTEGRASYON-PLANI.md phase 6). The new invoice is issued, but its link is put in place by the seller in
///   the PTT AVM seller panel and then confirmed with "Pazaryerinde Gördüm, Tamamla".
/// </summary>
public static class MarketplaceInvoiceReopenPolicy
{
    // The delivery state of the cancelled invoice ends as LINK_REMOVED with this code: the seller reopened the package.
    public const string ReopenedCode = "OPERATOR_REOPENED_FOR_NEW_INVOICE";
    public const string PttAvmManualReplacementCode = "PTTAVM_INVOICE_LINK_REPLACE_IN_SELLER_PANEL";
    public const string PttAvmManualReplacementMessage =
        "PTT AVM'de siparişin satırları önceki (iptal edilen) faturayla işaretli ve PTT AVM ikinci fatura linkini kabul edip etmediğini bildirmiyor; link API ile gönderilmedi. Yeni faturanın linkini PTT AVM satıcı panelinde eskisinin yerine yükleyin, sonra “Pazaryerinde Gördüm, Tamamla” ile kapatın.";

    public static bool AppliesTo(string? platformCode) => platformCode?.Trim().ToUpperInvariant() is "N11" or "PAZARAMA" or "PTTAVM";

    // The marketplace replaces the order's link when a new one is sent (n11, Pazarama).
    public static bool ApiReplacesLink(string? platformCode) => platformCode?.Trim().ToUpperInvariant() is "N11" or "PAZARAMA";

    // A package the seller reopened ("Fatura Bekliyor" over a cancelled invoice of this panel) no longer counts as holding
    // an invoice, whatever a later order read brings back about the cancelled one's link.
    public static bool OverridesInvoiceEvidence(string? manualInvoiceStatus, bool reopened) =>
        reopened && string.Equals(manualInvoiceStatus?.Trim(), "PENDING", StringComparison.OrdinalIgnoreCase);
}
