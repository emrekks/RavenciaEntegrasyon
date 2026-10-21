using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Maps PTT AVM's product list (SOAP StokKontrolListesi, StokKontrolDetay rows) to the catalog shape every import
// reads. The barcode is the match key (the user's choice, and PTT AVM's own key for price and stock). A product
// without variants is one variant carrying the product's barcode, price and stock; a product with a VariantListesi
// becomes one variant per entry. The documentation leaves the shape of VariantListesi open (the sample answers nil),
// so its entries are read by the names PTT AVM uses on the write side (VariantBarcode, Quantity, Price,
// Attributes/Definition/Value) and their Turkish read-side spellings; an entry without a barcode is reported.
public static class PttAvmProductMapper
{
    public static (IReadOnlyList<RemoteCatalogProduct> Products, int? TotalCount, IReadOnlyList<AdapterPageIssue> Issues) Page(XElement result, IReadOnlyDictionary<string, IReadOnlyList<XElement>>? variantsByProductBarcode = null)
    {
        var products = new List<RemoteCatalogProduct>();
        var issues = new List<AdapterPageIssue>();
        int? total = null;
        foreach (var row in PttAvmHttpClient.ContractItems(result, "StokKontrolDetay"))
        {
            if (Number(row, "RowCount") is { } count && count >= 0) total ??= (int)Math.Min(count, int.MaxValue);
            if (Product(row, issues, variantsByProductBarcode) is { } product) products.Add(product);
        }
        return (products, total, issues);
    }

    internal static RemoteCatalogProduct? Product(XElement row, List<AdapterPageIssue> issues, IReadOnlyDictionary<string, IReadOnlyList<XElement>>? variantsByProductBarcode = null)
    {
        var productId = Value(row, "UrunId");
        var barcode = Value(row, "Barkod");
        if (productId is null || barcode is null)
        {
            issues.Add(new("PRODUCT_INVALID", productId ?? barcode ?? "unknown", "PTT AVM ürünü içeri alınmadı: ürün kimliği veya barkodu yok."));
            return null;
        }
        var title = Value(row, "UrunAdi") ?? barcode;
        var vatRate = Number(row, "KDVOran");
        var price = SalePrice(row, vatRate);
        var active = Bool(row, "Aktif") ?? true;
        var images = Images(row);
        var desi = Number(row, "Desi");
        var variants = new List<RemoteCatalogVariant>();
        // The product's own VariantListesi, or else the GetProductsWithVariants entries of its barcode.
        var variantList = row.Elements().FirstOrDefault(x => x.Name.LocalName == "VariantListesi");
        IReadOnlyList<XElement> entries = variantList is not null && !PttAvmHttpClient.IsNil(variantList) && variantList.HasElements
            ? variantList.Elements().ToList()
            : variantsByProductBarcode?.GetValueOrDefault(barcode) ?? [];
        foreach (var entry in entries)
        {
            var reads = PttAvmVariantMapper.Read(entry);
            if (reads.Count == 0)
            {
                issues.Add(new("PRODUCT_VARIANT_INVALID", $"{barcode}:{PttAvmVariantMapper.Shape(entry)}", $"PTT AVM ürünü {barcode} için bir varyantın barkodu okunamadı ({PttAvmVariantMapper.Shape(entry)}); varyant içeri alınmadı."));
                continue;
            }
            foreach (var read in reads)
                variants.Add(new(read.Barcode, read.Barcode, read.Barcode, null, read.Options, !active || read.Active == false,
                    PttAvmVariantMapper.SalePrice(read, price), null, vatRate, read.Quantity, "TRY", Snapshot(read.Source).ToJsonString(), images, Desi: desi));
        }
        // The product's stock code is UrunKodu; the documentation says the barcode replaced it, so it falls back to the barcode.
        if (variants.Count == 0)
            variants.Add(new(barcode, Value(row, "UrunKodu") ?? barcode, barcode, null, new Dictionary<string, string>(), !active,
                price, null, vatRate, Number(row, "Miktar"), "TRY", "{}", images, Desi: desi));
        // The product barcode groups the variants (for a model it is the model code).
        return new(productId, barcode, title, Value(row, "UzunAciklama") ?? Value(row, "Aciklama") ?? "", null, null,
            Value(row, "YeniKategoriId") is { } category && category != "0" ? category : null, null, images, variants, Snapshot(row).ToJsonString());
    }

    // KDVli is the VAT-inclusive price; when PTT AVM answers 0 there it is worked out from KDVsiz and the VAT rate.
    internal static decimal? SalePrice(XElement row, decimal? vatRate)
    {
        if (Number(row, "KDVli") is { } withVat && withVat > 0) return withVat;
        if (Number(row, "KDVsiz") is { } withoutVat && withoutVat > 0) return decimal.Round(withoutVat * (1 + (vatRate ?? 0) / 100m), 2);
        return null;
    }

    private static IReadOnlyList<string> Images(XElement row)
    {
        var urls = new List<string>();
        foreach (var name in new[] { "Resim1Url", "Resim2Url", "Resim3Url" })
            if (Value(row, name) is { } url) urls.Add(url);
        var list = row.Elements().FirstOrDefault(x => x.Name.LocalName == "ResimListesi");
        if (list is not null && !PttAvmHttpClient.IsNil(list))
            foreach (var item in list.Elements())
                if ((item.HasElements ? FirstValue(item, "Url", "ResimUrl", "url") : item.Value.Trim()) is { Length: > 0 } url) urls.Add(url);
        return urls.Where(url => Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https").Distinct(StringComparer.Ordinal).ToList();
    }

    // Every non-nil field as JSON (nested lists keep their own fields), never the image streams.
    internal static JsonObject Snapshot(XElement element)
    {
        var snapshot = new JsonObject();
        foreach (var field in element.Elements())
        {
            var name = field.Name.LocalName;
            if (PttAvmHttpClient.IsNil(field) || name.EndsWith("Stream", StringComparison.Ordinal)) continue;
            if (field.HasElements) snapshot[name] = new JsonArray([.. field.Elements().Select(x => (JsonNode)(x.HasElements ? Snapshot(x) : JsonValue.Create(x.Value.Trim())))]);
            else if (field.Value.Trim() is { Length: > 0 } text) snapshot[name] = text;
        }
        return snapshot;
    }

    private static string? Value(XElement element, string name) => PttAvmHttpClient.ContractValue(element, name);

    private static string? FirstValue(XElement element, params string[] names) => names.Select(name => Value(element, name)).FirstOrDefault(x => x is not null);

    private static decimal? Number(XElement element, string name) =>
        decimal.TryParse(Value(element, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static bool? Bool(XElement element, string name) => bool.TryParse(Value(element, name), out var value) ? value : null;
}
