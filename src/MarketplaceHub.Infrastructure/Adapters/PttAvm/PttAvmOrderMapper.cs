using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Maps PTT AVM's order list (SOAP SiparisKontrolListesiV2) to the package-shaped order stream the processor
// merges for every marketplace. PTT AVM tracks status per order line (SiparisUrun.SiparisDurumu) and has no
// package concept, so one package per order carries every line; its status is the least advanced one among the
// lines that were not cancelled, as for Pazarama. Line statuses are translated to the Trendyol-style package
// statuses (Created, Shipped, Delivered, Cancelled, ...) the panel already understands.
public static class PttAvmOrderMapper
{
    public static AdapterPageResult<RemoteOrder> Page(XElement result)
    {
        var rows = new List<RemoteOrder>();
        var issues = new List<AdapterPageIssue>();
        foreach (var order in PttAvmHttpClient.ContractItems(result, "TedarikciSiparisKontrolV2"))
        {
            var orderNumber = Value(order, "SiparisNo");
            if (orderNumber is null)
            {
                issues.Add(new("ORDER_PACKAGE_INVALID", "unknown", "PTT AVM siparişi içeri alınmadı: sipariş numarası yok."));
                continue;
            }
            if (Order(order, orderNumber, issues) is { } mapped) rows.Add(mapped);
        }
        // SiparisKontrolListesiV2 has no paging: one answer is the whole window.
        return new(rows, null, false, null, issues);
    }

    private static RemoteOrder? Order(XElement order, string orderNumber, List<AdapterPageIssue> issues)
    {
        var ordered = TurkeyTime.Parse(Value(order, "IslemTarihi"));
        if (ordered is null)
        {
            issues.Add(new("ORDER_PACKAGE_INVALID", orderNumber, $"PTT AVM siparişi {orderNumber} içeri alınmadı: işlem tarihi okunamadı."));
            return null;
        }
        var lines = new List<RemoteOrderLine>();
        var allocations = new List<RemotePackageAllocation>();
        var lineStatuses = new List<string>();
        // Cancelled lines are no part of the order value while another line is live; a fully cancelled order keeps
        // the value it was placed with (as for Pazarama, audit 2026-10-20 S21).
        decimal gross = 0, discount = 0, liveGross = 0, liveDiscount = 0;
        var hasLiveLine = false;
        var products = order.Elements().FirstOrDefault(x => x.Name.LocalName == "SiparisUrunler");
        foreach (var line in products is null ? [] : PttAvmHttpClient.ContractItems(products, "SiparisUrun"))
        {
            var lineId = Value(line, "LineItemId");
            var quantity = Number(line, "ToplamIslemAdedi");
            // KdvDahilToplamTutar is the line total the customer paid (quantity × VAT-inclusive price, after the
            // discounts); IndirimToplam is the discount on it (PTT AVM's and the store's share together).
            var paid = Number(line, "KdvDahilToplamTutar");
            if (lineId is null || quantity is not > 0 || paid is null or < 0)
            {
                issues.Add(new("ORDER_PACKAGE_LINE_INVALID", $"{orderNumber}:{lineId ?? "?"}", $"PTT AVM siparişi {orderNumber} alındı ancak bir ürün satırında satır kimliği, adet veya tutar eksik."));
                continue;
            }
            var lineDiscount = Math.Max(Number(line, "IndirimToplam") ?? 0, 0);
            var lineGross = paid.Value + lineDiscount;
            var raw = LineStatus(Value(line, "SiparisDurumu"));
            lineStatuses.Add(raw);
            // UrunBarkod is the product's barcode (for a model, the model code); the variant sold carries its own in
            // VariantBarkod (live: RYP001 / RYP00105). The documentation calls VariantBarkod a comma list of variant
            // ids, so a value with a comma is not taken as a barcode.
            var barcode = Value(line, "VariantBarkod") is { } variantBarcode && !variantBarcode.Contains(',', StringComparison.Ordinal) ? variantBarcode : Value(line, "UrunBarkod");
            var title = Value(line, "Urun") ?? barcode ?? lineId;
            var cancelled = raw == "Cancelled" ? quantity.Value : 0;
            lines.Add(new(lineId, barcode ?? Value(line, "UrunId") ?? lineId, barcode, title, quantity.Value, decimal.Round(lineGross / quantity.Value, 4), Number(line, "KdvOrani") ?? 0, raw, LineSnapshot(line), cancelled));
            // The allocation counts only what is still active: a cancelled line's units are cancelled, not allocated, so the
            // package check (active + cancelled <= ordered) holds when other lines go on. A returned line was delivered first.
            allocations.Add(new(lineId, quantity.Value - cancelled, cancelled, Shipped(raw) ? quantity.Value : 0, raw is "Delivered" or "Returned" ? quantity.Value : 0, raw is "Returned" ? quantity.Value : 0));
            gross += lineGross;
            discount += lineDiscount;
            if (raw != "Cancelled")
            {
                hasLiveLine = true;
                liveGross += lineGross;
                liveDiscount += lineDiscount;
            }
        }
        if (lines.Count == 0)
        {
            issues.Add(new("ORDER_PACKAGE_INVALID", orderNumber, $"PTT AVM siparişi {orderNumber} içeri alınmadı: kullanılabilir ürün satırı yok."));
            return null;
        }

        if (hasLiveLine) (gross, discount) = (liveGross, liveDiscount);
        discount = Math.Clamp(discount, 0, gross);
        var net = gross - discount;
        var packageStatus = PackageStatus(lineStatuses);
        var trackingNumber = Value(order, "KargoBarkod") ?? CargoBarcodes(order).FirstOrDefault();
        var occurredAt = StatusObservedAt(ordered.Value, packageStatus);
        var package = new RemotePackage($"order:{orderNumber}", null, packageStatus, occurredAt, trackingNumber is null ? null : "PTT Kargo", trackingNumber, allocations, gross, discount, net);
        var remote = new RemoteOrder(orderNumber, orderNumber, ordered.Value, occurredAt, "TRY", gross, discount, net,
            CustomerSnapshot(order), Address(order, "Siparis", Value(order, "MusteriAdi"), Value(order, "MusteriSoyadi")),
            Address(order, "Fatura", Value(order, "FaturaMusteriAdi") ?? Value(order, "MusteriAdi"), Value(order, "FaturaMusteriSoyadi") ?? Value(order, "MusteriSoyadi")),
            lines, [package], OrderSnapshot(order).ToJsonString());
        // PTT AVM's IsInvoice: every live line true means it holds an invoice. A false proves nothing: an invoice
        // uploaded in PTT AVM's own seller panel leaves the lines false (live, 23 Oct 2026), so it gives no observation.
        return PttAvmInvoiceMapper.Invoiced(remote) == true
            ? remote with { Packages = [package with { Invoice = new("INVOICED", null, null, null) }] }
            : remote;
    }

