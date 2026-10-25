using System.Security.Cryptography;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class CommissionTariffService(AppDbContext db, TimeProvider timeProvider) : ICommissionTariffService
{
    public const long MaximumFileBytes = 10 * 1024 * 1024;
    private const long MaximumExpandedBytes = 64 * 1024 * 1024;
    private const string LikeEscape = "\\";

    public async Task<ServiceResult<CommissionTariffUploadResult>> UploadAsync(Guid tenantId, Guid? userId, Guid connectionId, string fileName, Stream content, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return ServiceResult<CommissionTariffUploadResult>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        if (!CommissionTariffPlatforms.Supports(connection.PlatformCode))
            return ServiceResult<CommissionTariffUploadResult>.Fail("COMMISSION_TARIFF_PLATFORM_UNSUPPORTED", "Komisyon tarifesi bu pazaryeri için yüklenemez.", 422);

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length is 0 or > MaximumFileBytes)
            return ServiceResult<CommissionTariffUploadResult>.Fail("COMMISSION_TARIFF_FILE_SIZE", "Tarife dosyası boş olamaz ve en fazla 10 MiB olabilir.", 413);
        var hash = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        var now = timeProvider.GetUtcNow();
        ParsedCommissionTariffFile parsed;
        try
        {
            buffer.Position = 0;
            parsed = CommissionTariffFileParser.Parse(ImportFileReader.ReadXlsxTable(buffer, MaximumExpandedBytes), now);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
        {
            return ServiceResult<CommissionTariffUploadResult>.Fail("COMMISSION_TARIFF_FILE_INVALID", exception is InvalidDataException ? exception.Message : "Dosya okunamadı; pazaryerinden indirilen .xlsx tarife dosyasını yükleyin.", 422);
        }

        var safeName = Path.GetFileName(fileName);
        if (safeName.Length > 260) safeName = safeName[..260];
        var created = new List<CommissionTariff>();
        var alreadyUploaded = true;
        var imported = 0;
        foreach (var period in parsed.Tariffs)
        {
            var existing = await db.CommissionTariffs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.FileHash == hash && x.PeriodStart == period.PeriodStart, cancellationToken);
            if (existing) continue;
            alreadyUploaded = false;
            var tariff = new CommissionTariff
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                PlatformCode = connection.PlatformCode.ToUpperInvariant(),
                PeriodStart = period.PeriodStart,
                PeriodEnd = period.PeriodEnd,
                PeriodText = period.PeriodText.Length > 120 ? period.PeriodText[..120] : period.PeriodText,
                FileName = safeName,
                FileHash = hash,
                UploadedAt = now,
                UploadedBy = userId,
                RowCount = period.Rows.Count
            };
            foreach (var row in period.Rows) { row.TenantId = tenantId; row.TariffId = tariff.Id; }
            db.CommissionTariffs.Add(tariff);
            db.CommissionTariffRows.AddRange(period.Rows);
            created.Add(tariff);
            imported += period.Rows.Count;
        }
        if (created.Count > 0)
        {
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "COMMISSION_TARIFF_UPLOADED", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = $"{safeName}: {imported} satır, {string.Join(", ", created.Select(x => x.PeriodText))}", CorrelationId = correlationId, CreatedAt = now });
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException)
            {
                // The same file for the same period was stored by a parallel upload: it is already there.
                db.ChangeTracker.Clear();
                alreadyUploaded = true;
                imported = 0;
            }
        }
        var periodStarts = parsed.Tariffs.Select(x => x.PeriodStart).ToArray();
        var tariffs = await db.CommissionTariffs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.FileHash == hash && periodStarts.Contains(x.PeriodStart))
            .OrderBy(x => x.PeriodStart)
            .ToListAsync(cancellationToken);
        var views = new List<CommissionTariffView>(tariffs.Count);
        foreach (var tariff in tariffs) views.Add(await ViewAsync(tariff, now, cancellationToken));
        return ServiceResult<CommissionTariffUploadResult>.Ok(new(views, imported, parsed.SkippedRows, alreadyUploaded, parsed.Warnings));
    }

    public async Task<ServiceResult<IReadOnlyList<CommissionTariffView>>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (await ConnectionAsync(tenantId, connectionId, cancellationToken) is null) return ServiceResult<IReadOnlyList<CommissionTariffView>>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        var now = timeProvider.GetUtcNow();
        var tariffs = await db.CommissionTariffs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .OrderByDescending(x => x.PeriodStart).ThenByDescending(x => x.UploadedAt)
            .Take(60)
            .ToListAsync(cancellationToken);
        var views = new List<CommissionTariffView>(tariffs.Count);
        foreach (var tariff in tariffs) views.Add(await ViewAsync(tariff, now, cancellationToken));
        return ServiceResult<IReadOnlyList<CommissionTariffView>>.Ok(views);
    }

    public async Task<ServiceResult<CommissionTariffRowPage>> RowsAsync(Guid tenantId, Guid connectionId, Guid tariffId, string? search, int page, int pageSize, CommissionTariffRowFilter filter, CancellationToken cancellationToken)
    {
        var tariff = await db.CommissionTariffs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == tariffId, cancellationToken);
        if (tariff is null) return ServiceResult<CommissionTariffRowPage>.Fail("RESOURCE_NOT_FOUND", "Komisyon tarifesi bulunamadı.", 404);
        var query = db.CommissionTariffRows.AsNoTracking().Where(x => x.TenantId == tenantId && x.TariffId == tariffId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (term.Length > 120) term = term[..120];
            var pattern = $"%{CatalogService.EscapeLike(term)}%";
            query = query.Where(x => EF.Functions.ILike(x.Barcode, pattern, LikeEscape)
                || x.SellerSku != null && EF.Functions.ILike(x.SellerSku, pattern, LikeEscape)
                || x.ModelCode != null && EF.Functions.ILike(x.ModelCode, pattern, LikeEscape)
                || x.ProductName != null && EF.Functions.ILike(x.ProductName, pattern, LikeEscape));
        }
        // The band the price is in now, by the tariff's own limits (as CommissionTariffBands.Bands and BandFor read them).
        query = filter.Tier switch
        {
            1 => query.Where(x => x.Rate1 != null && x.Tier1Min != null && x.BasisPrice >= x.Tier1Min),
            2 => query.Where(x => x.Rate2 != null && x.BasisPrice != null && (x.Tier2Min != null || x.Tier2Max != null) && (x.Tier2Min == null || x.BasisPrice >= x.Tier2Min) && (x.Tier2Max == null || x.BasisPrice <= x.Tier2Max)),
            3 => query.Where(x => x.Rate3 != null && x.BasisPrice != null && (x.Tier3Min != null || x.Tier3Max != null) && (x.Tier3Min == null || x.BasisPrice >= x.Tier3Min) && (x.Tier3Max == null || x.BasisPrice <= x.Tier3Max)),
            4 => query.Where(x => x.Rate4 != null && x.Tier4Max != null && x.BasisPrice <= x.Tier4Max),
            _ => query
        };
        if (filter.HasCost is { } hasCost)
            query = query.Where(x => db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.CostPrice != null && variant.CostPrice > 0
                && (variant.Barcode == x.Barcode || x.SellerSku != null && variant.Sku == x.SellerSku)) == hasCost);
        // A page is a set of models (a row without a model code is a model of its own), each with all its variants, so a
        // model's variants are never split across two pages.
        var models = query.GroupBy(x => x.ModelCode != null && x.ModelCode != "" ? x.ModelCode : x.Barcode)
            .Select(group => new { Key = group.Key, Name = group.Min(x => x.ProductName) });
        var total = await models.CountAsync(cancellationToken);
        var keys = await models.OrderBy(x => x.Name).ThenBy(x => x.Key).Skip(page * pageSize).Take(pageSize).Select(x => x.Key).ToListAsync(cancellationToken);
        var rows = await query.Where(x => keys.Contains(x.ModelCode != null && x.ModelCode != "" ? x.ModelCode : x.Barcode))
            .OrderBy(x => x.ProductName).ThenBy(x => x.Barcode).ToListAsync(cancellationToken);
        var barcodes = rows.Select(x => x.Barcode).ToArray();
        var skus = rows.Where(x => x.SellerSku != null).Select(x => x.SellerSku!).ToArray();
        var matched = await PanelMatchesAsync(tenantId, connectionId, barcodes, skus, cancellationToken);
        var costs = await CostsAsync(tenantId, barcodes, skus, cancellationToken);
        var images = await ImagesAsync(tenantId, rows, cancellationToken);
        var colors = await ColorsAsync(tenantId, barcodes, skus, cancellationToken);
        var items = rows.Select(row =>
        {
            var current = row.BasisPrice is { } basis ? CommissionTariffBands.BandFor(row, basis) : null;
            var next = row.BasisPrice is { } price ? CommissionTariffBands.NextLowerBand(row, price) : null;
            var cost = CostOf(costs, row);
            var choices = CommissionTariffChoices.Of(row, cost);
            return new CommissionTariffRowView(row.Id, row.Barcode, row.SellerSku, row.ModelCode, row.ProductName, row.Size, row.BasisPrice, row.CurrentSalePrice, row.CurrentRate,
                current?.Tier, current?.Rate, CommissionTariffBands.Bands(row).Select(Band).ToList(), next is null ? null : Band(next),
                matched.Contains(row.Barcode) || row.SellerSku is not null && matched.Contains(row.SellerSku), cost, choices, CommissionTariffChoices.Recommended(choices),
                images.GetValueOrDefault(row.Id), colors.ByBarcode.GetValueOrDefault(row.Barcode) ?? (row.SellerSku is null ? null : colors.BySku.GetValueOrDefault(row.SellerSku)));
        }).ToList();
        return ServiceResult<CommissionTariffRowPage>.Ok(new(items, total, page, pageSize));
    }

    // The seller's picks written out as an Excel sheet: one row per product picked, its new price, band, commission and
    // what is left after the commission and its cost. Products picked for no band are left out.
    public async Task<ServiceResult<CommissionTariffExportFile>> ExportAsync(Guid tenantId, Guid connectionId, Guid tariffId, CommissionTariffExportRequest request, CancellationToken cancellationToken)
    {
        var tariff = await db.CommissionTariffs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == tariffId, cancellationToken);
        if (tariff is null) return ServiceResult<CommissionTariffExportFile>.Fail("RESOURCE_NOT_FOUND", "Komisyon tarifesi bulunamadı.", 404);
        var bulk = request.Bulk?.Trim().ToLowerInvariant() switch { "recommended" => "amount", var value => value };
        if (bulk is not (null or "" or "tier" or "amount" or "rate") || bulk == "tier" && request.Tier is not (>= 1 and <= 4)
            || request.MinRate is < -1000 or > 100 || request.MinAmount is < -10_000_000 or > 10_000_000)
            return ServiceResult<CommissionTariffExportFile>.Fail("COMMISSION_TARIFF_EXPORT_INVALID", "Toplu seçim geçersiz.", 400);
        if (request.Choices is { Count: > 50_000 } || request.Choices?.Any(x => x.Tier is not (null or (>= 1 and <= 4)) || x.Price is <= 0 or > 10_000_000) == true)
            return ServiceResult<CommissionTariffExportFile>.Fail("COMMISSION_TARIFF_EXPORT_INVALID", "Seçimlerden biri geçersiz.", 400);
        var picks = (request.Choices ?? []).GroupBy(x => x.RowId).ToDictionary(x => x.Key, x => x.Last());
        var rows = await db.CommissionTariffRows.AsNoTracking().Where(x => x.TenantId == tenantId && x.TariffId == tariffId)
            .OrderBy(x => x.ProductName).ThenBy(x => x.Barcode).ToListAsync(cancellationToken);
        var costs = await CostsAsync(tenantId, rows.Select(x => x.Barcode).ToArray(), rows.Where(x => x.SellerSku != null).Select(x => x.SellerSku!).ToArray(), cancellationToken);
        var sheet = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Barkod", "Satıcı Stok Kodu", "Model Kodu", "Ürün", "Beden", "Güncel Satış Fiyatı", "Komisyona Esas Fiyat", "Güncel Bant", "Seçilen Bant", "Yeni Fiyat",
                "Komisyon Oranı (%)", "Komisyon", "Ürün Maliyeti", "Komisyon ve Maliyet Sonrası Kalan" }
        };
        foreach (var row in rows)
        {
            var cost = CostOf(costs, row);
            CommissionChoiceView? choice;
            if (picks.TryGetValue(row.Id, out var pick))
                choice = pick.Price is { } own ? CommissionTariffChoices.PricedAt(row, own, cost) : pick.Tier is { } tier ? CommissionTariffChoices.Of(row, cost).FirstOrDefault(x => x.Tier == tier) : null;
            else choice = string.IsNullOrEmpty(bulk) ? null : CommissionTariffChoices.Pick(CommissionTariffChoices.Of(row, cost), bulk, request.Tier, request.MinRate, request.MinAmount);
            if (choice is null) continue;
            var current = row.BasisPrice is { } basis ? CommissionTariffBands.BandFor(row, basis) : null;
            sheet.Add(new object?[]
            {
                row.Barcode, row.SellerSku, row.ModelCode, row.ProductName, row.Size, row.CurrentSalePrice, row.BasisPrice,
                current is null ? null : $"{current.Tier}. bant", $"{choice.Tier}. bant", choice.Price, choice.Rate, choice.Commission, cost, choice.Remaining
            });
        }
        var content = SimpleXlsxWriter.Write([new SimpleXlsxSheet("Seçimler", sheet, [16, 18, 16, 48, 10, 16, 18, 12, 12, 14, 16, 14, 14, 22])]);
        var name = $"komisyon-tarifesi-secimler-{tariff.PeriodStart.ToOffset(CommissionTariffFileParser.TurkeyOffset):yyyyMMdd}.xlsx";
        return ServiceResult<CommissionTariffExportFile>.Ok(new(content, name, sheet.Count - 1));
    }

    // The product cost entered on the panel's variant with the row's barcode, else its seller SKU.
    private async Task<(Dictionary<string, decimal> ByBarcode, Dictionary<string, decimal> BySku)> CostsAsync(Guid tenantId, string[] barcodes, string[] skus, CancellationToken cancellationToken)
    {
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CostPrice != null && x.CostPrice > 0 && (x.Barcode != null && barcodes.Contains(x.Barcode) || skus.Contains(x.Sku)))
            .Select(x => new { x.Barcode, x.Sku, Cost = x.CostPrice!.Value })
            .ToListAsync(cancellationToken);
        var byBarcode = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var bySku = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (variant.Barcode is not null) byBarcode.TryAdd(variant.Barcode, variant.Cost);
            bySku.TryAdd(variant.Sku, variant.Cost);
        }
        return (byBarcode, bySku);
    }

    // Each row's picture from the panel's product with its barcode (else seller SKU): the variant's own first, else the
    // product's, as the reports' product lists show them.
    private async Task<Dictionary<Guid, string>> ImagesAsync(Guid tenantId, IReadOnlyList<CommissionTariffRow> rows, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        var barcodes = rows.Select(x => x.Barcode).ToArray();
        var skus = rows.Where(x => x.SellerSku != null).Select(x => x.SellerSku!).ToArray();
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Barcode != null && barcodes.Contains(x.Barcode) || skus.Contains(x.Sku)))
            .Select(x => new { x.Id, x.ProductId, x.Barcode, x.Sku })
            .ToListAsync(cancellationToken);
        if (variants.Count == 0) return result;
        var productIds = variants.Select(x => x.ProductId).Distinct().ToArray();
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && productIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE"
                               && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder, item.Id
                           select new { item.ProductId, item.VariantId, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            var variant = variants.FirstOrDefault(x => x.Barcode == row.Barcode) ?? (row.SellerSku is null ? null : variants.FirstOrDefault(x => x.Sku == row.SellerSku));
            if (variant is null) continue;
            var picked = media.FirstOrDefault(x => x.VariantId == variant.Id) ?? media.FirstOrDefault(x => x.ProductId == variant.ProductId && x.VariantId == null) ?? media.FirstOrDefault(x => x.ProductId == variant.ProductId);
            if (picked is not null) result[row.Id] = CatalogMediaDisplay.Url(picked.Id, picked.Classification, picked.Url);
        }
        return result;
    }

    // Each variant's colour from its option signature ("Renk: Bej | Beden: L | …"), by barcode and by SKU.
    private async Task<(Dictionary<string, string> ByBarcode, Dictionary<string, string> BySku)> ColorsAsync(Guid tenantId, string[] barcodes, string[] skus, CancellationToken cancellationToken)
    {
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Barcode != null && barcodes.Contains(x.Barcode) || skus.Contains(x.Sku)))
            .Select(x => new { x.Barcode, x.Sku, x.OptionSignature })
            .ToListAsync(cancellationToken);
        var byBarcode = new Dictionary<string, string>(StringComparer.Ordinal);
        var bySku = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (ColorOf(variant.OptionSignature) is not { } color) continue;
            if (variant.Barcode is not null) byBarcode.TryAdd(variant.Barcode, color);
            bySku.TryAdd(variant.Sku, color);
        }
        return (byBarcode, bySku);
    }

    internal static string? ColorOf(string? optionSignature) =>
        optionSignature?.Split('|').Select(part => part.Split(':', 2)).Where(pair => pair.Length == 2)
            .Where(pair => pair[0].Trim().Equals("Renk", StringComparison.OrdinalIgnoreCase) || pair[0].Trim().Equals("Color", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair[1].Trim()).FirstOrDefault(value => value.Length > 0);

    private static decimal? CostOf((Dictionary<string, decimal> ByBarcode, Dictionary<string, decimal> BySku) costs, CommissionTariffRow row) =>
        costs.ByBarcode.TryGetValue(row.Barcode, out var cost) ? cost : row.SellerSku is not null && costs.BySku.TryGetValue(row.SellerSku, out var skuCost) ? skuCost : null;

    public async Task<ServiceResult<bool>> DeleteAsync(Guid tenantId, Guid? userId, Guid connectionId, Guid tariffId, string correlationId, CancellationToken cancellationToken)
    {
        var tariff = await db.CommissionTariffs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == tariffId, cancellationToken);
        if (tariff is null) return ServiceResult<bool>.Fail("RESOURCE_NOT_FOUND", "Komisyon tarifesi bulunamadı.", 404);
        // The rows go with the tariff (the database cascades the delete).
        db.CommissionTariffs.Remove(tariff);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "COMMISSION_TARIFF_DELETED", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = $"{tariff.FileName}: {tariff.PeriodText}", CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    private async Task<CommissionTariffView> ViewAsync(CommissionTariff tariff, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A row counts as matched when a panel variant carries its barcode or seller SKU, or this connection's link
        // carries the barcode.
        var matched = await db.CommissionTariffRows.AsNoTracking()
            .Where(row => row.TenantId == tariff.TenantId && row.TariffId == tariff.Id
                && (db.ProductVariants.Any(variant => variant.TenantId == tariff.TenantId && (variant.Barcode == row.Barcode || row.SellerSku != null && variant.Sku == row.SellerSku))
                    || db.MarketplaceVariantLinks.Any(link => link.TenantId == tariff.TenantId && link.ConnectionId == tariff.ConnectionId && link.ExternalBarcode == row.Barcode)))
            .CountAsync(cancellationToken);
        return new(tariff.Id, tariff.PlatformCode, tariff.PeriodStart, tariff.PeriodEnd, tariff.PeriodText, tariff.FileName, tariff.UploadedAt, tariff.RowCount, matched,
            now >= tariff.PeriodStart && now < tariff.PeriodEnd);
    }

    private async Task<HashSet<string>> PanelMatchesAsync(Guid tenantId, Guid connectionId, string[] barcodes, string[] skus, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (barcodes.Length > 0)
        {
            result.UnionWith(await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.Barcode != null && barcodes.Contains(x.Barcode)).Select(x => x.Barcode!).ToListAsync(cancellationToken));
            result.UnionWith(await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalBarcode != null && barcodes.Contains(x.ExternalBarcode)).Select(x => x.ExternalBarcode!).ToListAsync(cancellationToken));
        }
        if (skus.Length > 0)
            result.UnionWith(await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && skus.Contains(x.Sku)).Select(x => x.Sku).ToListAsync(cancellationToken));
        return result;
    }

    private Task<PlatformConnection?> ConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED", cancellationToken);

    private static CommissionBandView Band(CommissionBand band) => new(band.Tier, band.Min, band.Max, band.Rate);
}

