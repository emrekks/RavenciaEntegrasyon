using System.Net;
using System.Security.Claims;
using System.Text;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Infrastructure.Ai;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.SizeCharts;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class SizeChartRulesTests
{
    private static SizeChartInput Input(string?[] columns, params (string Size, decimal?[] Values)[] rows) =>
        new(columns, rows.Select(row => new SizeChartRowInput(row.Size, row.Values)).ToList());

    [Fact]
    public void Valid_measurements_are_kept_exactly_with_a_turkish_decimal_comma()
    {
        var (table, error) = SizeChartRules.Validate(Input(["Boy", "En"], ("S", [64m, 50m]), ("M", [66.5m, 53.0m])));
        Assert.Null(error);
        Assert.Equal(["Boy", "En"], table!.Columns);
        Assert.Equal(["64", "50"], table.Rows[0].Values);
        Assert.Equal(["66,5", "53"], table.Rows[1].Values);
    }

    [Theory]
    [InlineData(null, "boş")]
    [InlineData(0d, "0'dan büyük")]
    [InlineData(-3d, "0'dan büyük")]
    [InlineData(501d, "en fazla 500")]
    [InlineData(64.25, "bir ondalık")]
    public void A_bad_cell_is_refused_never_rounded(double? value, string expected)
    {
        var (table, error) = SizeChartRules.Validate(Input(["Boy"], ("S", [value is null ? null : (decimal)value.Value])));
        Assert.Null(table);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Column_names_must_be_present_unique_and_short_and_at_most_eight()
    {
        Assert.NotNull(SizeChartRules.Validate(Input(["Boy", " "], ("S", [1m, 2m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input(["Boy", "BOY"], ("S", [1m, 2m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input([new string('x', 25)], ("S", [1m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input(Enumerable.Range(1, 9).Select(i => (string?)$"K{i}").ToArray(), ("S", Enumerable.Repeat((decimal?)1m, 9).ToArray()))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input([], ("S", []))).Error);
    }

    [Fact]
    public void Every_row_needs_a_unique_size_and_one_value_per_column()
    {
        Assert.NotNull(SizeChartRules.Validate(Input(["Boy", "En"], ("S", [1m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input(["Boy"], ("S", [1m]), ("s", [2m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input(["Boy"], ("", [1m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(new SizeChartInput(["Boy"], [])).Error);
    }

    [Theory]
    [InlineData("Beden", true)]
    [InlineData("BEDEN", true)]
    [InlineData(" beden ", true)]
    [InlineData("Renk", false)]
    [InlineData(null, false)]
    public void The_size_option_is_the_one_named_beden(string? label, bool expected) => Assert.Equal(expected, SizeChartRules.IsSizeOption(label));

    private static IReadOnlyList<SizeChartSpecSize> Sizes(string?[] columns, params (string Size, decimal?[] Values)[] rows)
    {
        var (table, error) = SizeChartRules.Validate(Input(columns, rows));
        Assert.Null(error);
        var (sizes, specError) = SizeChartSpec.Map(table!);
        Assert.Null(specError);
        return sizes!;
    }

    private static string? MapError(string?[] columns, params (string Size, decimal?[] Values)[] rows) => SizeChartSpec.Map(SizeChartRules.Validate(Input(columns, rows)).Table!).Error;

    [Fact]
    public void En_is_one_side_gogus_and_boy_is_boy_in_the_skills_spec()
    {
        var sizes = Sizes(["Boy (cm)", "En (cm)"], ("S", [64m, 50m]), ("M", [66.5m, 53m]));
        Assert.Equal(new SizeChartSpecSize("S", 50m, 64m, null, null), sizes[0]);
        Assert.Equal(new SizeChartSpecSize("M", 53m, 66.5m, null, null), sizes[1]);
        var json = SizeChartSpec.Json("Kadın Kısa Kollu Bluz", sizes).ToJsonString();
        Assert.Equal("""{"product_name":"Kadın Kısa Kollu Bluz","extra_notes":["Kilonuz iki aralığın sınırındaysa veya bol giymeyi seviyorsanız bir üst bedeni seçmenizi öneririz."],"sizes":[{"beden":"S","gogus":50,"boy":64},{"beden":"M","gogus":53,"boy":66.5}]}""",
            System.Text.RegularExpressions.Regex.Unescape(json));
        Assert.DoesNotContain("kilo\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("icon", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Front_and_back_lengths_go_together_and_goguse_alias_works()
    {
        var sizes = Sizes(["Göğüs", "Ön Boy", "Arka Boy"], ("L", [54m, 71m, 77m]));
        Assert.Equal(new SizeChartSpecSize("L", 54m, null, 71m, 77m), sizes[0]);
        Assert.Contains("Ön Boy ve Arka Boy", MapError(["En", "Ön Boy"], ("L", [54m, 71m])), StringComparison.Ordinal);
        Assert.Contains("birlikte", MapError(["En", "Boy", "Ön Boy", "Arka Boy"], ("L", [54m, 70m, 71m, 77m])), StringComparison.Ordinal);
    }

    [Fact]
    public void Columns_the_chart_cannot_show_are_refused_never_guessed()
    {
        Assert.Contains("Göğüs kolonu", MapError(["Boy"], ("S", [64m])), StringComparison.Ordinal);
        Assert.Contains("Boy kolonu", MapError(["En"], ("S", [50m])), StringComparison.Ordinal);
        Assert.Contains("tahmin", MapError(["Boy", "En", "Kilo"], ("S", [64m, 50m, 60m])), StringComparison.Ordinal);
        Assert.Contains("'Kol Boyu'", MapError(["Boy", "En", "Kol Boyu"], ("S", [64m, 50m, 20m])), StringComparison.Ordinal);
        Assert.Contains("aynı ölçü", MapError(["En", "Göğüs", "Boy"], ("S", [50m, 50m, 64m])), StringComparison.Ordinal);
        var thirteen = Enumerable.Range(1, 13).Select(i => ($"B{i}", new decimal?[] { 60m, 50m })).ToArray();
        Assert.Contains("en fazla 12", MapError(["Boy", "En"], thirteen), StringComparison.Ordinal);
    }

    private static byte[] Spec(string sizes, string icon = """{"sleeve":"short","neckline":"round","hem":"straight"}""", string extra = "") =>
        Encoding.UTF8.GetBytes($$"""{"product_name":"Kadın Kısa Kollu Bluz","icon":{{icon}},"extra_notes":["{{SizeChartSpec.KiloNote}}"],"sizes":{{sizes}}{{extra}}}""");

    [Fact]
    public void The_spec_the_chart_was_drawn_from_must_hold_exactly_the_given_values()
    {
        var sizes = Sizes(["Boy", "En"], ("S", [64m, 50m]), ("M", [66.5m, 53m]));
        Assert.Null(SizeChartSpec.Check(Spec("""[{"beden":"S","gogus":50,"boy":64},{"beden":"M","gogus":53,"boy":66.5}]"""), "Kadın Kısa Kollu Bluz", sizes));
        Assert.Null(SizeChartSpec.Check(Spec("""[{"beden":"S","gogus":50.0,"boy":64},{"beden":"M","gogus":53,"boy":66.50}]""", """{"sleeve":"long","neckline":"v","hem":"curved","studs":4,"motif":"chevron","placket":false}"""), "Kadın Kısa Kollu Bluz", sizes));

        Assert.NotNull(SizeChartSpec.Check(Spec("""[{"beden":"S","gogus":50,"boy":64},{"beden":"M","gogus":53,"boy":67}]"""), "Kadın Kısa Kollu Bluz", sizes));
        Assert.NotNull(SizeChartSpec.Check(Spec("""[{"beden":"M","gogus":53,"boy":66.5},{"beden":"S","gogus":50,"boy":64}]"""), "Kadın Kısa Kollu Bluz", sizes));
        Assert.NotNull(SizeChartSpec.Check(Spec("""[{"beden":"S","gogus":50,"boy":64,"kilo":"58-64"},{"beden":"M","gogus":53,"boy":66.5}]"""), "Kadın Kısa Kollu Bluz", sizes));
        Assert.NotNull(SizeChartSpec.Check(Spec("""[{"beden":"S","gogus":100,"boy":64},{"beden":"M","gogus":106,"boy":66.5}]"""), "Kadın Kısa Kollu Bluz", sizes));
        Assert.NotNull(SizeChartSpec.Check(Spec("""[{"beden":"S","gogus":50,"boy":64},{"beden":"M","gogus":53,"boy":66.5}]""", extra: ""","ease":10"""), "Kadın Kısa Kollu Bluz", sizes));
        Assert.NotNull(SizeChartSpec.Check(Spec("""[{"beden":"S","gogus":50,"boy":64},{"beden":"M","gogus":53,"boy":66.5}]""", """{"sleeve":"short","neckline":"round","hem":"straight","color":"#1F2A56"}"""), "Kadın Kısa Kollu Bluz", sizes));
        Assert.NotNull(SizeChartSpec.Check(Spec("""[{"beden":"S","gogus":50,"boy":64},{"beden":"M","gogus":53,"boy":66.5}]""", """{"sleeve":"short","neckline":"round"}"""), "Kadın Kısa Kollu Bluz", sizes));
        Assert.NotNull(SizeChartSpec.Check(Spec("""[{"beden":"S","gogus":50,"boy":64},{"beden":"M","gogus":53,"boy":66.5}]""", """{"sleeve":"cape","neckline":"round","hem":"straight"}"""), "Kadın Kısa Kollu Bluz", sizes));
        Assert.NotNull(SizeChartSpec.Check(Encoding.UTF8.GetBytes("not json"), "Kadın Kısa Kollu Bluz", sizes));
    }

    [Fact]
    public void Only_a_complete_png_reports_its_size()
    {
        var png = Png(1200, 1800);
        Assert.Equal((1200, 1800), SizeChartService.PngSize(png));
        Assert.Null(SizeChartService.PngSize(png[..^12]));
        Assert.Null(SizeChartService.PngSize([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal((2400, 3600), SizeChartService.PngSize(Png(2400, 3600)));
    }

    [Theory]
    [InlineData(1200, 1800, null)]
    [InlineData(2400, 3600, "Beden tablosu 1200×1800 olmalı; üretilen görsel 2400×3600 geldi, tekrar deneyin.")]
    [InlineData(1200, 1799, "Beden tablosu 1200×1800 olmalı; üretilen görsel 1200×1799 geldi, tekrar deneyin.")]
    public void Only_a_1200x1800_png_is_accepted(int width, int height, string? expected)
    {
        var failure = Record.Exception(() => SizeChartService.CheckImage(Png(width, height)));
        Assert.Equal(expected, failure?.Message);
        Assert.Equal("AI_IMAGE_INVALID", Assert.IsType<SizeChartException>(Record.Exception(() => SizeChartService.CheckImage([1, 2, 3]))).Code);
    }

    // The bundled fallback skill (chart.json → JPEG).
    [Fact]
    public void The_drawn_values_must_echo_the_request_exactly()
    {
        var (table, _) = SizeChartRules.Validate(Input(["Boy", "En"], ("S", [64m, 50m]), ("M", [66.5m, 53m])));
        var same = """{"unit":"cm","columns":["Boy","En"],"rows":[{"size":"S","values":["64","50"]},{"size":"M","values":["66,5","53"]}]}""";
        var changed = """{"unit":"cm","columns":["Boy","En"],"rows":[{"size":"S","values":["64","50"]},{"size":"M","values":["66","53"]}]}""";
        var reordered = """{"unit":"cm","columns":["Boy","En"],"rows":[{"size":"M","values":["66,5","53"]},{"size":"S","values":["64","50"]}]}""";
        Assert.True(SizeChartService.EchoMatches(Encoding.UTF8.GetBytes(same), table!));
        Assert.False(SizeChartService.EchoMatches(Encoding.UTF8.GetBytes(changed), table!));
        Assert.False(SizeChartService.EchoMatches(Encoding.UTF8.GetBytes(reordered), table!));
        Assert.False(SizeChartService.EchoMatches(Encoding.UTF8.GetBytes("not json"), table!));
    }

    [Fact]
    public void Only_a_complete_jpeg_reports_its_size()
    {
        var jpeg = Jpeg(1200, 1800);
        Assert.Equal((1200, 1800), SizeChartService.JpegSize(jpeg));
        Assert.Null(SizeChartService.JpegSize(jpeg[..^2]));
        Assert.Null(SizeChartService.JpegSize([0x89, 0x50, 0x4E, 0x47]));
        Assert.Equal((800, 600), SizeChartService.JpegSize(Jpeg(800, 600)));
    }

    internal static byte[] Jpeg(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x12, 0x34,
        0xFF, 0xD9
    ];

    // A minimal complete PNG: signature, IHDR with the size, IEND.
    internal static byte[] Png(int width, int height)
    {
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
            8, 6, 0, 0, 0, 0, 0, 0, 0];
        return [.. header, 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
    }
}

// The owner's .skill / .zip file: checked before anything is sent to the Skills API.
public sealed class AiSkillPackageTests
{
    private const string SkillMd = "---\nname: ravencia-beden-tablosu\ndescription: Ravencia beden tablosu görseli üretir.\n---\n\n# Beden\n";

    internal static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        return buffer.ToArray();
    }

    private static (AiSkillBundle? Bundle, string? Error) Read(byte[] bytes) => AiSkillPackage.Read(new MemoryStream(bytes));

    [Fact]
    public void A_skill_file_with_one_folder_and_skill_md_is_read_with_its_name()
    {
        var (bundle, error) = Read(Zip(("ravencia-beden-tablosu/SKILL.md", SkillMd), ("ravencia-beden-tablosu/scripts/render_size_chart.py", "print(1)"), ("__MACOSX/._SKILL.md", "x"), ("ravencia-beden-tablosu/", "")));
        Assert.Null(error);
        Assert.Equal("ravencia-beden-tablosu", bundle!.Name);
        Assert.Equal(["ravencia-beden-tablosu/SKILL.md", "ravencia-beden-tablosu/scripts/render_size_chart.py"], bundle.Files.Select(file => file.Path));
        Assert.Equal(12, bundle.BundleHash.Length);
    }

    [Fact]
    public void Not_a_zip_is_refused() => Assert.Contains("arşivi değil", Read(Encoding.UTF8.GetBytes("plain text")).Error, StringComparison.Ordinal);

    [Fact]
    public void A_missing_skill_md_or_bad_frontmatter_is_refused()
    {
        Assert.Contains("SKILL.md", Read(Zip(("beden/README.md", "x"))).Error, StringComparison.Ordinal);
        Assert.Contains("tek bir üst klasör", Read(Zip(("SKILL.md", SkillMd))).Error, StringComparison.Ordinal);
        Assert.Contains("tek bir üst klasör", Read(Zip(("a/SKILL.md", SkillMd), ("b/x.py", "1"))).Error, StringComparison.Ordinal);
        Assert.Contains("'name' yok", Read(Zip(("a/SKILL.md", "---\ndescription: x\n---\n"))).Error, StringComparison.Ordinal);
        Assert.Contains("geçersiz", Read(Zip(("a/SKILL.md", "---\nname: Claude Beden\ndescription: x\n---\n"))).Error, StringComparison.Ordinal);
        Assert.Contains("description", Read(Zip(("a/SKILL.md", "---\nname: beden\n---\n"))).Error, StringComparison.Ordinal);
        Assert.Contains("frontmatter", Read(Zip(("a/SKILL.md", "# no frontmatter"))).Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a/../../etc/passwd")]
    [InlineData("/a/SKILL.md")]
    [InlineData("a/./x.py")]
    [InlineData("C:/a/x.py")]
    [InlineData("a\\..\\x.py")]
    public void A_path_leaving_the_folder_is_refused(string path) =>
        Assert.Contains("izin verilmeyen bir dosya yolu", Read(Zip(("a/SKILL.md", SkillMd), (path, "x"))).Error, StringComparison.Ordinal);

    [Fact]
    public void An_archive_unpacking_past_the_limit_is_refused()
    {
        // 26 MB of one letter packs into a few KB: a small upload that would unpack too large.
        var bomb = Zip(("a/SKILL.md", SkillMd), ("a/big.txt", new string('a', (int)AiSkillPackage.MaxUnpackedBytes + 1024 * 1024)));
        Assert.True(bomb.Length < AiSkillPackage.MaxUploadBytes);
        Assert.Contains("açılmış boyutu", Read(bomb).Error, StringComparison.Ordinal);
    }
}

public sealed class SizeChartSpecNameTests
{
    [Theory]
    [InlineData("Kadın Kısa Kollu Bluz", "Kadın Kısa Kollu Bluz")]
    [InlineData("  Kadın   Kısa Kollu  Bluz ", "Kadın Kısa Kollu Bluz")]
    [InlineData("Kışlık Saç Örgü Triko Düğmeli Yumuşak Bluz Büyük Beden Kadın Giyim Siyah", "Kışlık Saç Örgü Triko Düğmeli Yumuşak Bluz Büyük Beden Kadın")]
    public void The_product_name_is_the_cards_title_cut_at_a_word(string title, string expected)
    {
        var name = SizeChartSpec.ProductName(title);
        Assert.Equal(expected, name);
        Assert.True(name.Length <= SizeChartSpec.MaxProductName);
    }

    [Fact]
    public void A_changed_product_name_is_refused()
    {
        var sizes = new[] { new SizeChartSpecSize("S", 50m, 64m, null, null) };
        var spec = SizeChartSpec.Json("Kadın Bluz", sizes);
        spec["icon"] = System.Text.Json.Nodes.JsonNode.Parse("""{"sleeve":"short","neckline":"round","hem":"straight"}""");
        var bytes = Encoding.UTF8.GetBytes(spec.ToJsonString());
        Assert.Null(SizeChartSpec.Check(bytes, "Kadın Bluz", sizes));
        Assert.Equal("ürün adı değiştirilmiş", SizeChartSpec.Check(bytes, "Kadın Kısa Kollu Bluz", sizes));
    }
}

// The size chart is always the last picture: in the strip and variant lists the panel reads, and in what goes out.
public sealed class SizeChartPlacementTests
{
    private const string A = "/api/v1/files/product-media/00000000-0000-0000-0000-00000000000a/content";
    private const string B = "/api/v1/files/product-media/00000000-0000-0000-0000-00000000000b/content";
    private const string Chart = "/api/v1/files/product-media/00000000-0000-0000-0000-0000000000cc/content";
    private const string NewChart = "/api/v1/files/product-media/00000000-0000-0000-0000-0000000000dd/content";

    [Fact]
    public void An_upload_saved_after_the_chart_still_comes_before_it() =>
        Assert.Equal([A, B, Chart], SizeChartPlacement.Last([A, Chart, B], Chart));

    // Owner 7.11.2026, MZ052BOD: Beden × Renk, 7 colours each on its own card with 3 pictures. The family strip lists
    // card by card (the opened card first), so the opened card's chart, its 4th picture, stood after its 3 pictures
    // and before the other 6 colours. Every card's chart closes the strip, the opened card's the very last.
    [Fact]
    public void A_multi_colour_family_strip_ends_with_the_charts_the_opened_cards_last()
    {
        var cards = Enumerable.Range(1, 7).Select(index => new Guid($"00000000-0000-0000-0000-0000000000{index:D2}")).ToList();
        string Url(int card, int picture) => $"https://cdn.example.com/card-{card}-{picture}.jpg";
        const string OtherChart = "https://cdn.example.com/card-3-chart.jpg";
        const string OwnChart = "https://cdn.example.com/card-1-chart.jpg";
        var rows = new List<CatalogFamilyMedia.Row>();
        for (var card = 0; card < cards.Count; card++)
        {
            for (var picture = 0; picture < 3; picture++)
                rows.Add(new CatalogFamilyMedia.Row(Guid.NewGuid(), cards[card], null, picture, Url(card + 1, picture)));
            // Every variant of the card holds the card's pictures too, numbered 0, 1, 2 (and the chart 3).
            rows.Add(new CatalogFamilyMedia.Row(Guid.NewGuid(), cards[card], Guid.NewGuid(), 0, Url(card + 1, 0)));
        }
        rows.Add(new CatalogFamilyMedia.Row(Guid.NewGuid(), cards[0], null, 20, OwnChart));
        rows.Add(new CatalogFamilyMedia.Row(Guid.NewGuid(), cards[0], Guid.NewGuid(), 3, OwnChart));
        rows.Add(new CatalogFamilyMedia.Row(Guid.NewGuid(), cards[2], null, 3, OtherChart));
        var titles = cards.ToDictionary(id => id, _ => "Kışlık Saç Örgü Triko Düğmeli Yumuşak Bluz");

        var (defaultUrls, _) = CatalogFamilyMedia.Build(rows, titles, cards);
        Assert.Equal(OwnChart, defaultUrls[3]); // what the owner saw: the chart 4th, after the opened colour's 3 pictures

        var strip = SizeChartPlacement.Last(SizeChartPlacement.LastAll(defaultUrls, [OwnChart, null, OtherChart]), OwnChart);
        Assert.Equal(23, strip.Count);
        Assert.Equal([OtherChart, OwnChart], strip.TakeLast(2));
        Assert.Equal([Url(1, 0), Url(1, 1), Url(1, 2), Url(2, 0)], strip.Take(4));

        // A custom family order (a family reorder ranks every row) keeps the charts last too.
        var ordered = SizeChartPlacement.Last(SizeChartPlacement.LastAll(CatalogFamilyMedia.OrderedUrls(rows), [OwnChart, OtherChart]), OwnChart);
        Assert.Equal(OwnChart, ordered[^1]);
        Assert.Equal(OtherChart, ordered[^2]);
    }

    [Fact]
    public void A_reorder_that_put_the_chart_first_leaves_it_last() =>
        Assert.Equal([A, B, Chart], SizeChartPlacement.Last([Chart, A, B], Chart));

    [Fact]
    public void A_new_chart_replaces_the_old_one_which_is_then_an_ordinary_picture_or_gone()
    {
        // The product points at the new chart; the old one, if still on the card, is an ordinary picture.
        Assert.Equal([Chart, A, NewChart], SizeChartPlacement.Last([NewChart, Chart, A], NewChart));
        Assert.Equal([A, B], SizeChartPlacement.Last([A, B], Chart));
        Assert.Equal([A, B], SizeChartPlacement.Last([A, B], null));
    }

    private sealed record Item(Guid? VariantId, Guid Id);
    private static readonly Guid V1 = Guid.NewGuid(), V2 = Guid.NewGuid();
    private static readonly Guid P1 = Guid.NewGuid(), P2 = Guid.NewGuid(), C = Guid.NewGuid();

    [Fact]
    public void Every_variant_publishes_the_chart_last_even_when_its_own_pictures_lack_it_or_put_it_first()
    {
        // Card strip saved as C (chart), P1, P2; V1 has its own pictures including the chart; V2 has its own without it.
        Item[] media = [new(null, C), new(null, P1), new(null, P2), new(V1, C), new(V1, P2), new(V2, P1)];
        Assert.Equal([P2, C], PublicationVariantMedia.For(media, V1, x => x.VariantId, x => x.Id, C).Select(x => x.Id));
        Assert.Equal([P1, C], PublicationVariantMedia.For(media, V2, x => x.VariantId, x => x.Id, C).Select(x => x.Id));
        Assert.Equal([P1, P2, C], PublicationVariantMedia.For(media, Guid.NewGuid(), x => x.VariantId, x => x.Id, C).Select(x => x.Id));
        // Without a marked chart the saved order stands.
        Assert.Equal([C, P2], PublicationVariantMedia.For(media, V1, x => x.VariantId, x => x.Id).Select(x => x.Id));
    }

    [Fact]
    public void When_a_marketplace_takes_fewer_pictures_the_chart_is_still_sent_last()
    {
        string[] urls = ["1", "2", "3", "4", "5", "6", "chart"];
        Assert.Equal(["1", "2", "3", "4", "chart"], SizeChartPlacement.TakeKeepingLast(urls, 5, lastIsSizeChart: true));
        Assert.Equal(["1", "2", "3", "4", "5"], SizeChartPlacement.TakeKeepingLast(urls, 5, lastIsSizeChart: false));
        Assert.Equal(["1", "chart"], SizeChartPlacement.TakeKeepingLast(["1", "chart"], 5, lastIsSizeChart: true));
    }

    // Ş2: the chart is found by its url in the list that is trimmed, after blanks and duplicates are gone.
    [Fact]
    public void The_chart_is_told_by_its_url_after_blanks_and_duplicates_are_dropped()
    {
        const string chart = "https://cdn.example.com/chart.png";
        // The chart's own row could not be built (null): the last url is a product picture and must not take its place.
        string?[] withoutChartUrl = ["https://a/1.jpg", "https://a/2.jpg", "https://a/3.jpg", "https://a/4.jpg", "https://a/5.jpg", "https://a/6.jpg", null];
        Assert.Equal(["https://a/1.jpg", "https://a/2.jpg", "https://a/3.jpg", "https://a/4.jpg", "https://a/5.jpg"],
            ProductPublicationComposer.SelectPublicationImageUrls(withoutChartUrl, null, limit: 5));
        // The chart repeated earlier in the list (a duplicate the Distinct keeps first) still goes last and stays.
        string?[] chartTwice = ["https://a/1.jpg", chart, "https://a/2.jpg", "https://a/3.jpg", "https://a/4.jpg", "https://a/5.jpg", "", chart];
        Assert.Equal(["https://a/1.jpg", "https://a/2.jpg", "https://a/3.jpg", "https://a/4.jpg", chart],
            ProductPublicationComposer.SelectPublicationImageUrls(chartTwice, chart, limit: 5));
        // A chart url missing from the list changes nothing.
        Assert.Equal(["https://a/1.jpg", "https://a/2.jpg"], ProductPublicationComposer.SelectPublicationImageUrls(["https://a/1.jpg", "https://a/2.jpg"], chart, limit: 5));
    }

    [Fact]
    public void A_family_keeps_every_cards_chart_last_once_when_trimmed()
    {
        string[] urls = ["a1", "chartA", "a2", "b1", "chartB", "b2"];
        Assert.Equal(["a1", "a2", "chartA", "chartB"], SizeChartPlacement.TakeKeepingCharts(urls, 4, ["chartA", "chartB"]));
        Assert.Equal(["a1", "a2", "b1", "b2", "chartA", "chartB"], SizeChartPlacement.TakeKeepingCharts(urls, 250, ["chartA", "chartB"]));
        Assert.Equal(["a1", "chartA", "a2"], SizeChartPlacement.TakeKeepingCharts(urls, 3, []));
    }
}

// Ayarlar › Yapay Zekâ: only the owner and administrators reach it; the size chart is product work.
public sealed class AiSettingsRoleTests
{
    [Theory]
    [InlineData("GET", "/api/v1/settings/ai", "OPERATIONS", false)]
    [InlineData("GET", "/api/v1/settings/ai", "READONLY", false)]
    [InlineData("GET", "/api/v1/settings/ai/usage", "ACCOUNTING", false)]
    [InlineData("PUT", "/api/v1/settings/ai", "OPERATIONS", false)]
    [InlineData("POST", "/api/v1/settings/ai/test", "OPERATIONS", false)]
    [InlineData("POST", "/api/v1/settings/ai/skills/beden-tablosu/upload", "OPERATIONS", false)]
    [InlineData("GET", "/api/v1/settings/ai", "OWNER", true)]
    [InlineData("PUT", "/api/v1/settings/ai", "ADMINISTRATOR", true)]
    [InlineData("POST", "/api/v1/settings/ai/skills/beden-tablosu/upload", "OWNER", true)]
    [InlineData("POST", "/api/v1/products/11111111-1111-1111-1111-111111111111/size-chart/generate", "OPERATIONS", true)]
    [InlineData("POST", "/api/v1/products/11111111-1111-1111-1111-111111111111/size-chart/generate", "READONLY", false)]
    public async Task Roles(string method, string path, string role, bool allowed)
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test")) };
        context.Request.Method = method;
        context.Request.Path = path;
        var called = false;
        await new RoleAuthorizationMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.Equal(allowed, called);
    }

    [Fact]
    public void The_size_chart_skill_is_embedded_with_its_files()
    {
        var skill = AiSkillCatalog.Find(AiSkillCatalog.SizeChartKey);
        Assert.NotNull(skill);
        Assert.Contains(skill.Files, file => file.Path == "beden-tablosu/SKILL.md");
        Assert.Contains(skill.Files, file => file.Path == "beden-tablosu/scripts/draw_size_chart.py");
        Assert.Equal("beden-tablosu", skill.Name);
        Assert.Contains(skill.Files, file => file.Path == "beden-tablosu/assets/ravencia-symbol.png");
        Assert.Equal(12, skill.BundleHash.Length);
    }

    [Theory]
    [InlineData("sk-ant-api03-abcdefghijklmnop", true)]
    [InlineData("sk-ant-api03 abcdefghijklmnop", false)]
    [InlineData("sk-proj-abcdefghijklmnopqrstu", false)]
    [InlineData("sk-ant-x", false)]
    public void Only_a_claude_key_shape_is_accepted(string key, bool expected) => Assert.Equal(expected, AiSettingsService.IsApiKeyShaped(key));

    [Fact]
    public void Cost_uses_the_model_list_price_and_is_unknown_for_other_models()
    {
        Assert.Equal(0.005m, ClaudePricing.CostUsd("claude-sonnet-5-5", new ClaudeUsage(1000, 300, null, null)));
        Assert.Null(ClaudePricing.CostUsd("some-other-model", new ClaudeUsage(1000, 300, null, null)));
        Assert.Null(ClaudePricing.CostUsd("claude-sonnet-5-5", new ClaudeUsage(null, null, null, null)));
    }
}

// The SDK client against a fake Anthropic API: nothing reaches the real one.
public sealed class ClaudeClientTests
{
    private const string Key = "sk-ant-api03-test-key-never-shown";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? ApiKey, string? Beta, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("anthropic-beta", out var betas) ? string.Join(",", betas) : null, body));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string SkillResponse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5",
         "content":[
          {"type":"server_tool_use","id":"srvtoolu_1","name":"bash_code_execution","input":{"command":"python scripts/draw_size_chart.py --input chart.json"}},
          {"type":"bash_code_execution_tool_result","tool_use_id":"srvtoolu_1","content":{"type":"bash_code_execution_result","stdout":"OK","stderr":"","return_code":0,
            "content":[{"type":"bash_code_execution_output","file_id":"file_img"},{"type":"bash_code_execution_output","file_id":"file_echo"}]}},
          {"type":"text","text":"OK"}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":1500,"output_tokens":400,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}}
        """;

    private static string Metadata(string id, string name) =>
        $$"""{"id":"{{id}}","type":"file","filename":"{{name}}","mime_type":"application/octet-stream","size_bytes":10,"created_at":"2026-11-06T10:00:00Z","downloadable":true}""";

    [Fact]
    public async Task A_skill_run_returns_the_files_the_container_wrote_with_their_names_and_the_usage()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/messages" => Json(SkillResponse),
            "/v1/files/file_img" => Json(Metadata("file_img", "beden-tablosu.jpg")),
            "/v1/files/file_echo" => Json(Metadata("file_echo", "beden-tablosu.json")),
            _ => Json("""{"type":"error","error":{"type":"not_found_error","message":"no"}}""", HttpStatusCode.NotFound)
        });
        var client = new ClaudeClient(new Factory(handler));

        var result = await client.RunSkillAsync(new ClaudeSkillRequest(Key, "claude-sonnet-5-5", "skill_1", "v1", "system", "prompt", "low", 8000, TimeSpan.FromSeconds(30)), CancellationToken.None);

        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(["beden-tablosu.jpg", "beden-tablosu.json"], result.Files.Select(file => file.FileName));
        Assert.Equal(1500, result.Usage.InputTokens);
        Assert.Equal(400, result.Usage.OutputTokens);
        var call = handler.Requests.First(request => request.Path == "/v1/messages");
        Assert.Equal(Key, call.ApiKey);
        Assert.Contains(ClaudeClient.CodeExecutionBeta, call.Beta, StringComparison.Ordinal);
        Assert.Contains("\"skill_id\":\"skill_1\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"custom\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("claude-sonnet-5-5", call.Body, StringComparison.Ordinal);
        Assert.Contains("code_execution", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_key_becomes_a_turkish_message_without_the_key()
    {
        var handler = new FakeHandler(_ => Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized));
        var client = new ClaudeClient(new Factory(handler));

        var failure = await Assert.ThrowsAsync<ClaudeException>(() => client.TestAsync(Key, "claude-sonnet-5-5", CancellationToken.None));

        Assert.Equal("AI_KEY_INVALID", failure.Code);
        Assert.DoesNotContain(Key, failure.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    // D4: the message is billed before the file lookups; a failed lookup keeps its usage for the cost log.
    [Fact]
    public async Task A_failed_file_lookup_after_the_billed_message_keeps_the_usage()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath == "/v1/messages"
            ? Json(SkillResponse)
            : Json("""{"type":"error","error":{"type":"api_error","message":"boom"}}""", HttpStatusCode.InternalServerError));
        var client = new ClaudeClient(new Factory(handler));

        var failure = await Assert.ThrowsAsync<ClaudeException>(() => client.RunSkillAsync(new ClaudeSkillRequest(Key, "claude-sonnet-5-5", "skill_1", "v1", "system", "prompt", "low", 8000, TimeSpan.FromSeconds(30)), CancellationToken.None));

        Assert.Equal("AI_UNAVAILABLE", failure.Code);
        Assert.Equal((1500L, 400L), (failure.Usage?.InputTokens, failure.Usage?.OutputTokens));
    }

    [Fact]
    public async Task A_call_the_user_abandoned_is_cancelled_not_a_timeout()
    {
        var handler = new FakeHandler(_ => Json(SkillResponse));
        var client = new ClaudeClient(new Factory(handler));
        using var abandoned = new CancellationTokenSource();
        await abandoned.CancelAsync();

        var failure = await Assert.ThrowsAsync<ClaudeException>(() => client.TestAsync(Key, "claude-sonnet-5-5", abandoned.Token));

        Assert.Equal(ClaudeClient.CancelledCode, failure.Code);
    }

    [Fact]
    public async Task A_download_larger_than_allowed_is_refused()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) });
        var client = new ClaudeClient(new Factory(handler));

        Assert.Equal(2048, (await client.DownloadFileAsync(Key, "file_img", 4096, CancellationToken.None)).Length);
        var failure = await Assert.ThrowsAsync<ClaudeException>(() => client.DownloadFileAsync(Key, "file_img", 1024, CancellationToken.None));
        Assert.Equal("AI_FILE_TOO_LARGE", failure.Code);
    }

    [Fact]
    public async Task The_product_picture_goes_before_the_prompt()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath == "/v1/messages" ? Json(SkillResponse) : Json(Metadata("file_img", "beden-tablosu.png")));
        var client = new ClaudeClient(new Factory(handler));

        await client.RunSkillAsync(new ClaudeSkillRequest(Key, "claude-sonnet-5-5", "skill_1", "v1", "system", "prompt", "low", 8000, TimeSpan.FromSeconds(30),
            [new ClaudeImage(null, null, "https://cdn.example.invalid/bluz.jpg"), new ClaudeImage([1, 2, 3], "image/png", null)]), CancellationToken.None);

        var body = handler.Requests.First(request => request.Path == "/v1/messages").Body;
        Assert.Contains("\"type\":\"url\"", body, StringComparison.Ordinal);
        Assert.Contains("https://cdn.example.invalid/bluz.jpg", body, StringComparison.Ordinal);
        Assert.Contains("\"media_type\":\"image/png\"", body, StringComparison.Ordinal);
        Assert.Contains("\"data\":\"AQID\"", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("bluz.jpg", StringComparison.Ordinal) < body.IndexOf("\"text\":\"prompt\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_skill_with_the_same_name_is_uploaded_as_a_new_version_of_it()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/skills/skill_1/versions" => Json("""{"id":"skver_2","type":"skill_version","skill_id":"skill_1","version":"1762540000000000","name":"beden-tablosu","description":"x","directory":"beden-tablosu","created_at":"2026-11-07T19:00:00Z"}"""),
            _ => Json("""{"type":"error","error":{"type":"not_found_error","message":"no"}}""", HttpStatusCode.NotFound)
        });
        var client = new ClaudeClient(new Factory(handler));

        var upload = await client.UploadSkillAsync(Key, "Ravencia Beden Tablosu", [new ClaudeSkillFile("beden-tablosu/SKILL.md", Encoding.UTF8.GetBytes("---\nname: beden-tablosu\n---"))], "skill_1", CancellationToken.None);

        Assert.Equal(new ClaudeSkillUpload("skill_1", "skver_2"), upload);
        var call = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Contains("beden-tablosu/SKILL.md", call.Body, StringComparison.Ordinal);
    }
}
