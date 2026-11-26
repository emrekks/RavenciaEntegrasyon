using System.Globalization;
using System.Text.RegularExpressions;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Owner decision of 27 Nov 2026, the one exception to the rule of 18 Nov 2026 ("the price always goes with the stock"):
// when Trendyol refuses the PRICE of a price-and-inventory line, the line's stock was not written either, and every later
// send carried the same refused line again, so the stock on Trendyol stood still (overselling). Such a row now goes with
// its stock alone on the next send that happens anyway (a stock change, the reconcile scan, a send from the panel); this
// class queues nothing and never opens a write switch.
//
// The mark is the open operational issue of the row (key per connection and variant, no migration): its summary tells the
// owner the price was refused and keeps a fingerprint of what was refused (the code, the price mark and the two prices).
// While the price to send has that fingerprint the row goes stock-only; a new price (panel price, Özel Fiyat, a new code)
// goes with the stock again. A line Trendyol accepts with a price resolves the issue, and so does the daily orphan pass
// once the variant, its offer, its codes or the connection are gone (OrphanedOperationalIssues). A send from the panel for
// that one variant still carries the price (as a twice-refused code is tried again only there).
internal static partial class TrendyolPriceRefusal
{
    public const string Code = "TRENDYOL_PRICE_REFUSED";
    public const string KeyPrefix = "trendyol-price-refused:";
    public const string MarkStart = "[reddedilen fiyat: ";

    // The send log's note on a stock-only line (PriceInventoryPushLine.PriceNote).
    public const string StockOnlyNote = "Trendyol bu fiyatı daha önce reddetti; stok fiyatsız gönderildi. Fiyat, değişene kadar gönderilmeyecek.";

    public static string Key(Guid connectionId, Guid variantId) => $"{KeyPrefix}{connectionId:N}:{variantId:N}";

    public static string ConnectionPrefix(Guid connectionId) => $"{KeyPrefix}{connectionId:N}:";

    // What was refused: the code the line went to, the offer's price mark and the two prices it carried.
    public static string Fingerprint(string barcode, string priceHash, decimal salePrice, decimal listPrice) =>
        TextValues.Sha256Hex($"{barcode.Trim()}|{priceHash}|{Price(salePrice)}|{Price(listPrice)}")[..16];

    public static string Summary(string barcode, decimal salePrice, decimal listPrice, string reason, string fingerprint) =>
        $"Trendyol {barcode} için fiyatı reddetti (satış {Price(salePrice)} TL, liste {Price(listPrice)} TL): {Cut(reason, 300).TrimEnd('.', ' ')}. "
        + $"Stok fiyatsız gönderilmeye devam ediyor; fiyat, panel fiyatı veya Özel Fiyat değişene kadar gönderilmeyecek. {MarkStart}{fingerprint}]";

    public static string? FingerprintOf(string summary)
    {
        var start = summary.LastIndexOf(MarkStart, StringComparison.Ordinal);
        if (start < 0) return null;
        start += MarkStart.Length;
        var end = summary.IndexOf(']', start);
        return end > start ? summary[start..end] : null;
    }

    // The open marks of one connection by variant. An issue the owner resolved by hand is no mark: the price is tried again.
    public static async Task<Dictionary<Guid, string>> OpenMarksAsync(AppDbContext db, Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var prefix = ConnectionPrefix(connectionId);
        var open = await db.OperationalIssues.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Code == Code && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(prefix))
            .Select(x => new { x.DedupeKey, x.Summary })
            .ToListAsync(cancellationToken);
        var marks = new Dictionary<Guid, string>();
        foreach (var issue in open)
            if (Guid.TryParseExact(issue.DedupeKey[prefix.Length..], "N", out var variantId) && FingerprintOf(issue.Summary) is { } fingerprint)
                marks[variantId] = fingerprint;
        return marks;
    }

    // Whether Trendyol's failure reasons for a line are clearly about its price alone. Neither the offline documents nor the
    // live answers name the texts Trendyol writes for a refused price (the price-and-inventory batch sample carries an empty
    // failureReasons; live, 17 sends to 18 Nov 2026, none refused), so the test is strict: every reason names the price
    // (fiyat, salePrice, listPrice, price, PSF, TSF) and none names anything else the line or the request could have been
    // refused for: the stock or quantity, the barcode or product, its approval, archive, lock or sale state, the request
    // itself (the 30 price requests a minute per barcode, the 15-minute repeat), an authorisation. Anything else, or no
    // reason at all, is not a price refusal and keeps today's behaviour (unknown stays unknown).
    public static bool IsPriceRefusal(IReadOnlyList<string>? reasons)
    {
        if (reasons is null || reasons.Count == 0) return false;
        foreach (var reason in reasons)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            if (!PriceTerm().IsMatch(reason)) return false;
            if (OtherSubjects.Any(term => Contains(reason, term))) return false;
        }
        return true;
    }

    private static readonly string[] OtherSubjects =
    [
        "stok", "stock", "quantity", "miktar", "adet", "envanter", "inventory",
        "barkod", "barcode", "ürün bulunamad", "bulunamad", "not found", "kayıtlı değil", "mevcut değil", "tanımsız", "tanımlı değil",
        "onay", "approv", "arşiv", "archiv", "kilit", "lock", "blacklist", "kara liste", "satışa kapalı", "satisa kapali", "silin", "delete",
        "dakika", "minute", "istek", "request", "tekrar", "repeat", "limit aşım", "too many", "rate",
        "yetki", "auth", "token", "erişim", "access", "sistem hatası", "system error", "internal", "timeout", "zaman aşımı"
    ];

    // Case- and accent-insensitive in the invariant culture, so "STOK", "Stok" and "Arşiv"/"Arsiv" match alike.
    private static bool Contains(string text, string term) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    [GeneratedRegex(@"fiyat|sale\s*price|list\s*price|\bprice\b|\bpsf\b|\btsf\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PriceTerm();

    private static string Price(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Cut(string value, int maximum)
    {
        var text = value.Trim();
        return text.Length <= maximum ? text : text[..maximum];
    }
}