// The commission rate a sale pays by the uploaded tariff in force when it was ordered.
public sealed class CommissionTariffLookup(AppDbContext db) : ICommissionTariffLookup
{
    public async Task<CommissionTariffMatch?> RateAsync(Guid tenantId, Guid connectionId, string? barcode, string? sellerSku, DateTimeOffset at, decimal basisPrice, CancellationToken cancellationToken)
    {
        var code = barcode?.Trim();
        var sku = sellerSku?.Trim();
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(sku)) return null;
        // Npgsql writes timestamptz parameters only at offset 0.
        at = at.ToUniversalTime();
        // The tariffs in force at `at`, the latest upload first: a re-issued tariff for the same week replaces the earlier one.
        var tariffs = await db.CommissionTariffs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.PeriodStart <= at && x.PeriodEnd > at)
            .OrderByDescending(x => x.UploadedAt)
            .Select(x => new { x.Id, x.PeriodStart, x.PeriodEnd })
            .ToListAsync(cancellationToken);
        foreach (var tariff in tariffs)
        {
            CommissionTariffRow? row = null;
            if (!string.IsNullOrEmpty(code))
                row = await db.CommissionTariffRows.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.TariffId == tariff.Id && x.Barcode == code, cancellationToken);
            if (row is null && !string.IsNullOrEmpty(sku))
                row = await db.CommissionTariffRows.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.TariffId == tariff.Id && x.SellerSku == sku, cancellationToken);
            if (row is null) continue;
            var band = CommissionTariffBands.BandFor(row, basisPrice);
            return band is null ? null : new CommissionTariffMatch(band.Rate, band.Tier, tariff.Id, tariff.PeriodStart, tariff.PeriodEnd);
        }
        return null;
    }
}

