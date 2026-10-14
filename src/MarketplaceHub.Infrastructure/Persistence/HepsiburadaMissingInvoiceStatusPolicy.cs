namespace MarketplaceHub.Infrastructure.Persistence;

internal static class HepsiburadaMissingInvoiceStatusPolicy
{
    private static readonly TimeSpan FeedLookback = TimeSpan.FromDays(30);

    public static string Key(string orderNumber, string packageNumber) => $"{orderNumber.Trim()}\u001f{packageNumber.Trim()}";

    public static string? Resolve(
        string orderNumber,
        string packageNumber,
        DateTimeOffset orderDate,
        DateTimeOffset now,
        IReadOnlySet<string> missingInvoicePackageKeys,
        bool completeFeed)
    {
        if (!completeFeed) return null;
        if (missingInvoicePackageKeys.Contains(Key(orderNumber, packageNumber))) return "NOT_INVOICED";
        // Hepsiburada documents this feed as a complete missing-invoice list for the last month.
        // An absent package is positive invoice evidence only inside that documented window.
        return orderDate >= now - FeedLookback ? "INVOICED" : null;
    }
}
