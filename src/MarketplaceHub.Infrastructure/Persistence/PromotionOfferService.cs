using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The latest promotion offer file of each kind per connection, and what each offered price leaves. The commission at a
// price the file does not price itself (an advantage star, a flash price) is the band of the commission tariff in force
// at that time; without one it stays unknown.
public sealed class PromotionOfferService(AppDbContext db, TimeProvider timeProvider) : IPromotionOfferService
{
    public const long MaximumFileBytes = 10 * 1024 * 1024;
    private const long MaximumExpandedBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ServiceResult<PromotionOfferUploadResult>> UploadAsync(Guid tenantId, Guid? userId, Guid connectionId, string fileName, Stream content, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return ServiceResult<PromotionOfferUploadResult>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        if (!CommissionTariffPlatforms.Supports(connection.PlatformCode))
            return ServiceResult<PromotionOfferUploadResult>.Fail("PROMOTION_OFFER_PLATFORM_UNSUPPORTED", "Promosyon dosyası bu pazaryeri için yüklenemez.", 422);
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length is 0 or > MaximumFileBytes)
            return ServiceResult<PromotionOfferUploadResult>.Fail("PROMOTION_OFFER_FILE_SIZE", "Dosya boş olamaz ve en fazla 10 MiB olabilir.", 413);
        ParsedPromotionOfferFile parsed;
        try
        {
            buffer.Position = 0;
            parsed = PromotionOfferFileParser.Parse(ImportFileReader.ReadXlsxTable(buffer, MaximumExpandedBytes));
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
        {
            return ServiceResult<PromotionOfferUploadResult>.Fail("PROMOTION_OFFER_FILE_INVALID", exception is InvalidDataException ? exception.Message : "Dosya okunamadı; Trendyol'dan indirilen .xlsx dosyasını yükleyin.", 422);
        }

        var now = timeProvider.GetUtcNow();
        var safeName = Path.GetFileName(fileName);
        if (safeName.Length > 260) safeName = safeName[..260];
        // A new file of a kind replaces the one before it: an offer is weekly and only the latest is worked on.
        var previous = await db.PromotionOfferFiles.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Kind == parsed.Kind).ToListAsync(cancellationToken);
        db.PromotionOfferFiles.RemoveRange(previous);
        var file = new PromotionOfferFile
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            PlatformCode = connection.PlatformCode.ToUpperInvariant(),
            Kind = parsed.Kind,
            FileName = safeName,
            FileHash = Convert.ToHexString(SHA256.HashData(buffer.ToArray())),
            PeriodText = parsed.PeriodText,
            UploadedAt = now,
            UploadedBy = userId,
            RowCount = parsed.Rows.Count,
            RowsJson = parsed.Kind switch
            {
                PromotionOfferKinds.Plus => JsonSerializer.Serialize(parsed.Rows.Cast<PlusOfferRow>().ToList(), Json),
                PromotionOfferKinds.Advantage => JsonSerializer.Serialize(parsed.Rows.Cast<AdvantageOfferRow>().ToList(), Json),
                _ => JsonSerializer.Serialize(parsed.Rows.Cast<FlashOfferRow>().ToList(), Json)
            }
        };
        db.PromotionOfferFiles.Add(file);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "PROMOTION_OFFER_UPLOADED", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = $"{parsed.Kind} {safeName}: {parsed.Rows.Count} satır", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<PromotionOfferUploadResult>.Ok(new(await SummaryAsync(file, cancellationToken), parsed.SkippedRows, parsed.Warnings));
    }

    public async Task<ServiceResult<IReadOnlyList<PromotionOfferSummary>>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (await ConnectionAsync(tenantId, connectionId, cancellationToken) is null) return ServiceResult<IReadOnlyList<PromotionOfferSummary>>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        var files = await db.PromotionOfferFiles.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).OrderByDescending(x => x.UploadedAt).ToListAsync(cancellationToken);
        var result = new List<PromotionOfferSummary>();
        foreach (var file in files.GroupBy(x => x.Kind).Select(x => x.First())) result.Add(await SummaryAsync(file, cancellationToken));
        return ServiceResult<IReadOnlyList<PromotionOfferSummary>>.Ok(result);
    }

    public async Task<ServiceResult<PromotionOfferDetail>> DetailAsync(Guid tenantId, Guid connectionId, string kind, CancellationToken cancellationToken)
    {
        var file = await db.PromotionOfferFiles.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Kind == kind)
            .OrderByDescending(x => x.UploadedAt).FirstOrDefaultAsync(cancellationToken);
        if (file is null) return ServiceResult<PromotionOfferDetail>.Fail("RESOURCE_NOT_FOUND", "Bu promosyon için yüklenmiş dosya yok.", 404);
        var summary = await SummaryAsync(file, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (file.Kind == PromotionOfferKinds.Plus)
        {
            var rows = JsonSerializer.Deserialize<List<PlusOfferRow>>(file.RowsJson, Json) ?? [];
            var facts = await VariantFactsAsync(tenantId, rows.Select(x => x.Barcode).Concat(rows.Where(x => x.SellerSku != null).Select(x => x.SellerSku!)).ToArray(), cancellationToken);
            return ServiceResult<PromotionOfferDetail>.Ok(new(summary, rows.Select(row => PlusView(row, facts.Of(row.Barcode) ?? (row.SellerSku is null ? null : facts.Of(row.SellerSku)))).ToList(), null, null));
        }
        if (file.Kind == PromotionOfferKinds.Advantage)
        {
            var rows = JsonSerializer.Deserialize<List<AdvantageOfferRow>>(file.RowsJson, Json) ?? [];
            var barcodes = rows.Select(x => x.Barcode).ToArray();
            var facts = await VariantFactsAsync(tenantId, barcodes, cancellationToken);
            var tariff = await TariffRowsAsync(tenantId, connectionId, barcodes, now, cancellationToken);
            // A product outside the commission tariff pays one rate at any price: its current rate, read where it is known.
            var outside = rows.Where(row => row.InTariff == false && !tariff.ContainsKey(row.Barcode)).Select(row => row.Barcode).ToArray();
            var fixedRates = outside.Length == 0 ? [] : await CurrentRatesAsync(tenantId, connectionId, outside, cancellationToken);
            return ServiceResult<PromotionOfferDetail>.Ok(new(summary, null, rows.Select(row => AdvantageView(row, facts.Of(row.Barcode), tariff.GetValueOrDefault(row.Barcode),
                fixedRates.TryGetValue(row.Barcode, out var rate) ? rate : null)).ToList(), null));
        }

        var flashRows = JsonSerializer.Deserialize<List<FlashOfferRow>>(file.RowsJson, Json) ?? [];
        var flashCodes = flashRows.SelectMany(x => x.Barcodes).Distinct(StringComparer.Ordinal).ToArray();
        var flashFacts = await VariantFactsAsync(tenantId, flashCodes, cancellationToken);
        // The tariff in force when each slot starts (a flash runs in the coming days).
        var tariffs = new Dictionary<DateTimeOffset, Dictionary<string, CommissionTariffRow>>();
        var views = new List<FlashOfferView>(flashRows.Count);
        foreach (var row in flashRows)
        {
            var known = row.Barcodes.Select(flashFacts.Of).OfType<VariantFact>().ToList();
            var costs = known.Where(x => x.Cost is not null).Select(x => x.Cost!.Value).ToList();
            // What the model leaves needs every variant's cost; it is then a range from the dearest to the cheapest.
            decimal? low = costs.Count == row.Barcodes.Count && costs.Count > 0 ? costs.Min() : null;
            decimal? high = low is null ? null : costs.Max();
            var current = row.CustomerPrice is { } price && row.CurrentRate is { } rate ? Ranged(price, rate, low, high) : null;
            var slots = new List<FlashSlotView>(2);
            foreach (var (slot, offered, start, end) in new[] { ("24 Saat", row.Price24h, row.Start24h, row.End24h), ("3 Saat", row.Price3h, row.Start3h, row.End3h) })
            {
                if (offered is not { } flashPrice || flashPrice <= 0) continue;
                // A product in the commission tariff pays its band at the flash price; one outside it keeps its rate.
                decimal? flashRate = row.CurrentRate;
                if (row.InTariff != false)
                {
                    var at = ParseLocal(start) ?? now;
                    if (!tariffs.TryGetValue(at, out var rowsAt)) tariffs[at] = rowsAt = await TariffRowsAsync(tenantId, connectionId, flashCodes, at, cancellationToken);
                    flashRate = row.Barcodes.Select(code => rowsAt.GetValueOrDefault(code)).OfType<CommissionTariffRow>().Select(x => CommissionTariffBands.BandFor(x, flashPrice)?.Rate).FirstOrDefault(x => x is not null);
                }
                slots.Add(new(slot, start, end, flashRate is { } known2 ? Ranged(flashPrice, known2, low, high) : new PromotionPriceView(flashPrice, null, null, null)));
            }
            views.Add(new FlashOfferView(row.ModelCode, row.Barcodes, row.ProductName, known.Select(x => x.Color).FirstOrDefault(x => x is not null), known.Select(x => x.ImageUrl).FirstOrDefault(x => x is not null),
                row.Stock, low, high, row.InTariff, row.Campaign, current, slots, row.Promotions, row.Category, row.Brand));
        }
        return ServiceResult<PromotionOfferDetail>.Ok(new(summary, null, null, views));
    }

    public async Task<ServiceResult<bool>> DeleteAsync(Guid tenantId, Guid? userId, Guid connectionId, string kind, string correlationId, CancellationToken cancellationToken)
    {
        var files = await db.PromotionOfferFiles.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Kind == kind).ToListAsync(cancellationToken);
        if (files.Count == 0) return ServiceResult<bool>.Fail("RESOURCE_NOT_FOUND", "Bu promosyon için yüklenmiş dosya yok.", 404);
        db.PromotionOfferFiles.RemoveRange(files);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "PROMOTION_OFFER_DELETED", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = $"{kind} {files[0].FileName}", CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    internal sealed record VariantFact(decimal? Cost, string? Color, string? ImageUrl);

    // Now: the commission-basis price at the current rate; Plus: the Plus ceiling at the offered rate.
    internal static PlusOfferView PlusView(PlusOfferRow row, VariantFact? fact)
    {
        var current = row.BasisPrice is { } basis && row.CurrentRate is { } rate ? Ranged(basis, rate, fact?.Cost, fact?.Cost) : null;
        var plus = row.PlusPriceLimit is { } limit && row.PlusRate is { } plusRate ? Ranged(limit, plusRate, fact?.Cost, fact?.Cost) : null;
        return new(row.Barcode, row.SellerSku, row.ModelCode, row.ProductName, row.Size, fact?.Color, fact?.ImageUrl, row.Stock, fact?.Cost, current, plus,
            current?.Remaining is { } before && plus?.Remaining is { } after ? after - before : null, row.Promotions, row.Category, row.Brand);
    }

    // Each star priced at its ceiling with the commission tariff's band there; the stars the customer's price earns now.
    // FixedRate: the one rate of a product outside the commission tariff, at any price (null when it is not known).
    internal static AdvantageOfferView AdvantageView(AdvantageOfferRow row, VariantFact? fact, CommissionTariffRow? tariff, decimal? fixedRate = null)
    {
        PromotionPriceView Priced(decimal price) => tariff is null && row.InTariff == false && fixedRate is { } rate ? Ranged(price, rate, fact?.Cost, fact?.Cost) : PricedByTariff(price, tariff, fact?.Cost);
        var stars = new List<AdvantageStarView>(3);
        foreach (var (star, min, max) in new[] { (1, row.Star1Min, row.Star1Max), (2, row.Star2Min, row.Star2Max), (3, (decimal?)null, row.Star3Max) })
            if (max is { } ceiling and > 0) stars.Add(new(star, min, ceiling, Priced(ceiling)));
        var current = row.CustomerPrice is { } price ? Priced(price) : null;
        var earned = row.CustomerPrice is { } customer ? stars.Where(x => customer <= x.Max).Select(x => x.Star).DefaultIfEmpty(0).Max() : 0;
        return new(row.Barcode, row.ModelCode, row.ProductName, row.Size, fact?.Color, fact?.ImageUrl, row.Stock, fact?.Cost, row.InTariff, earned, current, stars, row.Promotions, row.Category, row.Brand);
    }

    internal static PromotionPriceView Ranged(decimal price, decimal rate, decimal? lowCost, decimal? highCost)
    {
        var commission = decimal.Round(price * rate / 100m, 2, MidpointRounding.AwayFromZero);
        // The dearest variant leaves the least.
        decimal? least = highCost is { } high ? price - commission - high : null;
        decimal? most = lowCost is { } low && lowCost != highCost ? price - commission - low : null;
        return new(price, rate, commission, least, most);
    }

    private static PromotionPriceView PricedByTariff(decimal price, CommissionTariffRow? row, decimal? cost) =>
        row is not null && CommissionTariffBands.BandFor(row, price) is { } band ? Ranged(price, band.Rate, cost, cost) : new(price, null, null, null);

    // "26/10/2026 00:00" in Istanbul time, as a UTC instant.
    internal static DateTimeOffset? ParseLocal(string? text) =>
        text is not null && DateTime.TryParseExact(text.Trim(), ["dd/MM/yyyy HH:mm", "d/M/yyyy HH:mm", "dd.MM.yyyy HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, CommissionTariffFileParser.TurkeyOffset).ToUniversalTime() : null;

    // Each barcode's current commission rate where the panel has it: the latest Plus file (Güncel Komisyon), else the
    // latest flash file (Mevcut Komisyon), else the rate Trendyol sent on the barcode's latest order line of the last 120 days.
    private async Task<Dictionary<string, decimal>> CurrentRatesAsync(Guid tenantId, Guid connectionId, string[] barcodes, CancellationToken cancellationToken)
    {
        var wanted = barcodes.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var files = await db.PromotionOfferFiles.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.Kind == PromotionOfferKinds.Plus || x.Kind == PromotionOfferKinds.Flash))
            .OrderByDescending(x => x.UploadedAt).ToListAsync(cancellationToken);
        foreach (var file in files.Where(x => x.Kind == PromotionOfferKinds.Plus).Take(1))
            foreach (var row in JsonSerializer.Deserialize<List<PlusOfferRow>>(file.RowsJson, Json) ?? [])
                if (row.CurrentRate is { } rate && wanted.Contains(row.Barcode)) result.TryAdd(row.Barcode, rate);
        foreach (var file in files.Where(x => x.Kind == PromotionOfferKinds.Flash).Take(1))
            foreach (var row in JsonSerializer.Deserialize<List<FlashOfferRow>>(file.RowsJson, Json) ?? [])
                if (row.CurrentRate is { } rate)
                    foreach (var code in row.Barcodes.Where(wanted.Contains)) result.TryAdd(code, rate);
        var missing = wanted.Where(code => !result.ContainsKey(code)).ToArray();
        if (missing.Length == 0) return result;
        var since = timeProvider.GetUtcNow().AddDays(-120).ToUniversalTime();
        var lines = await (from line in db.OrderLines.AsNoTracking()
                           join order in db.Orders.AsNoTracking() on new { line.TenantId, Id = line.OrderId } equals new { order.TenantId, order.Id }
                           where line.TenantId == tenantId && order.ConnectionId == connectionId && order.OrderedAt >= since
                               && line.Barcode != null && missing.Contains(line.Barcode) && line.SourceSnapshotJson != null
                           orderby order.OrderedAt descending
                           select new { line.Barcode, line.SourceSnapshotJson }).ToListAsync(cancellationToken);
        foreach (var line in lines)
        {
            if (result.ContainsKey(line.Barcode!)) continue;
            try
            {
                using var json = JsonDocument.Parse(line.SourceSnapshotJson!);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("commission", out var value)
                    && (value.ValueKind == JsonValueKind.Number ? value.TryGetDecimal(out var rate) : value.ValueKind == JsonValueKind.String && CommissionTariffFileParser.TryParseNumber(value.GetString()!, out rate))
                    && rate is > 0 and <= 100)
                    result[line.Barcode!] = rate;
            }
            catch (JsonException) { }
        }
        return result;
    }

    // Each barcode's row of the commission tariffs in force at `at`, the latest upload first.
    private async Task<Dictionary<string, CommissionTariffRow>> TariffRowsAsync(Guid tenantId, Guid connectionId, string[] barcodes, DateTimeOffset at, CancellationToken cancellationToken)
    {
        at = at.ToUniversalTime();
        var tariffIds = await db.CommissionTariffs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.PeriodStart <= at && x.PeriodEnd > at)
            .OrderByDescending(x => x.UploadedAt).Select(x => x.Id).ToListAsync(cancellationToken);
        var result = new Dictionary<string, CommissionTariffRow>(StringComparer.Ordinal);
        foreach (var tariffId in tariffIds)
        {
            var rows = await db.CommissionTariffRows.AsNoTracking().Where(x => x.TenantId == tenantId && x.TariffId == tariffId && barcodes.Contains(x.Barcode)).ToListAsync(cancellationToken);
            foreach (var row in rows) result.TryAdd(row.Barcode, row);
        }
        return result;
    }

    private sealed record VariantFacts(Dictionary<string, VariantFact> ByBarcode, Dictionary<string, VariantFact> BySku)
    {
        public VariantFact? Of(string code) => ByBarcode.GetValueOrDefault(code) ?? BySku.GetValueOrDefault(code);
    }

    // The panel variant with each code as its barcode or SKU: its cost, colour and picture (its own, else its product's).
    private async Task<VariantFacts> VariantFactsAsync(Guid tenantId, string[] codes, CancellationToken cancellationToken)
    {
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Barcode != null && codes.Contains(x.Barcode) || codes.Contains(x.Sku)))
            .Select(x => new { x.Id, x.ProductId, x.Barcode, x.Sku, x.CostPrice, x.OptionSignature })
            .ToListAsync(cancellationToken);
        var productIds = variants.Select(x => x.ProductId).Distinct().ToArray();
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && productIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE"
                               && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder, item.Id
                           select new { item.ProductId, item.VariantId, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        var byBarcode = new Dictionary<string, VariantFact>(StringComparer.Ordinal);
        var bySku = new Dictionary<string, VariantFact>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var picked = media.FirstOrDefault(x => x.VariantId == variant.Id) ?? media.FirstOrDefault(x => x.ProductId == variant.ProductId && x.VariantId == null) ?? media.FirstOrDefault(x => x.ProductId == variant.ProductId);
            var fact = new VariantFact(variant.CostPrice is > 0 ? variant.CostPrice : null, CommissionTariffService.ColorOf(variant.OptionSignature),
                picked is null ? null : CatalogMediaDisplay.Url(picked.Id, picked.Classification, picked.Url));
            if (variant.Barcode is not null) byBarcode.TryAdd(variant.Barcode, fact);
            bySku.TryAdd(variant.Sku, fact);
        }
        return new(byBarcode, bySku);
    }

    private async Task<PromotionOfferSummary> SummaryAsync(PromotionOfferFile file, CancellationToken cancellationToken)
    {
        var codes = file.Kind switch
        {
            PromotionOfferKinds.Plus => (JsonSerializer.Deserialize<List<PlusOfferRow>>(file.RowsJson, Json) ?? []).Select(x => x.Barcode).ToArray(),
            PromotionOfferKinds.Advantage => (JsonSerializer.Deserialize<List<AdvantageOfferRow>>(file.RowsJson, Json) ?? []).Select(x => x.Barcode).ToArray(),
            _ => (JsonSerializer.Deserialize<List<FlashOfferRow>>(file.RowsJson, Json) ?? []).SelectMany(x => x.Barcodes).ToArray()
        };
        var matched = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == file.TenantId && x.Barcode != null && codes.Contains(x.Barcode)).Select(x => x.Barcode).Distinct().CountAsync(cancellationToken);
        return new(file.Kind, file.FileName, file.UploadedAt, file.PeriodText, file.RowCount, matched);
    }

    private Task<PlatformConnection?> ConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED", cancellationToken);
}