    // SiparisDurumu values of the documentation, in the panel's package vocabulary. The Turkish letters are
    // folded first (gonderilmiş / gönderilmiş / gonderilmis all read the same).
    internal static string LineStatus(string? value) => Fold(value) switch
    {
        "kargo_yapilmasi_bekleniyor" => "Created",
        // Payment not yet confirmed (bank transfer, approval): not to be prepared yet.
        "havale_onayi_bekleniyor" or "onay_surecinde" => "Awaiting",
        "gonderilmis" or "gonderildi" => "Shipped",
        // The parcel came back to the store without being delivered.
        "gondericisine_teslim_edildi" => "Undelivered",
        "tamamlandi" => "Delivered",
        "iptal" or "odeme_gecersiz" => "Cancelled",
        "iade" => "Returned",
        _ => $"Unknown:{value?.Trim() ?? "null"}"
    };

    private static bool Shipped(string raw) => raw is "Shipped" or "Delivered" or "Undelivered" or "Returned";

    // The package status from the statuses of the lines still in the order; all cancelled is cancelled. Otherwise
    // the package is where its least advanced line is, so a line still to be shipped is not hidden behind one
    // already shipped. Each line keeps its own status and shipped quantity.
    internal static string PackageStatus(IReadOnlyCollection<string> lineStatuses)
    {
        var active = lineStatuses.Where(status => status != "Cancelled").ToList();
        if (active.Count == 0) return "Cancelled";
        return active.OrderBy(status => ShipmentPackageStatusPolicy.Rank(TrendyolShipmentPackageStatusPolicy.FromRemote(status))).First();
    }

    // PTT AVM gives no time for a status change, only the order's IslemTarihi. The package event time is that time
    // plus one second per status step, so each status the package goes through is its own ordered event (the
    // panel records an event once per package and time) and a later status never looks older than an earlier one.
    internal static DateTimeOffset StatusObservedAt(DateTimeOffset orderedAt, string packageStatus) => orderedAt.AddSeconds(packageStatus switch
    {
        "Awaiting" => 1,
        "Created" => 2,
        "Shipped" => 3,
        "Undelivered" => 4,
        "Delivered" => 5,
        "Cancelled" => 6,
        "Returned" => 7,
        _ => 0
    });

