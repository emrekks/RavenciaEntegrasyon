using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Ai;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.SizeCharts;

public sealed record SizeChartMeasurementsView(IReadOnlyList<string> Columns, IReadOnlyList<SizeChartMeasurementRowView> Rows);
public sealed record SizeChartMeasurementRowView(string Size, IReadOnlyList<decimal?> Values);
public sealed record SizeChartView(IReadOnlyList<string> Sizes, string? SizeOptionLabel, SizeChartMeasurementsView? Measurements, string? SizeChartUrl, bool AiReady, string? AiIssue);
public sealed record SizeChartGenerated(string Url, Guid FileAssetId, long? InputTokens, long? OutputTokens, decimal? CostUsd);

// A failure the editor shows in the size chart dialog (Turkish), with its HTTP status.
public sealed class SizeChartException(string code, string message, int status) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

// "Beden Tablosu Oluştur": the product's sizes and last measurements, the Claude call that draws the chart with the
// beden-tablosu skill, and which picture of the card is its size chart. The generated 1200×1800 PNG is stored as an ordinary
// product picture file; it joins the card only with the editor's Kaydet (no marketplace send here).
public sealed class SizeChartService(AppDbContext db, ClaudeService claude, IPrivateFileStorage storage, TimeProvider timeProvider)
{
    public const int Width = 1200;
    public const int Height = 1800;
    private const long MaxImageBytes = 6 * 1024 * 1024;
    private const long MaxProductImageBytes = 5 * 1024 * 1024; // the API's limit for one picture
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(4);

    private sealed class StoredMeasurements
    {
        [JsonPropertyName("columns")] public List<string> Columns { get; set; } = [];
        [JsonPropertyName("rows")] public List<StoredRow> Rows { get; set; } = [];
    }

    private sealed class StoredRow
    {
        [JsonPropertyName("size")] public string Size { get; set; } = "";
        [JsonPropertyName("values")] public List<string> Values { get; set; } = [];
    }

    private sealed class Echo
    {
        [JsonPropertyName("unit")] public string? Unit { get; set; }
        [JsonPropertyName("columns")] public List<string>? Columns { get; set; }
        [JsonPropertyName("rows")] public List<StoredRow>? Rows { get; set; }
    }

