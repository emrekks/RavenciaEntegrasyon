using System.Globalization;
using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Persistence;

// What the report reads for the parcels' desi: the unit desi learned from the billed parcels and, by order, the desi
// the marketplace put on the order (Hepsiburada).
internal sealed record ReportDesiInput(ReportParcelDesi.Learned Learned, IReadOnlyDictionary<Guid, decimal> DeclaredByOrder)
{
    public static readonly ReportDesiInput None = new(ReportParcelDesi.Learned.None, new Dictionary<Guid, decimal>());
}

// The desi of an order's parcel for the platforms costed from their cargo tariffs (ReportPlatformCosts), and where it
// came from. In order of trust:
//   MARKETPLACE: the desi the marketplace put on the order itself (Hepsiburada's totalDeci, which it bills the seller by);
//   MANUAL: the desi entered on the product (product editor or the products import);
//   MEASURED: the one-unit desi learned from what Trendyol's carriers measured and billed (Learn);
//   DEFAULT: the store-wide measured mean for a product with no measurement of its own, or, with none at all, the old
//   rule of a one-desi parcel.
// A parcel's desi is its units' desi added up, one parcel for the order: two units of 0.3 desi go as one parcel of
// 1 desi, not as 1 + 1. The tariff takes the next whole desi of a declared or entered desi (ReportPlatformCosts.Desi);
// a learned one is rounded to the nearest whole desi first (ForOrder).
internal static class ReportParcelDesi
{
    public const string Marketplace = "MARKETPLACE";
    public const string Manual = "MANUAL";
    public const string Measured = "MEASURED";
    public const string Default = "DEFAULT";

    // A product's own mean needs this many parcels, a category's and the store-wide one more, so one odd parcel does not
    // set a desi.
    internal const int ProductSamples = 3;
    internal const int CategorySamples = 5;
    internal const int OverallSamples = 10;

    // One billed shipping parcel: the desi the carrier billed (a whole number; 0 is the lowest band, not a missing
    // value) and the order lines in it.
    public sealed record Parcel(decimal Desi, IReadOnlyList<ParcelLine> Lines);
    public sealed record ParcelLine(Guid? ProductId, Guid? CategoryId, decimal Units);

    // The learned one-unit desi by product and by category, the store-wide mean and how many parcels were read.
    public sealed record Learned(IReadOnlyDictionary<Guid, decimal> ByProduct, IReadOnlyDictionary<Guid, decimal> ByCategory, decimal? Overall, int Parcels)
    {
        public static readonly Learned None = new(new Dictionary<Guid, decimal>(), new Dictionary<Guid, decimal>(), null, 0);

        public decimal? For(Guid? productId, Guid? categoryId) =>
            productId is Guid product && ByProduct.TryGetValue(product, out var byProduct) ? byProduct
            : categoryId is Guid category && ByCategory.TryGetValue(category, out var byCategory) ? byCategory
            : null;
    }

    // Each parcel's billed desi spread evenly over its units gives one sample of a unit's desi for every product (and
    // category) in it. The mean, not the median: the billed desi is a whole number, and a product that goes as 0 desi
    // seven times in ten and as 1 three times is about 0.3 desi a unit, which a median would round to 0.
    public static Learned Learn(IEnumerable<Parcel> parcels)
    {
        var byProduct = new Dictionary<Guid, List<decimal>>();
        var byCategory = new Dictionary<Guid, List<decimal>>();
        var overall = new List<decimal>();
        foreach (var parcel in parcels)
        {
            var units = parcel.Lines.Sum(line => Math.Max(0m, line.Units));
            if (units <= 0m || parcel.Desi < 0m) continue;
            var perUnit = parcel.Desi / units;
            overall.Add(perUnit);
            foreach (var product in parcel.Lines.Select(line => line.ProductId).OfType<Guid>().Distinct()) Add(byProduct, product, perUnit);
            foreach (var category in parcel.Lines.Select(line => line.CategoryId).OfType<Guid>().Distinct()) Add(byCategory, category, perUnit);
        }
        return new Learned(Means(byProduct, ProductSamples), Means(byCategory, CategorySamples), overall.Count >= OverallSamples ? Mean(overall) : null, overall.Count);

        static void Add(Dictionary<Guid, List<decimal>> samples, Guid key, decimal value)
        {
            if (!samples.TryGetValue(key, out var list)) samples[key] = list = [];
            list.Add(value);
        }
        static Dictionary<Guid, decimal> Means(Dictionary<Guid, List<decimal>> samples, int minimum) =>
            samples.Where(pair => pair.Value.Count >= minimum).ToDictionary(pair => pair.Key, pair => Mean(pair.Value));
        static decimal Mean(List<decimal> values) => Math.Round(values.Average(), 2, MidpointRounding.AwayFromZero);
    }

    // An order's parcel desi (null: none known, the tariff's one-desi parcel) and its source. declared is the
    // marketplace's own desi of the order; lines are the order's lines with their entered desi and units.
    public static (decimal? Desi, string Source) ForOrder(decimal? declared, IEnumerable<(decimal? EnteredDesi, Guid? ProductId, Guid? CategoryId, decimal Units)> lines, Learned learned)
    {
        if (declared is > 0m) return (declared, Marketplace);
        var picked = lines.Select(Pick).ToList();
        if (picked.Count == 0 || picked.All(line => line.Desi is null)) return (null, Default);
        var desi = picked.Sum(line => (line.Desi ?? ReportPlatformCosts.DefaultDesi) * line.Units);
        var source = picked.Any(line => line.Source == Default) ? Default : picked.Any(line => line.Source == Measured) ? Measured : Manual;
        // A learned desi is a mean of billed whole desi, so the parcel takes the nearest whole desi (at least 1) rather
        // than the tariff's next one: against 1,325 billed Trendyol parcels (Oct 2026) the nearest band was right for
        // 98.2 %, the next band for 94.9 % and the old one-desi parcel for 97.4 % (36 % of the parcels of four units).
        if (picked.Any(line => line.Source != Manual && line.Desi is not null))
            desi = Math.Max(1m, Math.Round(desi, 0, MidpointRounding.AwayFromZero));
        return (desi, source);

        (decimal? Desi, decimal Units, string Source) Pick((decimal? EnteredDesi, Guid? ProductId, Guid? CategoryId, decimal Units) line)
        {
            var units = Math.Max(1m, line.Units);
            if (line.EnteredDesi is > 0m) return (line.EnteredDesi.Value, units, Manual);
            if (learned.For(line.ProductId, line.CategoryId) is { } measured) return (measured, units, Measured);
            return (learned.Overall, units, Default);
        }
    }

    // Hepsiburada's desi of one order line, from the line as it was read: totalDeci (the line's units together), else
    // deci times the quantity. Null when the line carries neither.
    public static decimal? HepsiburadaLineDesi(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot) || snapshot[0] != '{') return null;
        try
        {
            using var document = JsonDocument.Parse(snapshot);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Number(root, "totalDeci") is decimal total && total > 0m) return total;
            if (Number(root, "deci") is decimal deci && deci > 0m) return deci * (Number(root, "quantity") is decimal quantity && quantity > 0m ? quantity : 1m);
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        static decimal? Number(JsonElement root, string name) =>
            !root.TryGetProperty(name, out var value) ? null
            : value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number
            : value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var text) ? text
            : null;
    }
}
