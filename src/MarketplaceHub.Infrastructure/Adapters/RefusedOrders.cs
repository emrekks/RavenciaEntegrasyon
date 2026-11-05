using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters;

// One rule for every marketplace (5 Nov 2026): an order record the mapper cannot take (no order date of its own, a
// currency the panel does not know, a malformed package) is skipped with an issue, never imported with a guessed value.
// When its order number is readable the issue names it (AdapterPageIssue.RereadOrderId): the order joins the re-read
// list (RejectedOrderRereadPolicy), and a read by that number answers with the same refusal as a contract error, so it
// is not taken for "not found". A record without a readable number keeps only its issue.
internal static class RefusedOrders
{
    public const string IssueCode = "ORDER_PACKAGE_INVALID";

    public static AdapterPageIssue Issue(string identity, string message, string? orderNumber) =>
        new(IssueCode, identity, message, string.IsNullOrWhiteSpace(orderNumber) ? null : orderNumber.Trim());

    // The refusal of the order a numbered read asked for, if the page refused it; null when the page did not name it.
    public static AdapterError? ReadError(IEnumerable<AdapterPageIssue>? issues, string orderNumber) =>
        issues?.FirstOrDefault(issue => string.Equals(issue.RereadOrderId, orderNumber.Trim(), StringComparison.OrdinalIgnoreCase)) is { } refused
            ? new AdapterError(AdapterErrorClass.ContractViolation, refused.Code, refused.Message, null, null, null)
            : null;

    // The order's currency as an ISO code: Turkish lira also when the marketplace writes "TL" (Pazarama) or "YTL"; any
    // other three-letter code as sent. Null when there is none or it is not a currency code: the caller skips the order
    // with an issue instead of storing an unknown currency (Order.Currency is char(3) and never guessed).
    public static string? Currency(string? raw)
    {
        var value = raw?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(value)) return null;
        if (value is "TL" or "TRY" or "YTL" or "₺") return "TRY";
        return value.Length == 3 && value.All(character => character is >= 'A' and <= 'Z') ? value : null;
    }
}
