using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// One variant of the catalogue with its MAIN stock: its product's category and VAT rate, its colour and size (the RENK
// and BEDEN options), its current cost and its panel sale price as the product editor shows it (both VAT included, as the
// panel stores them; null or 0 = not entered).
internal sealed record ReportStockVariantRow(Guid VariantId, Guid ProductId, string Title, string? ModelCode, string? Category, string? Color, string? Size,
    string OptionSignature, bool Active, decimal? CostPrice, decimal? VatRate, decimal OnHand, decimal Available, DateTimeOffset CreatedAt, decimal? PanelPrice = null);

// One offer of a variant on a store, with its sale price (VAT included).
internal sealed record ReportStockOfferRow(Guid VariantId, Guid ConnectionId, decimal SalePrice, string Currency, string Status);

// Stok raporu's figures from the catalogue's variants, their offers and the units kept per variant (ReportBuilder's
// products, so the sold units match the sales report). Every amount is worked out over the variants it is known for and
// the rest is counted apart (missing cost, missing price); nothing is guessed.
internal static class ReportStock
{
    public const int DeadDays = 90;
    private const int TopCount = 20;
    private const int DeadCount = 50;
    private const string Unknown = "Bilinmiyor";
    private const string Unset = "Belirtilmemiş";
    private const string NoPrice = "Fiyatı Yok";

    // The price bands (TL, VAT included): a band holds its lower bound and runs up to, not including, the next one.
    private static readonly decimal[] BandBounds = [500m, 750m, 1000m, 1500m, 2000m];

    private sealed record Item(ReportStockVariantRow Row, decimal Units, decimal? Cost, decimal? Price, decimal Sold, decimal Recent, decimal? Cover, bool Dead)
    {
        public decimal? Capital => Cost is { } cost ? Units * cost : null;
        public decimal? Value => Price is { } price ? Units * price : null;
    }