    public async Task<SizeChartView?> GetAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        if (!await db.Products.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken)) return null;
        var (label, sizes) = await SizesAsync(tenantId, productId, cancellationToken);
        var chart = await db.ProductSizeCharts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId, cancellationToken);
        var stored = Read(chart?.MeasurementsJson);
        var measurements = stored is null ? null : new SizeChartMeasurementsView(stored.Columns,
            stored.Rows.Select(row => new SizeChartMeasurementRowView(row.Size, row.Values.Select(value => decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), out var number) ? number : (decimal?)null).ToList())).ToList());
        var url = chart?.FileAssetId is Guid assetId ? await UrlAsync(tenantId, assetId, cancellationToken) : null;
        var credentials = await claude.CredentialsOrNullAsync(tenantId, cancellationToken);
        var issue = credentials is null ? "Claude API anahtarı kayıtlı değil. Ayarlar › Yapay Zekâ bölümünden hesap sahibi ya da yönetici girmeli." : SkillIssue(credentials);
        return new SizeChartView(sizes, label, measurements, url, issue is null, issue);
    }

    internal static string? SkillIssue(AiCredentials credentials) =>
        credentials.Skills.ContainsKey(AiSkillCatalog.SizeChartKey) ? null
            : "Beden tablosu becerisi yüklenmedi. Ayarlar › Yapay Zekâ › Beceriler bölümünde \"Beceriyi Yükle\" ile beceri dosyasını (.skill) seçip yükleyin.";

    // The latest upload decides how the chart is drawn: a skill file the owner uploaded (spec.json, 1200×1800 PNG) or,
    // when none was, the copy bundled with the panel (chart.json, 1200×1800 JPEG).
    internal static bool UsesSkillFile(AiCredentials credentials) =>
        credentials.Skills.GetValueOrDefault(AiSkillCatalog.SizeChartKey)?.Source == AiUploadedSkillView.File;

    public async Task<SizeChartGenerated> GenerateAsync(Guid tenantId, Guid userId, Guid productId, SizeChartInput input, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken)
            ?? throw new SizeChartException("RESOURCE_NOT_FOUND", "Ürün bulunamadı.", 404);
        var (label, _) = await SizesAsync(tenantId, productId, cancellationToken);
        if (label is null) throw new SizeChartException("SIZE_CHART_NO_SIZES", "Ürünün Beden seçeneği yok; beden tablosu için önce bedenleri ekleyip kaydedin.", 422);
        var (table, error) = SizeChartRules.Validate(input);
        if (table is null) throw new SizeChartException("SIZE_CHART_INVALID", error!, 422);

        // The measurements are remembered before the call, so a failed or abandoned try keeps them for the next one.
        var chart = await ChartRowAsync(tenantId, productId, cancellationToken);
        chart.MeasurementsJson = JsonSerializer.Serialize(new StoredMeasurements { Columns = [.. table.Columns], Rows = table.Rows.Select(row => new StoredRow { Size = row.Size, Values = [.. row.Values] }).ToList() });
        chart.UpdatedAt = timeProvider.GetUtcNow();
        chart.UpdatedByUserId = userId;
        await db.SaveChangesAsync(cancellationToken);

        AiCredentials credentials;
        AiSkillRun run;
        SizeChartSpecRequest? spec = null;
        try
        {
            credentials = await claude.RequireCredentialsAsync(tenantId, cancellationToken);
            if (SkillIssue(credentials) is { } skillIssue) throw new SizeChartException("AI_SKILL_MISSING", skillIssue, 409);
            var context = new AiCallContext(tenantId, userId, "SIZE_CHART", "Product", productId, product.Title);
            if (UsesSkillFile(credentials))
            {
                var (sizes, specError) = SizeChartSpec.Map(table);
                if (sizes is null) throw new SizeChartException("SIZE_CHART_INVALID", specError!, 422);
                spec = new SizeChartSpecRequest(SizeChartSpec.ProductName(product.Title), sizes);
                // The icon (sleeve, neckline, hem) is read from the product's own picture, never guessed without one.
                var image = await ProductImageAsync(tenantId, productId, chart.FileAssetId, cancellationToken)
                    ?? throw new SizeChartException("SIZE_CHART_NO_IMAGE", "Ürünün görseli yok: tablodaki ikon ürün görselinden seçilir. Önce bir ürün görseli ekleyip kaydedin.", 422);
                run = await claude.RunSkillAsync(context, credentials, AiSkillCatalog.SizeChartKey,
                    SpecSystemPrompt, SpecPrompt(spec), "low", 16000, CallTimeout, cancellationToken, [image]);
            }
            else
            {
                run = await claude.RunSkillAsync(context, credentials, AiSkillCatalog.SizeChartKey,
                    SystemPrompt, Prompt(product.Title, table), "low", 8000, CallTimeout, cancellationToken);
            }
        }
        catch (ClaudeException exception) { throw new SizeChartException(exception.Code, exception.Message, exception.Status); }

        try
        {
            var bytes = spec is not null
                ? await PngFromRunAsync(credentials, run.Result, spec, cancellationToken)
                : await JpegFromRunAsync(credentials, run.Result, table, cancellationToken);
            var asset = await StoreAsync(tenantId, productId, bytes, spec is not null, cancellationToken);
            chart.LastGeneratedAssetId = asset.Id;
            chart.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            var usage = run.Result.Usage;
            return new SizeChartGenerated(CatalogMediaDisplay.Url(asset.Id, asset.Classification, asset.RelativePath), asset.Id, usage.InputTokens, usage.OutputTokens, ClaudePricing.CostUsd(run.Model, usage));
        }
        catch (SizeChartException exception)
        {
            await claude.MarkFailedAsync(run.LogId, exception.Message, CancellationToken.None);
            throw;
        }
        catch (ClaudeException exception)
        {
            await claude.MarkFailedAsync(run.LogId, exception.Message, CancellationToken.None, exception.Code == ClaudeClient.CancelledCode ? ClaudeService.CancelledStatus : "FAILED");
            throw new SizeChartException(exception.Code, exception.Message, exception.Status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // D4: the billed call stays on its row, marked as abandoned by the user.
            await claude.MarkFailedAsync(run.LogId, "İstek iptal edildi; görsel eklenmedi.", CancellationToken.None, ClaudeService.CancelledStatus);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // D4: storing the image or the row failed after the billed call: the usage row says so.
            await claude.MarkFailedAsync(run.LogId, $"Görsel kaydedilemedi: {exception.Message}", CancellationToken.None);
            throw;
        }
    }

    // Which picture of the card is its size chart (null clears it). The picture must be one of the card's active
    // pictures or the image generated for it last; the editor sends this after its picture writes on Kaydet.
    public async Task<string?> SetAsync(Guid tenantId, Guid userId, Guid productId, string? url, CancellationToken cancellationToken)
    {
        if (!await db.Products.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken))
            throw new SizeChartException("RESOURCE_NOT_FOUND", "Ürün bulunamadı.", 404);
        var chart = await ChartRowAsync(tenantId, productId, cancellationToken);
        Guid? assetId = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            assetId = await ResolveAssetAsync(tenantId, url, cancellationToken)
                ?? throw new SizeChartException("PRODUCT_MEDIA_NOT_FOUND", "Beden tablosu olarak işaretlenen görsel bulunamadı.", 404);
            var onProduct = await db.ProductMedia.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.FileAssetId == assetId && x.Status == "ACTIVE", cancellationToken);
            if (!onProduct && !(await GeneratedForProductAsync(db, tenantId, productId, [assetId.Value], cancellationToken)).Contains(assetId.Value))
                throw new SizeChartException("SIZE_CHART_NOT_ON_PRODUCT", "Beden tablosu olarak işaretlenen görsel bu ürünün görsellerinde değil.", 422);
        }
        if (chart.FileAssetId != assetId)
        {
            chart.FileAssetId = assetId;
            chart.UpdatedAt = timeProvider.GetUtcNow();
            chart.UpdatedByUserId = userId;
            await CatalogLocalEdits.MarkMediaEditedAsync(db, tenantId, [productId], timeProvider.GetUtcNow(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        return assetId is Guid id ? await UrlAsync(tenantId, id, cancellationToken) : null;
    }

    // O8: which of the given assets are size charts generated for this card, any of its tries and not only the newest
    // (Kullan, then Yeniden Oluştur without using it, then Kaydet keeps the used one). A generated file is named after
    // its card (StoreAsync); the newest is also remembered on the chart row (a file shared by an identical image keeps
    // its first name).
    public static async Task<HashSet<Guid>> GeneratedForProductAsync(AppDbContext db, Guid tenantId, Guid productId, IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0) return [];
        string[] fileNames = [GeneratedFileName(productId), GeneratedFileName(productId, png: true)];
        var named = await db.FileAssets.AsNoTracking()
            .Where(x => x.TenantId == tenantId && assetIds.Contains(x.Id) && x.Classification == "PRODUCT_MEDIA" && fileNames.Contains(x.OriginalNameSafe))
            .Select(x => x.Id).ToListAsync(cancellationToken);
        var newest = await db.ProductSizeCharts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == productId && x.LastGeneratedAssetId != null && assetIds.Contains(x.LastGeneratedAssetId.Value))
            .Select(x => x.LastGeneratedAssetId!.Value).ToListAsync(cancellationToken);
        return [.. named, .. newest];
    }

    internal static string GeneratedFileName(Guid productId, bool png = false) => $"beden-tablosu-{productId:N}.{(png ? "png" : "jpg")}";

    // The bundled skill (chart.json → JPEG): the seller's table drawn exactly as given.
    internal const string SystemPrompt =
        "You make product size chart images for the Ravencia seller panel with the beden-tablosu skill. "
        + "Follow the skill's SKILL.md exactly: write the given JSON to chart.json unchanged and run scripts/draw_size_chart.py once. "
        + "The sizes, column names and values are the seller's own measurements: copy them character for character; "
        + "never invent, round, convert, reorder or correct anything. Do not draw the chart any other way. "
        + "When the script prints OK, reply OK. If it fails, reply with its error and do not retry with changed data.";

    internal static string Prompt(string productName, SizeChartTable table)
    {
        var json = JsonSerializer.Serialize(new
        {
            productName = productName.Length <= 120 ? productName : productName[..120],
            unit = "cm",
            columns = table.Columns,
            rows = table.Rows.Select(row => new { size = row.Size, values = row.Values })
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return $"Bu ürün için beden tablosu görselini oluştur. chart.json içeriği tam olarak şu olsun:\n\n{json}";
    }

    // An uploaded skill file (spec.json → PNG). The panel's rules take precedence over its SKILL.md: the seller's numbers
    // and the product name copied exactly, only the icon chosen from the picture, 1200×1800 (scale 1) only.
    internal const string SpecSystemPrompt =
        "You make product size chart images for the Ravencia seller panel with the beden-tablosu skill. "
        + "Follow the skill's SKILL.md; these panel rules take precedence over it.\n"
        + "1. The attached picture is the product. Choose only the icon fields from it as SKILL.md says: sleeve, neckline and hem always; "
        + "motif, studs or placket only when clearly visible; never color.\n"
        + "2. spec.json is exactly the given JSON plus the icon object. Copy product_name, sizes, every number and extra_notes character for character. "
        + "Never add kilo, ease, height_assumed, age_offset_kg, kilo_band_width, background or color; never invent, round, convert or reorder a value. "
        + "gogus is already one side: never halve or double it.\n"
        + "3. Render exactly once per try with the skill's render script, writing /mnt/user-data/outputs/beden-tablosu.png, always with --scale 1: "
        + "the output must be a 1200x1800 px PNG. Never pass --scale 2 and never produce 2400x3600 or any other size.\n"
        + "4. Copy the spec.json you rendered from, unchanged, to /mnt/user-data/outputs/beden-tablosu-spec.json.\n"
        + "5. If the icon shows the wrong garment category, change only the icon and render again; never change a measurement or the name.\n"
        + "6. The container has no network: never install packages. If the script fails, reply with 'HATA: ' and its error and stop.\n"
        + "7. When the PNG and the spec copy are written, reply OK.";

    internal static string SpecPrompt(SizeChartSpecRequest spec)
    {
        var json = SizeChartSpec.Json(spec.ProductName, spec.Sizes).ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true });
        return "Bu ürün için 1200×1800 PNG beden tablosu görselini oluştur (--scale 1). Ekteki görsel ürünün kendisi.\n\n"
            + $"spec.json bu JSON'a yalnız icon eklenerek yazılsın:\n\n{json}";
    }

    // The picture Claude reads the garment from: the card's first own picture (product-level first), not a size chart.
    private async Task<ClaudeImage?> ProductImageAsync(Guid tenantId, Guid productId, Guid? chartAssetId, CancellationToken cancellationToken)
    {
        var candidates = await (from media in db.ProductMedia.AsNoTracking()
                                join asset in db.FileAssets.AsNoTracking() on media.FileAssetId equals asset.Id
                                where media.TenantId == tenantId && media.ProductId == productId && media.Status == "ACTIVE" && asset.TenantId == tenantId && asset.Status == "ACTIVE"
                                      && (chartAssetId == null || asset.Id != chartAssetId) && (asset.OriginalNameSafe == null || !asset.OriginalNameSafe.StartsWith("beden-tablosu-"))
                                orderby media.VariantId == null ? 0 : 1, media.SortOrder
                                select new { asset.Classification, asset.RelativePath, asset.MimeType, asset.SizeBytes }).Take(8).ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            if (candidate.Classification == "PRODUCT_MEDIA" && candidate.MimeType is "image/jpeg" or "image/png" && candidate.SizeBytes is > 0 and <= MaxProductImageBytes)
            {
                await using var stream = await storage.OpenReadAsync(tenantId, candidate.RelativePath, cancellationToken);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                return new ClaudeImage(buffer.ToArray(), candidate.MimeType, null);
            }
            if (candidate.Classification == "PRODUCT_MEDIA_URL" && Uri.TryCreate(candidate.RelativePath, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
                return new ClaudeImage(null, null, url.AbsoluteUri);
        }
        return null;
    }

    private static void CheckStop(ClaudeSkillResult result)
    {
        switch (result.StopReason)
        {
            case "refusal":
                throw new SizeChartException("AI_REFUSED", $"Claude bu isteği yanıtlamadı{(string.IsNullOrWhiteSpace(result.RefusalExplanation) ? "." : $": {result.RefusalExplanation}")}", 502);
            case "max_tokens":
                throw new SizeChartException("AI_TRUNCATED", "Claude'un yanıtı yarıda kesildi; görsel eklenmedi. Yeniden Oluştur ile tekrar deneyin.", 502);
            case "pause_turn":
                throw new SizeChartException("AI_UNFINISHED", "Claude görseli zamanında bitiremedi; görsel eklenmedi. Yeniden Oluştur ile tekrar deneyin.", 502);
            case "end_turn" or "stop_sequence":
                return;
            default:
                throw new SizeChartException("AI_UNEXPECTED_STOP", $"Claude beklenmeyen biçimde durdu ({result.StopReason}); görsel eklenmedi.", 502);
        }
    }

    // A skill whose renderer needs a browser (Playwright/Chromium) cannot run in Claude's offline code container.
    private static SizeChartException NoImage(ClaudeSkillResult result) =>
        result.Text is { } text && (text.Contains("playwright", StringComparison.OrdinalIgnoreCase) || text.Contains("chromium", StringComparison.OrdinalIgnoreCase))
            ? new("AI_RENDERER_MISSING", $"Beceri dosyasındaki çizici (Playwright/Chromium) Claude'un kod ortamında çalışmadı; görsel eklenmedi. Yanıt: {Short(text)}", 502)
            : new("AI_NO_IMAGE", $"Claude görsel dosyası üretmedi; görsel eklenmedi.{(string.IsNullOrWhiteSpace(result.Text) ? "" : $" Yanıt: {Short(result.Text)}")}", 502);

    private async Task<byte[]> JpegFromRunAsync(AiCredentials credentials, ClaudeSkillResult result, SizeChartTable table, CancellationToken cancellationToken)
    {
        CheckStop(result);
        var image = result.Files.LastOrDefault(file => file.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || file.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
        var echo = result.Files.LastOrDefault(file => file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (image is null || echo is null) throw NoImage(result);

        var echoBytes = await claude.DownloadFileAsync(credentials, echo.FileId, 256 * 1024, cancellationToken);
        if (!EchoMatches(echoBytes, table))
            throw new SizeChartException("AI_VALUES_CHANGED", "Çizilen tablodaki ölçüler girilenlerle birebir aynı değil; görsel eklenmedi. Yeniden Oluştur ile tekrar deneyin.", 502);
        var bytes = await claude.DownloadFileAsync(credentials, image.FileId, MaxImageBytes, cancellationToken);
        CheckSize(JpegSize(bytes), "JPEG");
        return bytes;
    }

    private async Task<byte[]> PngFromRunAsync(AiCredentials credentials, ClaudeSkillResult result, SizeChartSpecRequest request, CancellationToken cancellationToken)
    {
        CheckStop(result);
        var image = result.Files.LastOrDefault(file => file.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        var spec = result.Files.LastOrDefault(file => file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (image is null || spec is null) throw NoImage(result);

        var specBytes = await claude.DownloadFileAsync(credentials, spec.FileId, 256 * 1024, cancellationToken);
        if (SizeChartSpec.Check(specBytes, request.ProductName, request.Sizes) is { } difference)
            throw new SizeChartException("AI_VALUES_CHANGED", $"Çizilen tablo girilen ölçülerle birebir aynı değil ({difference}); görsel eklenmedi. Yeniden Oluştur ile tekrar deneyin.", 502);
        var bytes = await claude.DownloadFileAsync(credentials, image.FileId, MaxImageBytes, cancellationToken);
        CheckImage(bytes);
        return bytes;
    }

    // The script writes what it drew; it must be exactly what was asked (sizes, columns, values, order).
    internal static bool EchoMatches(byte[] echoJson, SizeChartTable table)
    {
        Echo? echo;
        try { echo = JsonSerializer.Deserialize<Echo>(echoJson); }
        catch (JsonException) { return false; }
        if (echo?.Columns is null || echo.Rows is null || echo.Unit != "cm") return false;
        return echo.Columns.SequenceEqual(table.Columns, StringComparer.Ordinal)
            && echo.Rows.Count == table.Rows.Count
            && echo.Rows.Zip(table.Rows).All(pair => pair.First.Size == pair.Second.Size && pair.First.Values.SequenceEqual(pair.Second.Values, StringComparer.Ordinal));
    }

    // Only a complete PNG of exactly 1200×1800 is kept; anything else is refused before it is stored.
    internal static void CheckImage(byte[] bytes) => CheckSize(PngSize(bytes), "PNG");

    private static void CheckSize((int Width, int Height)? size, string format)
    {
        if (size is not { } found)
            throw new SizeChartException("AI_IMAGE_INVALID", $"Claude'un ürettiği dosya tam bir {format} görseli değil; görsel eklenmedi. Tekrar deneyin.", 502);
        if (found is not (Width, Height))
            throw new SizeChartException("SIZE_CHART_WRONG_SIZE", $"Beden tablosu {Width}×{Height} olmalı; üretilen görsel {found.Width}×{found.Height} geldi, tekrar deneyin.", 502);
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] PngEnd = [0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    // Width and height of a complete PNG (signature, IHDR first, IEND last); null for anything else.
    internal static (int Width, int Height)? PngSize(byte[] bytes)
    {
        if (bytes.Length < 8 + 25 + 12 || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature) || !bytes.AsSpan(bytes.Length - 12).SequenceEqual(PngEnd)) return null;
        var header = bytes.AsSpan(8);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header) != 13 || !header.Slice(4, 4).SequenceEqual("IHDR"u8)) return null;
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        return width is 0 or > int.MaxValue || height is 0 or > int.MaxValue ? null : ((int)width, (int)height);
    }

    // Width and height of a complete baseline or progressive JPEG (SOI … SOFn … EOI); null for anything else.
    internal static (int Width, int Height)? JpegSize(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[^2] != 0xFF || bytes[^1] != 0xD9) return null;
        var index = 2;
        while (index + 4 <= bytes.Length)
        {
            if (bytes[index] != 0xFF) return null;
            var marker = bytes[index + 1];
            if (marker == 0xFF) { index++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { index += 2; continue; }
            var length = (bytes[index + 2] << 8) | bytes[index + 3];
            if (length < 2 || index + 2 + length > bytes.Length) return null;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 7) return null;
                var height = (bytes[index + 5] << 8) | bytes[index + 6];
                var width = (bytes[index + 7] << 8) | bytes[index + 8];
                return (width, height);
            }
            if (marker == 0xDA) return null;
            index += 2 + length;
        }
        return null;
    }

    private async Task<FileAsset> StoreAsync(Guid tenantId, Guid productId, byte[] bytes, bool png, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var existing = await db.FileAssets
            .Where(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA" && x.Sha256 == hash && x.ArchivedAt == null)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            existing.Status = "ACTIVE";
            return existing;
        }
        var id = Guid.CreateVersion7();
        var mime = png ? "image/png" : "image/jpeg";
        using var content = new MemoryStream(bytes);
        var stored = await storage.SaveAsync(tenantId, $"{id:N}.{(png ? "png" : "jpg")}", mime, content, MaxImageBytes, cancellationToken);
        var asset = new FileAsset { Id = id, TenantId = tenantId, Classification = "PRODUCT_MEDIA", RelativePath = stored, OriginalNameSafe = GeneratedFileName(productId, png), MimeType = mime, SizeBytes = bytes.Length, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
        db.FileAssets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);
        return asset;
    }

    private async Task<ProductSizeChart> ChartRowAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var chart = await db.ProductSizeCharts.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId, cancellationToken);
        if (chart is not null) return chart;
        chart = new ProductSizeChart { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = productId, UpdatedAt = timeProvider.GetUtcNow() };
        db.ProductSizeCharts.Add(chart);
        return chart;
    }

    // The card's Beden option and its values in the panel's order (null label: no Beden option).
    private async Task<(string? Label, IReadOnlyList<string> Sizes)> SizesAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var options = await db.ProductOptions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var option = options.FirstOrDefault(x => SizeChartRules.IsSizeOption(x.Label));
        if (option is null) return (null, []);
        var sizes = await db.ProductOptionValues.AsNoTracking().Where(x => x.TenantId == tenantId && x.OptionId == option.Id).OrderBy(x => x.SortOrder).Select(x => x.Label).ToListAsync(cancellationToken);
        return (option.Label, sizes);
    }

    private async Task<Guid?> ResolveAssetAsync(Guid tenantId, string url, CancellationToken cancellationToken)
    {
        var value = url.Trim();
        var parts = value.Split('/');
        if (parts.Length == 7 && parts[0].Length == 0 && parts[1] == "api" && parts[2] == "v1" && parts[3] == "files" && parts[4] == "product-media" && parts[6] == "content" && Guid.TryParseExact(parts[5], "D", out var storedId))
            return await db.FileAssets.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == storedId && x.Status == "ACTIVE" && x.Classification == "PRODUCT_MEDIA", cancellationToken) ? storedId : null;
        return await db.FileAssets.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.RelativePath == value && x.Classification == "PRODUCT_MEDIA_URL" && x.Status == "ACTIVE")
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<string?> UrlAsync(Guid tenantId, Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == assetId && x.Status == "ACTIVE", cancellationToken);
        return asset is null ? null : CatalogMediaDisplay.Url(asset.Id, asset.Classification, asset.RelativePath);
    }

    private static StoredMeasurements? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StoredMeasurements>(json); }
        catch (JsonException) { return null; }
    }

    private static string Short(string value) => value.Length <= 200 ? value : value[..200] + "…";
}
