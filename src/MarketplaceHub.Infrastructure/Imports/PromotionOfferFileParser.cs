using System.Globalization;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Imports;

public sealed record ParsedPromotionOfferFile(string Kind, string? PeriodText, IReadOnlyList<object> Rows, int SkippedRows, IReadOnlyList<string> Warnings);

// Trendyol's promotion offer exports: "Plus Komisyon Tarifesi", "Avantajlı Ürün Etiketi" and "Flaş Ürünler". The kind is
// told by the columns only that export has; columns are found by their header text as CommissionTariffFileParser finds
// them. A number that does not read leaves that value unknown (with a warning), never guessed.
public static class PromotionOfferFileParser
{
    private const int MaximumWarnings = 20;

    public static ParsedPromotionOfferFile Parse(XlsxTable table)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < table.Headers.Count; index++)
        {
            var key = CommissionTariffFileParser.HeaderKey(table.Headers[index]);
            if (key.StartsWith("TARIHARALIGI", StringComparison.Ordinal)) key = "TARIHARALIGI";
            columns.TryAdd(key, index);
        }
        var kind = columns.ContainsKey("PLUSFIYATUSTLIMIT") && columns.ContainsKey("PLUSKOMISYONTEKLIFI") ? PromotionOfferKinds.Plus
            : columns.ContainsKey("1YILDIZUSTFIYAT") ? PromotionOfferKinds.Advantage
            : columns.ContainsKey("24SAATFIYAT") || columns.ContainsKey("3SAATFIYAT") ? PromotionOfferKinds.Flash
            : throw new InvalidDataException("Dosya Plus komisyon tarifesi, avantajlı ürün etiketi ya da flaş ürünler dosyası değil; Trendyol'dan indirilen dosyayı yükleyin.");
        if (!columns.ContainsKey("BARKOD")) throw new InvalidDataException("Dosyada BARKOD sütunu yok.");

        var warnings = new List<string>();
        var skipped = 0;
        var rows = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? period = null;
        foreach (var row in table.Rows)
        {
            string? Text(string column) => columns.TryGetValue(column, out var index) && index < row.Cells.Count && !string.IsNullOrWhiteSpace(row.Cells[index]) && row.Cells[index].Trim() != "-" ? row.Cells[index].Trim() : null;
            void Warn(string reason) { if (warnings.Count < MaximumWarnings) warnings.Add($"Satır {row.RowNumber.ToString(CultureInfo.InvariantCulture)}: {reason}"); }
            decimal? Number(string column)
            {
                var text = Text(column);
                if (text is null) return null;
                if (CommissionTariffFileParser.TryParseNumber(text, out var value) && value >= 0) return value;
                Warn($"{table.Headers[columns[column]]} değeri sayı değil ({text}); bilinmiyor sayıldı.");
                return null;
            }
            bool? YesNo(string column) => Text(column) is not { } text ? null : CommissionTariffFileParser.HeaderKey(text) is "VAR" or "EVET" or "KAMPANYALIURUN" ? true : CommissionTariffFileParser.HeaderKey(text) is "YOK" or "HAYIR" ? false : null;

            var barcodeText = Text("BARKOD");
            if (barcodeText is null) { skipped++; Warn("barkod yok."); continue; }
            var name = Short(Text("URUNISMI") ?? Text("URUNADI"), 500);
            var model = Short(Text("MODELKODU"), 160);
            var promotions = Short(Text("GUNCELUYGULANANPROMOSYONLAR"), 2000);
            switch (kind)
            {
                case PromotionOfferKinds.Plus:
                    if (!seen.Add(barcodeText)) { skipped++; Warn($"{barcodeText} barkodu dosyada birden çok kez var; ilki alındı."); continue; }
                    period ??= Short(Text("TARIHARALIGI"), 120);
                    var plusRate = Number("PLUSKOMISYONTEKLIFI");
                    var rate = Number("GUNCELKOMISYON");
                    rows.Add(new PlusOfferRow(Short(barcodeText, 160)!, Short(Text("SATICISTOKKODU"), 160), model, name, Short(Text("BEDEN"), 80), Number("STOK"),
                        Number("GUNCELTSF"), Number("KOMISYONAESASFIYAT"), rate is > 100 ? null : rate, Number("PLUSFIYATUSTLIMIT"), plusRate is > 100 ? null : plusRate, promotions));
                    break;
                case PromotionOfferKinds.Advantage:
                    if (!seen.Add(barcodeText)) { skipped++; Warn($"{barcodeText} barkodu dosyada birden çok kez var; ilki alındı."); continue; }
                    rows.Add(new AdvantageOfferRow(Short(barcodeText, 160)!, model, name, Short(Text("BEDEN"), 80), Number("STOK"), YesNo("KOMISYONTARIFESI"),
                        Number("1YILDIZUSTFIYAT"), Number("1YILDIZALTFIYAT"), Number("2YILDIZUSTFIYAT"), Number("2YILDIZALTFIYAT"), Number("3YILDIZUSTFIYAT"),
                        Number("MUSTERININGORDUGUFIYAT") ?? Number("MUSTERININGORDUGUGUNCELFIYAT"), Number("TRENDYOLSATISFIYATI"), promotions));
                    break;
                default:
                    // A flash row is a model's colour with all its barcodes ("A, B, C").
                    var barcodes = barcodeText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Short(x, 160)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var flashRate = Number("MEVCUTKOMISYON");
                    rows.Add(new FlashOfferRow(model, barcodes, name, Number("STOK"), Number("MEVCUTFIYAT"), Number("MUSTERININGORDUGUFIYAT"), flashRate is > 100 ? null : flashRate,
                        Number("24SAATFIYAT"), Short(Text("24SAATFLASBASLANGICTARIHI"), 40), Short(Text("24SAATFLASBITISTARIHI"), 40),
                        Number("3SAATFIYAT"), Short(Text("3SAATFLASBASLANGICTARIHI"), 40), Short(Text("3SAATFLASBITISTARIHI"), 40),
                        YesNo("URUNKOMISYONTARIFESECENEGI"), YesNo("KAMPANYALIURUN"), promotions));
                    break;
            }
        }
        if (rows.Count == 0) throw new InvalidDataException(warnings.Count > 0 ? $"Dosyada okunabilen satır yok. {warnings[0]}" : "Dosyada satır yok.");
        return new(kind, period, rows, skipped, warnings);
    }

    private static string? Short(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];
}