    // The order number ends with the order day: PTT-A1712HP2E-071024 was placed on 7 October 2024.
    internal static DateTime? OrderDay(string orderNumber)
    {
        var separator = orderNumber.LastIndexOf('-');
        var suffix = separator >= 0 ? orderNumber[(separator + 1)..] : "";
        return suffix.Length == 6 && DateTime.TryParseExact(suffix, "ddMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }

    private static IEnumerable<string> CargoBarcodes(XElement order)
    {
        var barcodes = order.Elements().FirstOrDefault(x => x.Name.LocalName == "Barcodes");
        if (barcodes is null || PttAvmHttpClient.IsNil(barcodes)) yield break;
        foreach (var item in barcodes.Elements().Where(x => x.Name.LocalName == "Barkod"))
            if (string.Equals(Value(item, "type"), "KARGO", StringComparison.OrdinalIgnoreCase) && Value(item, "barcode") is { } barcode)
                yield return barcode;
    }

    // The fields the panel reads for every platform, plus PTT AVM's own invoice and tax fields.
    internal static string CustomerSnapshot(XElement order)
    {
        var snapshot = new JsonObject();
        var first = Value(order, "MusteriAdi");
        var last = Value(order, "MusteriSoyadi");
        var name = string.Join(' ', new[] { first, last }.Where(x => x is not null));
        if (name.Length > 0)
        {
            snapshot["customerName"] = name;
            snapshot["customerFirstName"] = first ?? "";
            snapshot["customerLastName"] = last ?? "";
        }
        if (Value(order, "Eposta") is { } email) { snapshot["customerEmail"] = email; snapshot["email"] = email; }
        if (Value(order, "MusteriId") is { } customerId) snapshot["customerId"] = customerId;
        if (Value(order, "TCKN") is { } identity) snapshot["tcIdentityNumber"] = identity;
        if (Value(order, "VergiNo") is { } taxNumber) snapshot["taxNumber"] = taxNumber;
        if (Value(order, "VergiDaire") is { } taxOffice) snapshot["taxOffice"] = taxOffice;
        if (Value(order, "FirmaUnvani") is { } company) snapshot["companyName"] = company;
        if (Value(order, "FaturaTip") is { } invoiceType) snapshot["invoiceType"] = invoiceType;
        if (Number(order, "KargoTutari") is { } shipping && shipping > 0) snapshot["shipmentAmount"] = shipping;
        return snapshot.ToJsonString();
    }

    // Siparis* is the delivery address, Fatura* the invoice address; the panel's names are added beside PTT AVM's.
    internal static string Address(XElement order, string prefix, string? firstName, string? lastName)
    {
        var snapshot = new JsonObject();
        var line = Value(order, $"{prefix}Adresi");
        var district = Value(order, $"{prefix}Ilce");
        var city = Value(order, $"{prefix}Ili");
        if (line is null && district is null && city is null) return "{}";
        if (line is not null) { snapshot["address1"] = line; snapshot["fullAddress"] = string.Join(", ", new[] { line, district, city }.Where(x => x is not null)); }
        if (district is not null) snapshot["district"] = district;
        if (city is not null) snapshot["city"] = city;
        var name = string.Join(' ', new[] { firstName, lastName }.Where(x => x is not null));
        if (name.Length > 0) { snapshot["firstName"] = firstName ?? ""; snapshot["lastName"] = lastName ?? ""; snapshot["fullName"] = name; }
        if (Value(order, "TelefonNo") is { } phone) snapshot["phone"] = phone;
        return snapshot.ToJsonString();
    }

    // The order's own fields (without the line list, which travels per line) as JSON for the raw snapshot.
    private static JsonObject OrderSnapshot(XElement order)
    {
        var snapshot = new JsonObject();
        foreach (var field in order.Elements().Where(x => x.Name.LocalName is not ("SiparisUrunler" or "Barcodes")))
            if (!PttAvmHttpClient.IsNil(field) && field.Value.Trim() is { Length: > 0 } text) snapshot[field.Name.LocalName] = text;
        var barcodes = CargoBarcodes(order).ToArray();
        if (barcodes.Length > 0) snapshot["cargoBarcodes"] = new JsonArray([.. barcodes.Select(x => (JsonNode)x)]);
        return snapshot;
    }

    private static string LineSnapshot(XElement line)
    {
        var snapshot = new JsonObject();
        foreach (var field in line.Elements())
            if (!PttAvmHttpClient.IsNil(field) && field.Value.Trim() is { Length: > 0 } text) snapshot[field.Name.LocalName] = text;
        return snapshot.ToJsonString();
    }

    private static string? Value(XElement element, string name) => PttAvmHttpClient.ContractValue(element, name);

    private static decimal? Number(XElement element, string name) =>
        decimal.TryParse(Value(element, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;

    // Lower case without Turkish letters: "Gönderilmiş" → "gonderilmis".
    internal static string Fold(string? value)
    {
        var builder = new StringBuilder();
        foreach (var character in (value ?? "").Trim().ToLowerInvariant())
            builder.Append(character switch { 'ç' => 'c', 'ğ' => 'g', 'ı' => 'i', 'ö' => 'o', 'ş' => 's', 'ü' => 'u', _ => character });
        return builder.ToString();
    }
}