// A tariff row's bands as prices the seller can pick, each with its commission and what the price leaves after the
// commission and the product cost, and the band that leaves the most.
internal static class CommissionTariffChoices
{
    // Every band of the row as a price to pick, from the highest price band down.
    public static List<CommissionChoiceView> Of(CommissionTariffRow row, decimal? cost) =>
        CommissionTariffBands.Bands(row)
            .Select(band => CommissionTariffBands.TargetPrice(band, row.BasisPrice) is { } price and > 0 ? Priced(band, price, cost) : null)
            .OfType<CommissionChoiceView>().ToList();

    // A price of the seller's own, in the band it falls in (none when it falls in no band).
    public static CommissionChoiceView? PricedAt(CommissionTariffRow row, decimal price, decimal? cost)
    {
        var rounded = decimal.Round(price, 2, MidpointRounding.AwayFromZero);
        return CommissionTariffBands.BandFor(row, rounded) is { } band ? Priced(band, rounded, cost) : null;
    }

    private static CommissionChoiceView Priced(CommissionBand band, decimal price, decimal? cost)
    {
        var commission = decimal.Round(price * band.Rate / 100m, 2, MidpointRounding.AwayFromZero);
        return new(band.Tier, price, band.Rate, commission, cost is { } known ? price - commission - known : null);
    }

