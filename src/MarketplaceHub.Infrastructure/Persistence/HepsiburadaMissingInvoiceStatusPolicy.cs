namespace MarketplaceHub.Infrastructure.Persistence;

internal static class HepsiburadaMissingInvoiceStatusPolicy
{
    private static readonly TimeSpan FeedLookback = TimeSpan.FromDays(30);

    // The list is read this many times per reconciliation; a package counts as listed when any pass saw it.
    public const int Passes = 2;

    // Every page of one pass must give the total its first page gave; a page without a total cannot prove that and
    // counts as moved once a total was seen (or the other way round).
    public static bool SameTotal(ref int? firstTotal, int? pageTotal, int pageIndex)
    {
        if (pageIndex == 0)
        {
            firstTotal = pageTotal;
            return true;
        }
        return firstTotal == pageTotal;
    }

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