    public static ReportInventoryView Build(IReadOnlyList<ReportStockVariantRow> variants, IReadOnlyList<ReportStockOfferRow> offers,
        IReadOnlyDictionary<Guid, ReportChannelView> stores, bool filtered, IReadOnlyDictionary<Guid, decimal> periodSold, IReadOnlyDictionary<Guid, decimal> recentSold,
        DateOnly from, DateOnly to, DateOnly today)
    {
        var storeOffers = offers.Where(x => stores.ContainsKey(x.ConnectionId)).ToList();
        // With platforms picked, the variants on sale there: those with an active offer on one of their stores.
        var onSale = storeOffers.Where(IsActive).Select(x => x.VariantId).ToHashSet();
        var counted = filtered ? variants.Where(row => onSale.Contains(row.VariantId)).ToList() : variants.ToList();
        var countedIds = counted.Select(x => x.VariantId).ToHashSet();
        var offersByStore = storeOffers.Where(x => countedIds.Contains(x.VariantId)).GroupBy(x => x.ConnectionId).ToDictionary(x => x.Key, x => x.ToList());
        var stocked = counted.Where(x => x.OnHand > 0).ToList();
        var stockedUnits = stocked.Sum(x => x.OnHand);

        var periodDays = to.DayNumber - from.DayNumber + 1;
        var deadBefore = new DateTimeOffset(today.AddDays(-DeadDays).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        decimal? Cover(decimal units, decimal sold) => sold > 0 && periodDays > 0 ? Math.Round(units / (sold / periodDays), 1) : null;
        var items = counted.Select(row =>
        {
            var units = Math.Max(0m, row.OnHand);
            var sold = periodSold.GetValueOrDefault(row.VariantId);
            var recent = recentSold.GetValueOrDefault(row.VariantId);
            return new Item(row, units, row.CostPrice is > 0 ? row.CostPrice : null, row.PanelPrice is > 0 ? row.PanelPrice : null,
                sold, recent, units > 0 ? Cover(units, sold) : null, units > 0 && recent <= 0 && row.CreatedAt < deadBefore);
        }).ToList();
        var inStock = items.Where(x => x.Units > 0).ToList();

        return new ReportInventoryView(default, from, to, periodDays, DeadDays, Summary(items, inStock, periodDays), Platforms(stores, offersByStore, stocked, stockedUnits),
            Groups(inStock, x => x.Row.Category, Unknown, Cover),
            Groups(inStock, x => x.Row.Color, Unset, Cover),
            Groups(inStock, x => x.Row.Size, Unset, Cover),
            Bands(inStock, Cover),
            CoverBands(inStock),
            TopProducts(inStock, Cover),
            inStock.Where(x => x.Dead).OrderByDescending(x => x.Capital ?? -1m).ThenByDescending(x => x.Units).ThenBy(x => x.Row.Title, StringComparer.Ordinal)
                .Take(DeadCount).Select(VariantItem).ToList(),
            []);
    }

    private static bool IsActive(ReportStockOfferRow offer) => string.Equals(offer.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    private static bool Priced(ReportStockOfferRow offer) =>
        IsActive(offer) && offer.SalePrice > 0 && string.Equals(offer.Currency?.Trim(), "TRY", StringComparison.OrdinalIgnoreCase);

    private static ReportStockSummaryView Summary(IReadOnlyList<Item> items, IReadOnlyList<Item> inStock, int periodDays)
    {
        var active = items.Where(x => x.Row.Active).ToList();
        var costed = inStock.Where(x => x.Cost is not null).ToList();
        var priced = inStock.Where(x => x.Price is not null).ToList();
        var both = inStock.Where(x => x.Cost is not null && x.Price is not null).ToList();
        var bothValue = both.Sum(x => x.Value!.Value);
        var bothCapital = both.Sum(x => x.Capital!.Value);
        var profit = both.Count == 0 ? (decimal?)null : bothValue - bothCapital;
        // Ex-VAT only when every costed variant's VAT rate is known.
        var capitalExVat = costed.All(x => x.Row.VatRate is not null)
            ? Round(costed.Sum(x => x.Capital!.Value - ReportCosts.Vat(x.Capital!.Value, x.Row.VatRate!.Value)))
            : (decimal?)null;
        var units = inStock.Sum(x => x.Units);
        var sold = items.Sum(x => x.Sold);
        var daily = periodDays > 0 && sold > 0 ? Math.Round(sold / periodDays, 2) : (decimal?)null;
        var products = items.GroupBy(x => x.Row.ProductId).ToList();
        var low = products.Count(group => group.Any(x => x.Units > 0) && ProductStockPolicy.IsLowStock(
            group.Select(x => new ProductVariant { Id = x.Row.VariantId, ProductId = x.Row.ProductId, Sku = "", SkuNormalized = "", OptionSignature = x.Row.OptionSignature }),
            group.ToDictionary(x => x.Row.VariantId, x => x.Units)));
        var dead = inStock.Where(x => x.Dead).ToList();
        return new(units, inStock.Sum(x => Math.Max(0m, x.Row.Available)), products.Count, inStock.Select(x => x.Row.ProductId).Distinct().Count(),
            active.Count, active.Count(x => x.Units > 0), active.Count(x => x.Units <= 0), low,
            Round(costed.Sum(x => x.Capital!.Value)), capitalExVat, inStock.Count(x => x.Cost is null), inStock.Where(x => x.Cost is null).Sum(x => x.Units),
            priced.Count == 0 ? null : Round(priced.Sum(x => x.Value!.Value)), priced.Sum(x => x.Units), inStock.Where(x => x.Price is null).Sum(x => x.Units),
            profit is { } p ? Round(p) : null, profit is { } m && bothValue > 0 ? Math.Round(m / bothValue, 4) : null,
            profit is { } c && bothCapital > 0 ? Math.Round(c / bothCapital, 4) : null,
            sold, daily, daily is > 0 ? Math.Round(units / daily.Value, 1) : null,
            dead.Count, dead.Sum(x => x.Units), Round(dead.Sum(x => x.Capital ?? 0m)));
    }

    private static List<ReportStockPlatformView> Platforms(IReadOnlyDictionary<Guid, ReportChannelView> stores, Dictionary<Guid, List<ReportStockOfferRow>> offersByStore,
        IReadOnlyList<ReportStockVariantRow> stocked, decimal stockedUnits) =>
        offersByStore.Select(pair =>
            {
                var store = stores[pair.Key];
                var active = pair.Value.Where(IsActive).GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First());
                var listed = stocked.Where(x => active.ContainsKey(x.VariantId)).ToList();
                var priced = listed.Where(x => Priced(active[x.VariantId])).ToList();
                var value = priced.Sum(x => x.OnHand * active[x.VariantId].SalePrice);
                var pricedUnits = priced.Sum(x => x.OnHand);
                var listedUnits = listed.Sum(x => x.OnHand);
                return new ReportStockPlatformView(store.Id, store.PlatformCode, store.DisplayName, store.IsTest, pair.Value.Count, active.Count, listedUnits,
                    stockedUnits - listedUnits,
                    priced.Count == 0 ? null : Round(value), pricedUnits, pricedUnits > 0 ? Round(value / pricedUnits) : null);
            })
            .OrderByDescending(x => x.ListedUnits).ThenBy(x => x.PlatformCode, StringComparer.Ordinal).ThenBy(x => x.StoreName, StringComparer.Ordinal)
            .ToList();

    private static List<ReportStockGroupView> Groups(IReadOnlyList<Item> inStock, Func<Item, string?> name, string missing, Func<decimal, decimal, decimal?> cover) =>
        inStock.GroupBy(x => string.IsNullOrWhiteSpace(name(x)) ? missing : name(x)!.Trim(), StringComparer.Ordinal)
            .Select(group => Group(group.Key, group.Key, group.Key != missing, group.ToList(), cover))
            .OrderBy(x => x.Known ? 0 : 1).ThenByDescending(x => x.Units).ThenBy(x => x.Label, StringComparer.Ordinal)
            .ToList();

    private static ReportStockGroupView Group(string key, string label, bool known, IReadOnlyList<Item> rows, Func<decimal, decimal, decimal?> cover)
    {
        var priced = rows.Where(x => x.Price is not null).ToList();
        var units = rows.Sum(x => x.Units);
        var sold = rows.Sum(x => x.Sold);
        return new(key, label, known, rows.Select(x => x.Row.ProductId).Distinct().Count(), rows.Count, units, Round(rows.Sum(x => x.Capital ?? 0m)),
            priced.Count == 0 ? null : Round(priced.Sum(x => x.Value!.Value)), priced.Sum(x => x.Units), sold, cover(units, sold));
    }

    // The price bands in order, each shown only when it holds stock; the variants with no price last.
    private static List<ReportStockGroupView> Bands(IReadOnlyList<Item> inStock, Func<decimal, decimal, decimal?> cover)
    {
        static int Band(decimal price) { var index = 0; while (index < BandBounds.Length && price >= BandBounds[index]) index++; return index; }
        static string Tl(decimal amount) => amount.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
        static string Label(int band) => band == 0 ? $"{Tl(BandBounds[0])} TL altı"
            : band == BandBounds.Length ? $"{Tl(BandBounds[^1])} TL ve üstü"
            : $"{Tl(BandBounds[band - 1])} – {Tl(BandBounds[band])} TL";
        var bands = inStock.Where(x => x.Price is not null).GroupBy(x => Band(x.Price!.Value)).OrderBy(x => x.Key)
            .Select(group => Group($"band-{group.Key}", Label(group.Key), true, group.ToList(), cover)).ToList();
        var none = inStock.Where(x => x.Price is null).ToList();
        if (none.Count > 0) bands.Add(Group("none", NoPrice, false, none, cover));
        return bands;
    }

    // The stocked variants by the days their stock lasts at the period's pace; the ones not sold in the period last.
    private static List<ReportStockGroupView> CoverBands(IReadOnlyList<Item> inStock)
    {
        (string Key, string Label, Func<decimal, bool> Holds)[] bands =
        [
            ("0-30", "30 güne kadar", days => days <= 30m),
            ("31-90", "31 – 90 gün", days => days > 30m && days <= 90m),
            ("91-180", "91 – 180 gün", days => days > 90m && days <= 180m),
            ("181+", "180 günden fazla", days => days > 180m)
        ];
        var rows = bands.Select(band => (band, Items: inStock.Where(x => x.Cover is { } days && band.Holds(days)).ToList()))
            .Where(x => x.Items.Count > 0)
            .Select(x => Group(x.band.Key, x.band.Label, true, x.Items, (_, _) => null)).ToList();
        var unsold = inStock.Where(x => x.Cover is null).ToList();
        if (unsold.Count > 0) rows.Add(Group("unsold", "Dönemde satış yok", false, unsold, (_, _) => null));
        return rows;
    }

    // The products binding the most capital: their stocked variants together.
    private static List<ReportStockItemView> TopProducts(IReadOnlyList<Item> inStock, Func<decimal, decimal, decimal?> cover) =>
        inStock.GroupBy(x => x.Row.ProductId)
            .Select(group =>
            {
                var first = group.First().Row;
                var units = group.Sum(x => x.Units);
                var sold = group.Sum(x => x.Sold);
                var costed = group.Where(x => x.Capital is not null).ToList();
                var priced = group.Where(x => x.Value is not null).ToList();
                return new ReportStockItemView(first.ProductId.ToString("N"), first.ProductId, null, first.Title, null, first.ModelCode,
                    string.IsNullOrWhiteSpace(first.Category) ? Unknown : first.Category, null, units,
                    costed.Count == 0 ? null : Round(costed.Sum(x => x.Capital!.Value)), priced.Count == 0 ? null : Round(priced.Sum(x => x.Value!.Value)),
                    sold, group.Sum(x => x.Recent), cover(units, sold));
            })
            .OrderByDescending(x => x.Capital ?? -1m).ThenByDescending(x => x.Units).ThenBy(x => x.Title, StringComparer.Ordinal)
            .Take(TopCount)
            .ToList();

    private static ReportStockItemView VariantItem(Item item)
    {
        var row = item.Row;
        var options = string.Join(" / ", new[] { row.Color, row.Size }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new(row.VariantId.ToString("N"), row.ProductId, row.VariantId, row.Title, options.Length == 0 ? null : options, row.ModelCode,
            string.IsNullOrWhiteSpace(row.Category) ? Unknown : row.Category, null, item.Units, item.Capital is { } capital ? Round(capital) : null,
            item.Value is { } value ? Round(value) : null, item.Sold, item.Recent, item.Cover);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2);
}
