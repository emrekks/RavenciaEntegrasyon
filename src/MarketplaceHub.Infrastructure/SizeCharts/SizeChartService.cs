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
// beden-tablosu skill, and which picture of the card is its size chart. The generated JPEG is stored as an ordinary
// product picture file; it joins the card only with the editor's Kaydet (no marketplace send here).
public sealed class SizeChartService(AppDbContext db, ClaudeService claude, IPrivateFileStorage storage, TimeProvider timeProvider)
{
    public const int Width = 1200;
    public const int Height = 1800;
    private const long MaxImageBytes = 6 * 1024 * 1024;
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
        var issue = credentials is null ? "Claude API anahtarı kayıtlı değil. Ayarlar › Yapay Zekâ bölümünden hesap sahibi ya da yönetici girmeli."
            : !credentials.Skills.ContainsKey(AiSkillCatalog.SizeChartKey) ? "Beden tablosu becerisi yüklenmedi. Ayarlar › Yapay Zekâ bölümünde \"Beceriyi Yükle\" düğmesine basılmalı."
            : null;
        return new SizeChartView(sizes, label, measurements, url, issue is null, issue);
    }

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
        try
        {
            credentials = await claude.RequireCredentialsAsync(tenantId, cancellationToken);
            run = await claude.RunSkillAsync(new AiCallContext(tenantId, userId, "SIZE_CHART", "Product", productId, product.Title), credentials, AiSkillCatalog.SizeChartKey,
                SystemPrompt, Prompt(product.Title, table), "low", 8000, CallTimeout, cancellationToken);
        }
        catch (ClaudeException exception) { throw new SizeChartException(exception.Code, exception.Message, exception.Status); }

        try
        {
            var bytes = await ImageFromRunAsync(credentials, run.Result, table, cancellationToken);
            var asset = await StoreAsync(tenantId, productId, bytes, cancellationToken);
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
            await claude.MarkFailedAsync(run.LogId, exception.Message, CancellationToken.None);
            throw new SizeChartException(exception.Code, exception.Message, exception.Status);
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
            if (!onProduct && chart.LastGeneratedAssetId != assetId)
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

    // An image generated for any card of the tenant may be attached by the editor's picture save before it has rows.
    public static Task<bool> IsGeneratedAssetAsync(AppDbContext db, Guid tenantId, Guid assetId, CancellationToken cancellationToken) =>
        db.ProductSizeCharts.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.LastGeneratedAssetId == assetId, cancellationToken);

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

    private async Task<byte[]> ImageFromRunAsync(AiCredentials credentials, ClaudeSkillResult result, SizeChartTable table, CancellationToken cancellationToken)
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
                break;
            default:
                throw new SizeChartException("AI_UNEXPECTED_STOP", $"Claude beklenmeyen biçimde durdu ({result.StopReason}); görsel eklenmedi.", 502);
        }
        var image = result.Files.LastOrDefault(file => file.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || file.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
        var echo = result.Files.LastOrDefault(file => file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (image is null || echo is null)
            throw new SizeChartException("AI_NO_IMAGE", $"Claude görsel dosyası üretmedi; görsel eklenmedi.{(string.IsNullOrWhiteSpace(result.Text) ? "" : $" Yanıt: {Short(result.Text)}")}", 502);

        var echoBytes = await claude.DownloadFileAsync(credentials, echo.FileId, 256 * 1024, cancellationToken);
        if (!EchoMatches(echoBytes, table))
            throw new SizeChartException("AI_VALUES_CHANGED", "Çizilen tablodaki ölçüler girilenlerle birebir aynı değil; görsel eklenmedi. Yeniden Oluştur ile tekrar deneyin.", 502);
        var bytes = await claude.DownloadFileAsync(credentials, image.FileId, MaxImageBytes, cancellationToken);
        if (JpegSize(bytes) is not (Width, Height))
            throw new SizeChartException("AI_IMAGE_INVALID", $"Claude'un ürettiği dosya {Width}×{Height} tam bir JPEG değil; görsel eklenmedi.", 502);
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

    private async Task<FileAsset> StoreAsync(Guid tenantId, Guid productId, byte[] bytes, CancellationToken cancellationToken)
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
        using var content = new MemoryStream(bytes);
        var stored = await storage.SaveAsync(tenantId, $"{id:N}.jpg", "image/jpeg", content, MaxImageBytes, cancellationToken);
        var asset = new FileAsset { Id = id, TenantId = tenantId, Classification = "PRODUCT_MEDIA", RelativePath = stored, OriginalNameSafe = $"beden-tablosu-{productId:N}.jpg", MimeType = "image/jpeg", SizeBytes = bytes.Length, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
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
