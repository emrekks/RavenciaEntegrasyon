using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters;

// One rule for every marketplace (owner's decision, 5 Nov 2026): a product line the marketplace lists but the panel
// cannot read (no line id, quantity, price, SKU or name) does not stop the order. The package is imported for shipping
// without that line and counts it (RemotePackage.UnreadableLineCount); the line is never guessed. The order shows
// "Okunamayan Ürün Satırı", no sale invoice is drafted, approved or issued for it (InvoiceIssuePolicy.OrderLineUnreadable,
// also at the last check before issuing), and the order is read again by its number until a read fills the line in
// (RejectedOrderRereadPolicy). A read with every line clears the count and resolves this issue.
internal static class UnreadableOrderLines
{
    public const string IssueCode = "ORDER_PACKAGE_LINE_INVALID";

    // The order amount comes from the marketplace's own total where it sends one (Trendyol package total, n11 package
    // totalAmount, Pazarama orderAmount, Hepsiburada's order total, Shopify's order total). Where it sends none (PTT AVM
    // always; n11 or Pazarama when the field is missing) the amount of an order with an unread line is the read lines'
    // sum and may be short: this issue says so, keyed by the package like the line issues, and is resolved with them.
    public const string AmountIssueCode = "ORDER_AMOUNT_MAY_BE_INCOMPLETE";

    // The issue of one unreadable line; its identity starts with the package id, so the read that clears the package
    // finds it again (MarketplaceJobProcessor.ApplyUnreadableLines).
    public static AdapterPageIssue Issue(string marketplace, string externalPackageId, string? externalLineId, string reason) => new(
        IssueCode,
        string.IsNullOrWhiteSpace(externalLineId) ? externalPackageId : $"{externalPackageId}:{externalLineId}",
        $"{marketplace} sipariş paketi {externalPackageId} kargo için alındı ancak bir ürün satırı{(string.IsNullOrWhiteSpace(externalLineId) ? "" : $" ({externalLineId})")} okunamadı: {reason} Satır bilinmiyor; bu siparişe fatura kesilmez ve sipariş yeniden okunacak.");

    // The order's issue for a package stored with unreadable lines, which the order read raises for every marketplace
    // (the adapters' line issues above say which line and why, where the marketplace names it).
    public static string PackageIssueKey(Guid connectionId, string externalPackageId) => $"unreadable-lines:{connectionId}:{externalPackageId}";

    public static string PackageIssueSummary(string? platformCode, string orderNumber, string externalPackageId, int count) =>
        $"{MarketplaceName(platformCode)} siparişi {orderNumber}, paket {externalPackageId}: {count} ürün satırı okunamadı (Okunamayan Ürün Satırı). Paket kargo için alındı, satır bilinmiyor; satır gelene kadar siparişe fatura kesilmez ve sipariş yeniden okunuyor.";

    public static string MarketplaceName(string? platformCode) => platformCode switch
    {
        "TRENDYOL" => "Trendyol",
        "HEPSIBURADA" => "Hepsiburada",
        "N11" => "N11",
        "PAZARAMA" => "Pazarama",
        "PTTAVM" => "PTT AVM",
        "SHOPIFY" => "Shopify",
        _ => "Pazaryeri"
    };
}
