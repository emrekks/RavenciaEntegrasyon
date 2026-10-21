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
    public static (IReadOnlyList<RemoteCatalogProduct> Products, int? TotalCount, IReadOnlyList<AdapterPageIssue> Issues) Page(XElement result)
    {
        var products = new List<RemoteCatalogProduct>();
        var issues = new List<AdapterPageIssue>();
        int? total = null;
        foreach (var row in PttAvmHttpClient.ContractItems(result, "StokKontrolDetay"))
        {
            if (Number(row, "RowCount") is { } count && count >= 0) total ??= (int)Math.Min(count, int.MaxValue);
            if (Product(row, issues) is { } product) products.Add(product);
        }
        return (products, total, issues);
    }

    internal static RemoteCatalogProduct? Product(XElement row, List<AdapterPageIssue> issues)
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
        var variantList = row.Elements().FirstOrDefault(x => x.Name.LocalName == "VariantListesi");
        if (variantList is not null && !PttAvmHttpClient.IsNil(variantList))
        {
            foreach (var entry in variantList.Elements())
            {
                var variantBarcode = FirstValue(entry, "VariantBarkod", "VariantBarcode", "Barkod", "Barcode");
                if (variantBarcode is null)
                {
                    issues.Add(new("PRODUCT_VARIANT_INVALID", $"{barcode}:{entry.Name.LocalName}", $"PTT AVM ürünü {barcode} için bir varyantın barkodu okunamadı; varyant içeri alınmadı."));
                    continue;
                }
                // A variant's price is a VAT-inclusive difference to the product's price.
                var difference = FirstNumber(entry, "Fiyat", "FiyatFarki", "Price") ?? 0;
                var quantity = FirstNumber(entry, "Miktar", "Stok", "Quantity");
                variants.Add(new(variantBarcode, variantBarcode, variantBarcode, null, Options(entry), !active,
                    price is null ? null : Math.Max(price.Value + difference, 0), null, vatRate, quantity, "TRY", Snapshot(entry).ToJsonString(), images, Desi: desi));
            }
        }
        if (variants.Count == 0)
            variants.Add(new(barcode, barcode, barcode, null, new Dictionary<string, string>(), !active,
                price, null, vatRate, Number(row, "Miktar"), "TRY", "{}", images, Desi: desi));
        return new(productId, Value(row, "UrunKodu") ?? barcode, title, Value(row, "UzunAciklama") ?? Value(row, "Aciklama") ?? "", null, null,
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

    // Attributes/Attribute (or VariantAttrV3, Ozellik) with Definition/Value (or Tanim/Deger, Name/Value).
    private static IReadOnlyDictionary<string, string> Options(XElement entry)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entry.Descendants().Where(x => x.HasElements))
        {
            var name = FirstValue(pair, "Definition", "Tanim", "Name", "Ozellik");
            var value = FirstValue(pair, "Value", "Deger");
            if (name is not null && value is not null) options.TryAdd(name, value);
        }
        return options;
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

    private static decimal? FirstNumber(XElement element, params string[] names) => names.Select(name => Number(element, name)).FirstOrDefault(x => x is not null);

    private static bool? Bool(XElement element, string name) => bool.TryParse(Value(element, name), out var value) ? value : null;
}
