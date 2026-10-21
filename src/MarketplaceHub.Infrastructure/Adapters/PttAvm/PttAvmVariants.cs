using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Variant reads. A store whose products are models (the product barcode is the model code) keeps the variants
// apart: StokKontrolListesi answers VariantListesi empty for them, and GetProductsWithVariants(page, size) lists
// every product barcode with its variants. The service schema (service.svc?singleWsdl) names the variant fields of
// StokKontrolListesi (Variants: VariantBarkod, Miktar, Fiyat, FiyatFarkiMi, Variant1Deger, Variant2Deger) and of the
// write-side Variant (VariantBarkod, Miktar, Attributes{Tanim, Deger}, Combination{Barkod, Degerler, Stok, Aktif});
// the element under GetProductsWithVariants is declared recursively there, so every one of those spellings is read.
// An entry nothing can be read from is reported by its element names (never its values).
public sealed partial class PttAvmHttpClient
{
    internal const string ProductsWithVariants = "GetProductsWithVariants";
    internal const int VariantPageSize = 100;
    internal const int MaxVariantPages = 100;
    private static readonly TimeSpan VariantListLifetime = TimeSpan.FromMinutes(15);
    // One scan reads the variant list once: page zero fills it, the later pages of the same scan reuse it.
    private static readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyDictionary<string, IReadOnlyList<XElement>> Variants)> VariantLists = new();

    internal async Task<AdapterResult<IReadOnlyDictionary<string, IReadOnlyList<XElement>>>> VariantListAsync(PttAvmRequestContext account, bool refresh, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!refresh && VariantLists.TryGetValue(account.Connection.Id, out var cached) && now - cached.At < VariantListLifetime)
            return AdapterResult<IReadOnlyDictionary<string, IReadOnlyList<XElement>>>.Success(cached.Variants);
        var variants = new Dictionary<string, IReadOnlyList<XElement>>(StringComparer.OrdinalIgnoreCase);
        RateLimitMetadata? rate = null;
        // Pages start at 1 (PTT AVM answers page 0 with "Page 0.dan büyük olmalıdır") and are read until one brings no new product.
        for (var page = 1; page <= MaxVariantPages; page++)
        {
            var result = await SendSoapAsync(account, ProductsWithVariants, [new("page", page.ToString(CultureInfo.InvariantCulture)), new("size", VariantPageSize.ToString(CultureInfo.InvariantCulture))], cancellationToken);
            if (!result.IsSuccess) return AdapterResult<IReadOnlyDictionary<string, IReadOnlyList<XElement>>>.Failure(result.Error!, result.RateLimit);
            rate = result.RateLimit ?? rate;
            var added = 0;
            foreach (var (productBarcode, entries) in PttAvmVariantMapper.ProductsWithVariants(result.Value!))
                if (variants.TryAdd(productBarcode, entries)) added++;
            if (added == 0) break;
        }
        if (VariantLists.Count > 50) VariantLists.Clear();
        VariantLists[account.Connection.Id] = (now, variants);
        // The element names and one variant name (catalog text, e.g. "Lacivert / L") show the live shape of the list.
        var sample = variants.Values.SelectMany(x => x).FirstOrDefault();
        logger.LogInformation("PTT AVM variant list read. ConnectionId: {ConnectionId}, Products: {Products}, Shape: {Shape}, SampleVariantName: {SampleVariantName}",
            account.Connection.Id, variants.Count, sample is null ? "none" : PttAvmVariantMapper.Shape(sample), sample is null ? null : ContractValue(sample, "VariantName"));
        return AdapterResult<IReadOnlyDictionary<string, IReadOnlyList<XElement>>>.Success(variants, rate);
    }
}

public static class PttAvmVariantMapper
{
    public sealed record VariantRead(string Barcode, decimal? Quantity, decimal? Price, bool PriceIsDifference, IReadOnlyDictionary<string, string> Options, bool? Active, XElement Source);

