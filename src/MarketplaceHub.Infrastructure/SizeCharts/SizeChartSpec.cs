using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Infrastructure.SizeCharts;

// One size of the beden-tablosu skill's spec.json: göğüs is one side (flat width, the panel's "En"); boy, or ön and
// arka boy when they differ. Kilo is never given: the skill estimates it and labels it as an estimate.
public sealed record SizeChartSpecSize(string Beden, decimal Gogus, decimal? Boy, decimal? BoyOn, decimal? BoyArka);

// What the panel asks the skill to draw: the product's own name and the seller's sizes.
public sealed record SizeChartSpecRequest(string ProductName, IReadOnlyList<SizeChartSpecSize> Sizes);

// The skill's spec.json as the panel fills it: the product name (the card's title) and the seller's sizes and
// measurements; only the icon (sleeve, neckline, hem…) is left to Claude, who reads it from the product picture.
public static class SizeChartSpec
{
    public const int MaxSizes = 12;
    public const int MaxProductName = 60;

    // The card's title, cut at a word boundary to what fits on the chart; never reworded.
    public static string ProductName(string title)
    {
        var name = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length <= MaxProductName) return name;
        var cut = name[..(MaxProductName + 1)].LastIndexOf(' ');
        return (cut > 0 ? name[..cut] : name[..MaxProductName]).TrimEnd();
    }

    // The default note of every table with a kilo column (skill's SKILL.md, step 2).
    public const string KiloNote = "Kilonuz iki aralığın sınırındaysa veya bol giymeyi seviyorsanız bir üst bedeni seçmenizi öneririz.";

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    // The panel's column names (any case, "(cm)" and spaces ignored) and the spec field each one fills.
    private static readonly Dictionary<string, string> Fields = new(StringComparer.Ordinal)
    {
        ["en"] = "gogus",
        ["göğüs"] = "gogus",
        ["gogus"] = "gogus",
        ["göğüseni"] = "gogus",
        ["göğüsgenişliği"] = "gogus",
        ["göğüs(tektaraf)"] = "gogus",
        ["göğüstektaraf"] = "gogus",
        ["boy"] = "boy",
        ["önboy"] = "boy_on",
        ["onboy"] = "boy_on",
        ["arkaboy"] = "boy_arka",
    };

    private static readonly HashSet<string> Sleeves = ["short", "three_quarter", "long", "sleeveless"];
    private static readonly HashSet<string> Necklines = ["round", "v", "boat", "shirt"];
    private static readonly HashSet<string> Hems = ["straight", "curved"];
    private static readonly HashSet<string> Motifs = ["braid", "stripe", "dot", "chevron"];

    internal static string? FieldOf(string column)
    {
        var key = column.Trim().ToLower(Turkish).Replace("(cm)", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return Fields.GetValueOrDefault(key);
    }

    // The validated grid as the skill's sizes. En is göğüs (one side), Boy is boy, Ön Boy and Arka Boy go together;
    // any other column has no place on the chart and is refused, never guessed.
    public static (IReadOnlyList<SizeChartSpecSize>? Sizes, string? Error) Map(SizeChartTable table)
    {
        var fields = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < table.Columns.Count; index++)
        {
            var name = table.Columns[index];
            if (FieldOf(name) is not { } field)
                return (null, name.Trim().ToLower(Turkish) == "kilo"
                    ? "Kilo kolonu girilmez: beden tablosu tahmini kilo aralığını göğüs ölçüsünden kendisi hesaplar ve tahmin olarak yazar."
                    : $"'{name}' kolonu beden tablosu tasarımında yok. Kullanılabilen kolonlar: En (göğüs, tek taraf), Boy, ya da Ön Boy ve Arka Boy.");
            if (!fields.TryAdd(field, index)) return (null, $"'{name}' kolonu başka bir kolonla aynı ölçüyü gösteriyor.");
        }
        if (!fields.ContainsKey("gogus")) return (null, "Beden tablosu için Göğüs kolonu (tek taraf) gerekli.");
        var dual = fields.ContainsKey("boy_on") || fields.ContainsKey("boy_arka");
        if (dual && !(fields.ContainsKey("boy_on") && fields.ContainsKey("boy_arka"))) return (null, "Ön ve arka boy farklıysa Ön Boy ve Arka Boy kolonlarının ikisi de girilmeli.");
        if (dual && fields.ContainsKey("boy")) return (null, "Boy kolonu Ön Boy ve Arka Boy ile birlikte kullanılamaz; ya Boy ya da Ön Boy ve Arka Boy girin.");
        if (!dual && !fields.ContainsKey("boy")) return (null, "Beden tablosu için Boy kolonu (ya da Ön Boy ve Arka Boy) gerekli.");
        if (table.Rows.Count > MaxSizes) return (null, $"Beden tablosu en fazla {MaxSizes} beden gösterebilir.");

        decimal? Value(SizeChartRow row, string field) => fields.TryGetValue(field, out var index) ? SizeChartRules.Parse(row.Values[index]) : null;
        return (table.Rows.Select(row => new SizeChartSpecSize(row.Size, Value(row, "gogus")!.Value, Value(row, "boy"), Value(row, "boy_on"), Value(row, "boy_arka"))).ToList(), null);
    }

    // spec.json without the icon, which Claude adds. Numbers stay numbers (66.5, never "66,5").
    public static JsonObject Json(string productName, IReadOnlyList<SizeChartSpecSize> sizes)
    {
        var array = new JsonArray();
        foreach (var size in sizes)
        {
            var item = new JsonObject { ["beden"] = size.Beden, ["gogus"] = size.Gogus };
            if (size.Boy is { } boy) item["boy"] = boy;
            if (size.BoyOn is { } boyOn) item["boy_on"] = boyOn;
            if (size.BoyArka is { } boyArka) item["boy_arka"] = boyArka;
            array.Add(item);
        }
        return new JsonObject { ["product_name"] = productName, ["extra_notes"] = new JsonArray(KiloNote), ["sizes"] = array };
    }

    // The spec.json the script drew from (Claude copies it next to the picture) must hold exactly the given sizes and
    // values and product name, no kilo or design overrides and a known icon. Null when it does; else why not.
    public static string? Check(byte[] specJson, string productName, IReadOnlyList<SizeChartSpecSize> sizes)
    {
        JsonObject? spec;
        try { spec = JsonNode.Parse(specJson) as JsonObject; }
        catch (JsonException) { return "spec.json okunamadı"; }
        if (spec is null) return "spec.json okunamadı";
        foreach (var key in spec.Select(pair => pair.Key))
            if (key is not ("product_name" or "icon" or "extra_notes" or "sizes")) return $"spec.json'a izin verilmeyen '{key}' alanı eklenmiş";

        if (Text(spec["product_name"]) != productName)
            return "ürün adı değiştirilmiş";
        if (spec["extra_notes"] is not JsonArray notes || notes.Count != 1 || notes[0]?.GetValue<string>() != KiloNote) return "alt notlar değiştirilmiş";
        if (IconError(spec["icon"]) is { } iconError) return iconError;

        if (spec["sizes"] is not JsonArray drawn || drawn.Count != sizes.Count) return "beden sayısı değişmiş";
        for (var index = 0; index < sizes.Count; index++)
        {
            if (drawn[index] is not JsonObject row) return "beden satırı okunamadı";
            var expected = sizes[index];
            var keys = row.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            var wanted = new HashSet<string>(StringComparer.Ordinal) { "beden", "gogus" };
            if (expected.Boy is not null) wanted.Add("boy");
            if (expected.BoyOn is not null) wanted.Add("boy_on");
            if (expected.BoyArka is not null) wanted.Add("boy_arka");
            if (!keys.SetEquals(wanted)) return $"'{expected.Beden}' satırının alanları değişmiş";
            if (Text(row["beden"]) != expected.Beden) return "beden adları ya da sırası değişmiş";
            if (Number(row["gogus"]) != expected.Gogus || Number(row["boy"]) != expected.Boy || Number(row["boy_on"]) != expected.BoyOn || Number(row["boy_arka"]) != expected.BoyArka)
                return $"'{expected.Beden}' ölçüleri değişmiş";
        }
        return null;
    }

    private static string? IconError(JsonNode? node)
    {
        if (node is not JsonObject icon) return "ikon seçilmemiş";
        foreach (var (key, value) in icon)
        {
            var ok = key switch
            {
                "sleeve" => Sleeves.Contains(Text(value) ?? ""),
                "neckline" => Necklines.Contains(Text(value) ?? ""),
                "hem" => Hems.Contains(Text(value) ?? ""),
                "motif" => Motifs.Contains(Text(value) ?? ""),
                "placket" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
                "studs" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False || Number(value) is decimal count and >= 1 and <= 12 && decimal.Truncate(count) == count,
                _ => false
            };
            if (!ok) return $"ikonda geçersiz '{key}' değeri";
        }
        return icon.ContainsKey("sleeve") && icon.ContainsKey("neckline") && icon.ContainsKey("hem") ? null : "ikonun kol, yaka ya da etek tipi eksik";
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static decimal? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<JsonElement>(out var element) && element.TryGetDecimal(out var number) ? number
        : node is JsonValue direct && direct.TryGetValue<decimal>(out var same) ? same : null;
}
