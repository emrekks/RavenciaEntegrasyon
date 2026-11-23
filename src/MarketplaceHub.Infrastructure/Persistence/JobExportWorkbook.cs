using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;

namespace MarketplaceHub.Infrastructure.Persistence;

// İşlem Takibi › İş detayı › Excel: a job's list written row by row into a temporary file (never the whole list in
// memory), handed to the response as a stream that deletes the file when it is closed.
internal static class JobExportWorkbook
{
    public static async Task<Stream> WriteAsync(string sheetName, IReadOnlyList<string> header, IReadOnlyList<double> widths,
        IAsyncEnumerable<IReadOnlyList<object?>> rows, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"marketplacehub-export-{Guid.NewGuid():N}.xlsx");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
        try
        {
            await SimpleXlsxWriter.WriteStreamingAsync(file, sheetName, header, widths, rows, cancellationToken);
            await file.FlushAsync(cancellationToken);
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    // The job's type and its day (Turkey time): "price-inventory-sync-degisenler-20261119.xlsx".
    public static string FileName(IntegrationJob job, string list) =>
        $"{job.JobType.ToLowerInvariant().Replace('_', '-')}-{list}-{job.CreatedAt.ToOffset(TurkeyClock.Offset).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.xlsx";

    // Turkey time as the panel shows it.
    public static string Time(DateTimeOffset instant) => instant.ToOffset(TurkeyClock.Offset).ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    // A whole number of pieces is a whole-number cell; anything else keeps its decimals.
    public static object? Quantity(decimal? value) => value is { } number && number == decimal.Truncate(number) && Math.Abs(number) < long.MaxValue ? (long)number : value;
}

// The rows of Gidenler ve Değişenler's export: one row per value a line carried (stock, sale price, list price when it
// changed), so every number is a number cell. Nothing about a customer: the product, its codes, the store and the result.
internal static class JobChangesExportRows
{
    public static readonly IReadOnlyList<string> Header =
        ["Ürün", "Model Kodu", "Barkod", "Stok Kodu", "Varyant", "Alan", "Eski Değer", "Yeni Değer", "Fark", "Kaynak / İş Türü", "Pazaryeri", "Sonuç", "Zaman"];

    public static readonly IReadOnlyList<double> Widths = [40, 18, 18, 18, 18, 14, 12, 12, 10, 24, 22, 30, 20];

    private static readonly Dictionary<string, string> Results = new(StringComparer.Ordinal)
    {
        [PriceStockSendResults.Submitted] = "Gönderildi · Yanıt Bekleniyor",
        [PriceStockSendResults.Applied] = "Gönderildi · Kabul Edildi",
        [PriceStockSendResults.Unchanged] = "Zaten Güncel",
        [PriceStockSendResults.Rejected] = "Reddedildi",
        [PriceStockSendResults.Failed] = "Gönderilemedi",
        [PriceStockSendResults.Superseded] = "Yeni Sürüm Geldi",
        [PriceStockSendResults.Unknown] = "Belirsiz · Yeniden Denenecek",
        [JobChangeResults.Changed] = "Panelde Güncellendi"
    };

    private static readonly Dictionary<string, string> Triggers = new(StringComparer.Ordinal)
    {
        ["ORDER"] = "Sipariş",
        ["MANUAL_STOCK"] = "Elle Stok",
        ["PRICE_EDIT"] = "Fiyat Değişikliği",
        ["STOCK_IMPORT"] = "Aktarım",
        ["STOCK_CHANGE"] = "Stok Değişikliği",
        ["RECONCILE"] = "Uzlaştırma",
        ["PANEL_SEND"] = "Panelden Gönderim",
        ["AUTOMATIC"] = "Otomatik"
    };

    private static readonly Dictionary<string, string> Sources = new(StringComparer.Ordinal)
    {
        ["ORDER"] = "Sipariş",
        ["RETURN"] = "İade",
        ["MANUAL"] = "Elle",
        ["EDITOR"] = "Ürün Düzenleme",
        ["BULK_EDIT"] = "Toplu Düzenleme",
        ["PRICE_EDIT"] = "Fiyat Düzenleme",
        ["FILE_IMPORT"] = "Excel",
        ["MARKETPLACE_IMPORT"] = "Pazaryeri Çekimi",
        ["PUBLISH"] = "Toplu Yayın",
        ["CLASSIC_IMPORT"] = "İçe Aktarım",
        ["QUICK_UPDATE"] = "Hızlı Güncelle",
        ["PLATFORM_UPDATE"] = "Platformlarda Güncelle"
    };

    public static IEnumerable<IReadOnlyList<object?>> Rows(JobChangeLineView line, string jobType)
    {
        var source = line.Result == JobChangeResults.Changed ? Sources.GetValueOrDefault(line.Source, line.Source) : Triggers.GetValueOrDefault(line.Source, line.Source);
        if (string.IsNullOrWhiteSpace(source)) source = jobType;
        var result = Results.GetValueOrDefault(line.Result, line.Result);
        var refused = line.Result is PriceStockSendResults.Rejected or PriceStockSendResults.Failed or PriceStockSendResults.Unknown;
        var reason = refused ? line.ErrorMessage ?? line.ErrorCode : null;
        if (!string.IsNullOrWhiteSpace(reason)) result = $"{result}: {reason}";
        var market = line.StoreName ?? line.PlatformCode;
        var time = JobExportWorkbook.Time(line.OccurredAt);
        object?[] Row(string field, object? before, object? after, object? difference) =>
            [line.ProductTitle, line.ModelCode, line.Barcode, line.StockCode, line.OptionText, field, before, after, difference, source, market, result, time];

        var any = false;
        if (line.Kind != JobChangeLineKinds.Price && (line.PreviousQuantity is not null || line.NewQuantity is not null))
        {
            any = true;
            yield return Row("Stok", JobExportWorkbook.Quantity(line.PreviousQuantity), JobExportWorkbook.Quantity(line.NewQuantity), JobExportWorkbook.Quantity(line.NewQuantity - line.PreviousQuantity));
        }
        if (line.Kind != JobChangeLineKinds.Stock && (line.PreviousSalePrice is not null || line.NewSalePrice is not null))
        {
            any = true;
            yield return Row("Satış Fiyatı", line.PreviousSalePrice, line.NewSalePrice, line.NewSalePrice - line.PreviousSalePrice);
        }
        // The list price only when it moved, as the panel shows it.
        if (line.Kind != JobChangeLineKinds.Stock && line.NewListPrice is not null && line.PreviousListPrice != line.NewListPrice)
        {
            any = true;
            yield return Row("Liste Fiyatı", line.PreviousListPrice, line.NewListPrice, line.NewListPrice - line.PreviousListPrice);
        }
        if (!any) yield return Row(line.Kind switch { JobChangeLineKinds.Stock => "Stok", JobChangeLineKinds.Price => "Fiyat", _ => "Stok ve Fiyat" }, null, null, null);
    }
}