    // GetProductsWithVariantsResult {ProductsWithVariants{ProductWithVariants{ProductBarcode, Variants{...}}}, TotalPage}.
    public static IEnumerable<(string ProductBarcode, IReadOnlyList<XElement> Entries)> ProductsWithVariants(XElement result)
    {
        foreach (var product in result.Descendants().Where(x => x.Name.LocalName == "ProductWithVariants"))
        {
            var barcode = PttAvmHttpClient.ContractValue(product, "ProductBarcode");
            var container = product.Elements().FirstOrDefault(x => x.Name.LocalName == "Variants");
            if (barcode is null || container is null || PttAvmHttpClient.IsNil(container)) continue;
            var entries = container.Elements().ToList();
            if (entries.Count > 0) yield return (barcode, entries);
        }
    }

    // The variants an entry stands for: its combinations when it carries them (each with its own barcode), else itself.
    public static IReadOnlyList<VariantRead> Read(XElement entry)
    {
        var reads = new List<VariantRead>();
        var combinations = entry.Descendants().Where(x => x.Name.LocalName is "VariantCombination" || x.Parent?.Name.LocalName == "Combination").ToList();
        foreach (var combination in combinations)
            if (Value(combination, "Barkod", "VariantBarkod", "Barcode") is { } barcode)
                reads.Add(new(barcode, Number(combination, "Stok", "Miktar", "Quantity"), null, true, CombinationOptions(Value(combination, "Degerler")), Bool(combination, "Aktif"), combination));
        if (reads.Count > 0) return reads;
        if (Value(entry, "VariantBarkod", "VariantBarcode", "Barkod", "Barcode", "CatalogBarcode") is not { } entryBarcode) return reads;
        reads.Add(new(entryBarcode, Number(entry, "Miktar", "Stok", "Quantity"), Number(entry, "Fiyat", "FiyatFarki", "Price"),
            Bool(entry, "FiyatFarkiMi") ?? true, EntryOptions(entry), Bool(entry, "Aktif"), entry));
        return reads;
    }

    // The sale price of a variant: the product price plus the difference, or the variant's own price.
    public static decimal? SalePrice(VariantRead variant, decimal? productPrice) =>
        variant.Price is not { } price || price == 0 ? productPrice
        : variant.PriceIsDifference ? productPrice is { } basePrice ? Math.Max(basePrice + price, 0) : null
        : price;

    // Element names only, for an entry the reader does not understand.
    public static string Shape(XElement entry) =>
        $"{entry.Name.LocalName}[{string.Join(",", entry.Descendants().Select(x => x.Name.LocalName).Distinct().Take(20))}]";

    // Tanim/Deger (VariantAttr), Definition/Value or Name/Value pairs, and the Variant1Deger/Variant2Deger values.
    private static IReadOnlyDictionary<string, string> EntryOptions(XElement entry)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entry.Descendants().Where(x => x.HasElements))
        {
            var name = Value(pair, "Tanim", "Definition", "Name");
            var value = Value(pair, "Deger", "Value");
            if (name is not null && value is not null) options.TryAdd(name, value);
        }
        if (Value(entry, "Variant1Deger") is { } first) options.TryAdd("Varyant 1", first);
        if (Value(entry, "Variant2Deger") is { } second) options.TryAdd("Varyant 2", second);
        return options;
    }

    // Degerler is the combination's values as text ("Kırmızı,XL"); they are kept in order.
    private static IReadOnlyDictionary<string, string> CombinationOptions(string? values)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = (values ?? "").Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++) options[$"Varyant {index + 1}"] = parts[index];
        return options;
    }

    private static string? Value(XElement element, params string[] names) =>
        names.Select(name => PttAvmHttpClient.ContractValue(element, name)).FirstOrDefault(x => x is not null);

    private static decimal? Number(XElement element, params string[] names) =>
        names.Select(name => decimal.TryParse(PttAvmHttpClient.ContractValue(element, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : (decimal?)null).FirstOrDefault(x => x is not null);

    private static bool? Bool(XElement element, string name) => bool.TryParse(PttAvmHttpClient.ContractValue(element, name), out var value) ? value : null;
}