    // The band that leaves the most after the commission and the cost; none when the cost is not known.
    public static int? Recommended(IReadOnlyList<CommissionChoiceView> choices) => Pick(choices, "amount", null, null, null)?.Tier;

    // The pick for every row: one band ("tier"), the band leaving the most ("amount") or the largest share of its price
    // ("rate"); none when it leaves less than MinRate per cent of the price or MinAmount lira, or the cost is not known
    // for a comparison that needs it.
    public static CommissionChoiceView? Pick(IReadOnlyList<CommissionChoiceView> choices, string kind, int? tier, decimal? minRate, decimal? minAmount)
    {
        var choice = kind switch
        {
            "tier" => choices.FirstOrDefault(x => x.Tier == tier),
            "amount" => choices.Where(x => x.Remaining is not null).OrderByDescending(x => x.Remaining).ThenBy(x => x.Rate).FirstOrDefault(),
            "rate" => choices.Where(x => x.Remaining is not null && x.Price > 0).OrderByDescending(x => x.Remaining!.Value / x.Price).ThenBy(x => x.Rate).FirstOrDefault(),
            _ => null
        };
        if (choice is null) return null;
        if (minRate is { } rate && (choice.Remaining is not { } left || choice.Price <= 0 || left / choice.Price * 100m < rate)) return null;
        if (minAmount is { } amount && (choice.Remaining is not { } kept || kept < amount)) return null;
        return choice;
    }
}
