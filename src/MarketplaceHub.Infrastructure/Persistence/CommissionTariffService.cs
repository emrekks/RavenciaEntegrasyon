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
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
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
        var backfilled = 0;
        foreach (var period in parsed.Tariffs)
        {
            var existing = await db.CommissionTariffs.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.FileHash == hash && x.PeriodStart == period.PeriodStart)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
            // The same file again fills the category, brand and stock of rows stored before they were read, and keeps the
            // file itself when a tariff stored before files were kept has none.
            if (existing is { } existingId)
            {
                backfilled += await FillFactsAsync(tenantId, existingId, period.Rows, cancellationToken);
                // The period's name ("3 Gün") for a tariff stored before it was read.
                if (period.Label is { } label)
                    backfilled += await db.CommissionTariffs.Where(x => x.TenantId == tenantId && x.Id == existingId && x.PeriodLabel == null)
                        .ExecuteUpdateAsync(set => set.SetProperty(x => x.PeriodLabel, label), cancellationToken);
                if (!await db.CommissionTariffSourceFiles.AnyAsync(x => x.TenantId == tenantId && x.TariffId == existingId, cancellationToken))
                {
                    db.CommissionTariffSourceFiles.Add(new CommissionTariffSourceFile { TariffId = existingId, TenantId = tenantId, Content = bytes });
                    backfilled++;
                }
                continue;
            }
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
                PeriodLabel = period.Label,
                FileName = safeName,
                FileHash = hash,
                UploadedAt = now,
                UploadedBy = userId,
                RowCount = period.Rows.Count
            };
            foreach (var row in period.Rows) { row.TenantId = tenantId; row.TariffId = tariff.Id; }
            db.CommissionTariffs.Add(tariff);
            db.CommissionTariffRows.AddRange(period.Rows);
            db.CommissionTariffSourceFiles.Add(new CommissionTariffSourceFile { TariffId = tariff.Id, TenantId = tenantId, Content = bytes });
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
        else if (backfilled > 0) await db.SaveChangesAsync(cancellationToken);
        var periodStarts = parsed.Tariffs.Select(x => x.PeriodStart).ToArray();
        var tariffs = await db.CommissionTariffs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.FileHash == hash && periodStarts.Contains(x.PeriodStart))
            .OrderBy(x => x.PeriodStart)
            .ToListAsync(cancellationToken);
        return ServiceResult<CommissionTariffUploadResult>.Ok(new(await ViewsAsync(tenantId, connectionId, tariffs, now, cancellationToken), imported, parsed.SkippedRows, alreadyUploaded, parsed.Warnings));
    }

    // The tariffs as listed, each with the file it came from: the periods of one file share the Id of its first period.
    private async Task<List<CommissionTariffView>> ViewsAsync(Guid tenantId, Guid connectionId, IReadOnlyList<CommissionTariff> tariffs, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hashes = tariffs.Select(x => x.FileHash).Distinct().ToArray();
        var groups = (await db.CommissionTariffs.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && hashes.Contains(x.FileHash))
                .Select(x => new { x.Id, x.FileHash, x.PeriodStart }).ToListAsync(cancellationToken))
            .GroupBy(x => x.FileHash).ToDictionary(group => group.Key, group => group.OrderBy(x => x.PeriodStart).ThenBy(x => x.Id).First().Id);
        var views = new List<CommissionTariffView>(tariffs.Count);
        foreach (var tariff in tariffs) views.Add(await ViewAsync(tariff, now, groups.GetValueOrDefault(tariff.FileHash, tariff.Id), cancellationToken));
        return views;
    }

    // The tariffs of the same file as `tariff` (its periods), the earliest first; the tariff alone for a one-period file.
    private Task<List<CommissionTariff>> PeriodsAsync(CommissionTariff tariff, CancellationToken cancellationToken) =>
        db.CommissionTariffs.AsNoTracking().Where(x => x.TenantId == tariff.TenantId && x.ConnectionId == tariff.ConnectionId && x.FileHash == tariff.FileHash)
            .OrderBy(x => x.PeriodStart).ThenBy(x => x.Id).ToListAsync(cancellationToken);

    public async Task<ServiceResult<IReadOnlyList<CommissionTariffView>>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (await ConnectionAsync(tenantId, connectionId, cancellationToken) is null) return ServiceResult<IReadOnlyList<CommissionTariffView>>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        var now = timeProvider.GetUtcNow();
        var tariffs = await db.CommissionTariffs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .OrderByDescending(x => x.PeriodStart).ThenByDescending(x => x.UploadedAt)
            .Take(60)
            .ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<CommissionTariffView>>.Ok(await ViewsAsync(tenantId, connectionId, tariffs, now, cancellationToken));
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
        if (filter.Barcodes is { Count: > 0 } barcodes)
        {
            var exact = barcodes.ToArray();
            query = query.Where(x => exact.Contains(x.Barcode));
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
        if (Term(filter.Name) is { } name)
            query = query.Where(x => x.ProductName != null && EF.Functions.ILike(x.ProductName, Like(name), LikeEscape));
        if (Term(filter.Barcode) is { } code)
            query = query.Where(x => EF.Functions.ILike(x.Barcode, Like(code), LikeEscape) || x.SellerSku != null && EF.Functions.ILike(x.SellerSku, Like(code), LikeEscape));
        if (Term(filter.ModelCode) is { } model)
            query = query.Where(x => x.ModelCode != null && EF.Functions.ILike(x.ModelCode, Like(model), LikeEscape));
        // A stock range keeps only rows whose stock the file gave.
        // The panel's own stock of the variant with the row's barcode (else seller SKU), else the file's.
        // (SQL's SUM over no rows reads as 0 here, so a row with no panel stock is told apart first.)
        var stocked = query.Select(x => new
        {
            Row = x,
            Stock = db.InventoryItems.Any(item => item.TenantId == tenantId && item.LocationCode == "MAIN"
                && db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.Id == item.VariantId && (variant.Barcode == x.Barcode || x.SellerSku != null && variant.Sku == x.SellerSku)))
                ? (decimal?)db.InventoryItems.Where(item => item.TenantId == tenantId && item.LocationCode == "MAIN"
                && db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.Id == item.VariantId && (variant.Barcode == x.Barcode || x.SellerSku != null && variant.Sku == x.SellerSku))).Sum(item => item.OnHand)
                : x.Stock
        });
        if (filter.MinStock is { } minStock) stocked = stocked.Where(x => x.Stock != null && x.Stock >= minStock);
        if (filter.MaxStock is { } maxStock) stocked = stocked.Where(x => x.Stock != null && x.Stock <= maxStock);
        if (filter.MinStock is not null || filter.MaxStock is not null) query = stocked.Select(x => x.Row);
        if (Term(filter.Category) is { } category) query = query.Where(x => x.Category == category);
        if (Term(filter.Brand) is { } brand) query = query.Where(x => x.Brand == brand);
        if (filter.HasCost is { } hasCost)
            query = query.Where(x => db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.CostPrice != null && variant.CostPrice > 0
                && (variant.Barcode == x.Barcode || x.SellerSku != null && variant.Sku == x.SellerSku)) == hasCost);
        if (filter.Sort is not null || filter.FiltersProfit) return ServiceResult<CommissionTariffRowPage>.Ok(await SortedPageAsync(tenantId, connectionId, query, filter, page, pageSize, cancellationToken));
        // A page is a set of models (a row without a model code is a model of its own), each with all its variants, so a
        // model's variants are never split across two pages.
        var models = query.GroupBy(x => x.ModelCode != null && x.ModelCode != "" ? x.ModelCode : x.Barcode)
            .Select(group => new { Key = group.Key, Name = group.Min(x => x.ProductName) });
        var total = await models.CountAsync(cancellationToken);
        var keys = await models.OrderBy(x => x.Name).ThenBy(x => x.Key).Skip(page * pageSize).Take(pageSize).Select(x => x.Key).ToListAsync(cancellationToken);
        var rows = await query.Where(x => keys.Contains(x.ModelCode != null && x.ModelCode != "" ? x.ModelCode : x.Barcode))
            .OrderBy(x => x.ProductName).ThenBy(x => x.Barcode).ToListAsync(cancellationToken);
        return ServiceResult<CommissionTariffRowPage>.Ok(new(await ViewsAsync(tenantId, connectionId, rows, cancellationToken), total, page, pageSize));
    }

    // A page of models in the order asked for, of the variants whose band leaves what the profit filter asks. Both need
    // each band's price, commission and the panel's cost, so the filtered rows (a tariff holds a few thousand at most) are
    // worked here; a model with no value goes last either way.
    private async Task<CommissionTariffRowPage> SortedPageAsync(Guid tenantId, Guid connectionId, IQueryable<CommissionTariffRow> query, CommissionTariffRowFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var all = await query.ToListAsync(cancellationToken);
        var facts = await ProfitFactsAsync(tenantId, connectionId, all, cancellationToken);
        if (filter.FiltersProfit)
            all = all.Where(row => CommissionTariffChoices.Of(row, facts(row)).FirstOrDefault(x => x.Tier == filter.ProfitTier) is { Remaining: { } left } choice
                && (filter.MinProfit is not { } minProfit || left >= minProfit) && (filter.MaxProfit is not { } maxProfit || left <= maxProfit)
                && (filter.MinShare is not { } minShare || CommissionTariffChoices.OnCost(choice) >= minShare)
                && (filter.MaxShare is not { } maxShare || CommissionTariffChoices.OnCost(choice) <= maxShare)).ToList();
        var tier = filter.Sort is { Length: 5 } sort && sort.StartsWith("tier", StringComparison.Ordinal) && sort[4] is >= '1' and <= '4' ? sort[4] - '0' : (int?)null;
        var groups = all.GroupBy(x => string.IsNullOrEmpty(x.ModelCode) ? x.Barcode : x.ModelCode).Select(group =>
        {
            var name = group.Min(x => x.ProductName) ?? group.Key;
            IComparable? value = filter.Sort switch
            {
                "name" => name,
                "price" => group.Min(x => x.BasisPrice),
                _ when tier is { } band => group.Select(row => CommissionTariffChoices.Of(row, facts(row)).FirstOrDefault(x => x.Tier == band)?.Remaining).Max(),
                _ => null
            };
            return (Key: group.Key, Name: name, Value: value, Rows: group.ToList());
        }).ToList();
        var known = groups.Where(x => x.Value is not null);
        var ordered = (filter.Descending ? known.OrderByDescending(x => x.Value) : known.OrderBy(x => x.Value)).ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .Concat(groups.Where(x => x.Value is null).OrderBy(x => x.Name, StringComparer.CurrentCulture)).ToList();
        var rows = ordered.Skip(page * pageSize).Take(pageSize).SelectMany(x => x.Rows.OrderBy(row => row.ProductName).ThenBy(row => row.Barcode)).ToList();
        return new(await ViewsAsync(tenantId, connectionId, rows, cancellationToken), ordered.Count, page, pageSize);
    }

    // The rows of a page as the table shows them: matched to the panel, with cost, picture, colour and each band priced.
    private async Task<List<CommissionTariffRowView>> ViewsAsync(Guid tenantId, Guid connectionId, List<CommissionTariffRow> rows, CancellationToken cancellationToken)
    {
        var barcodes = rows.Select(x => x.Barcode).ToArray();
        var skus = rows.Where(x => x.SellerSku != null).Select(x => x.SellerSku!).ToArray();
        var matched = await PanelMatchesAsync(tenantId, connectionId, barcodes, skus, cancellationToken);
        var facts = await ProfitFactsAsync(tenantId, connectionId, rows, cancellationToken);
        var images = await ImagesAsync(tenantId, rows, cancellationToken);
        var colors = await ColorsAsync(tenantId, barcodes, skus, cancellationToken);
        var stocks = await StocksAsync(tenantId, barcodes, skus, cancellationToken);
        var items = rows.Select(row =>
        {
            var current = row.BasisPrice is { } basis ? CommissionTariffBands.BandFor(row, basis) : null;
            var next = row.BasisPrice is { } price ? CommissionTariffBands.NextLowerBand(row, price) : null;
            var fact = facts(row);
            var choices = CommissionTariffChoices.Of(row, fact);
            return new CommissionTariffRowView(row.Id, row.Barcode, row.SellerSku, row.ModelCode, row.ProductName, row.Size, row.BasisPrice, row.CurrentSalePrice, row.CurrentRate,
                current?.Tier, current?.Rate, CommissionTariffBands.Bands(row).Select(Band).ToList(), next is null ? null : Band(next),
                matched.Contains(row.Barcode) || row.SellerSku is not null && matched.Contains(row.SellerSku), fact.Cost, choices, CommissionTariffChoices.Recommended(choices),
                images.GetValueOrDefault(row.Id), colors.ByBarcode.GetValueOrDefault(row.Barcode) ?? (row.SellerSku is null ? null : colors.BySku.GetValueOrDefault(row.SellerSku)),
                stocks.TryGetValue(row.Barcode, out var stock) ? stock : row.SellerSku is not null && stocks.TryGetValue(row.SellerSku, out var skuStock) ? skuStock : row.Stock);
        }).ToList();
        return items;
    }

    public async Task<ServiceResult<CommissionTariffFacets>> FacetsAsync(Guid tenantId, Guid connectionId, Guid tariffId, CancellationToken cancellationToken)
    {
        if (!await db.CommissionTariffs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == tariffId, cancellationToken))
            return ServiceResult<CommissionTariffFacets>.Fail("RESOURCE_NOT_FOUND", "Komisyon tarifesi bulunamadı.", 404);
        var rows = db.CommissionTariffRows.AsNoTracking().Where(x => x.TenantId == tenantId && x.TariffId == tariffId);
        var categories = await rows.Where(x => x.Category != null).Select(x => x.Category!).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        var brands = await rows.Where(x => x.Brand != null).Select(x => x.Brand!).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        return ServiceResult<CommissionTariffFacets>.Ok(new(categories, brands));
    }

    // The category, brand and stock of a stored tariff's rows that have none, from the same file read again.
    private async Task<int> FillFactsAsync(Guid tenantId, Guid tariffId, IReadOnlyList<CommissionTariffRow> parsed, CancellationToken cancellationToken)
    {
        var facts = parsed.GroupBy(x => x.Barcode, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var stored = await db.CommissionTariffRows.Where(x => x.TenantId == tenantId && x.TariffId == tariffId && x.Category == null && x.Brand == null && x.Stock == null).ToListAsync(cancellationToken);
        var filled = 0;
        foreach (var row in stored)
        {
            if (!facts.TryGetValue(row.Barcode, out var fact) || fact.Category is null && fact.Brand is null && fact.Stock is null) continue;
            (row.Category, row.Brand, row.Stock) = (fact.Category, fact.Brand, fact.Stock);
            filled++;
        }
        return filled;
    }

    private static string? Term(string? value)
    {
        var term = value?.Trim();
        return string.IsNullOrEmpty(term) ? null : term.Length > 120 ? term[..120] : term;
    }

    private static string Like(string term) => $"%{CatalogService.EscapeLike(term)}%";

    // The seller's picks as Trendyol's own tariff workbook, ready to be uploaded back on Trendyol's Ürün Komisyon
    // Tarifeleri page: the uploaded file with each picked row's new price in "YENİ TSF (FİYAT GÜNCELLE)" and the tariff's
    // period in "Tarife Seçimi", every other row left empty there (no change). Without the uploaded file (a tariff
    // stored before files were kept) the workbook is written again from the stored rows in Trendyol's column order.
    // Without picks in the request the saved picks are written, so the file holds what the page shows.
    // A file with more than one period (Trendyol's "3 Gün" and "4 Gün") carries every period's saved picks, the exported
    // tariff's own taken from the request: a row picked in one period gets that period's choice ("4 Günlük Fiyat"), one
    // picked at the same price in every period the choice of them all ("7 Günlük Fiyat"), and one picked at different
    // prices the exported period's price (the file holds one price per row), counted in Conflicts.
    public async Task<ServiceResult<CommissionTariffExportFile>> ExportAsync(Guid tenantId, Guid connectionId, Guid tariffId, CommissionTariffExportRequest request, CancellationToken cancellationToken)
    {
        var tariff = await db.CommissionTariffs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == tariffId, cancellationToken);
        if (tariff is null) return ServiceResult<CommissionTariffExportFile>.Fail("RESOURCE_NOT_FOUND", "Komisyon tarifesi bulunamadı.", 404);
        if (request.Bulk is null && request.Choices is null) request = Request(await SelectionViewAsync(tenantId, tariffId, cancellationToken));
        if (Invalid(request, out _) is { } invalid) return ServiceResult<CommissionTariffExportFile>.Fail(invalid.Code, invalid.Message, invalid.Status);
        var periods = await PeriodsAsync(tariff, cancellationToken);
        var picks = new List<TrendyolTariffWorkbook.Pick>();
        List<CommissionTariffRow> rows = [];
        foreach (var period in periods)
        {
            var own = period.Id == tariff.Id;
            var periodRequest = own ? request : Request(await SelectionViewAsync(tenantId, period.Id, cancellationToken));
            // Saved picks of another period always read; ones that no longer do are left out rather than failing the file.
            if (!own && Invalid(periodRequest, out _) is not null) continue;
            var periodRows = await db.CommissionTariffRows.AsNoTracking().Where(x => x.TenantId == tenantId && x.TariffId == period.Id)
                .OrderBy(x => x.ProductName).ThenBy(x => x.Barcode).ToListAsync(cancellationToken);
            if (own) rows = periodRows;
            var days = TrendyolTariffWorkbook.PeriodDays(period.PeriodStart, period.PeriodEnd);
            foreach (var (row, price) in await ChosenAsync(tenantId, connectionId, periodRows, periodRequest, cancellationToken))
                picks.Add(new(row.Barcode, period.PeriodText, days, price, own));
        }
        var tariffDays = TrendyolTariffWorkbook.PeriodDays(tariff.PeriodStart, tariff.PeriodEnd);
        var name = $"komisyon-tarifesi-secimler-{periods[0].PeriodStart.ToOffset(CommissionTariffFileParser.TurkeyOffset):yyyyMMdd}.xlsx";
        var periodIds = periods.Select(x => x.Id).ToArray();
        // The periods of a file keep the same file; the exported tariff's own copy first.
        var source = (await db.CommissionTariffSourceFiles.AsNoTracking().Where(x => x.TenantId == tenantId && periodIds.Contains(x.TariffId))
            .Select(x => new { x.TariffId, x.Content }).ToListAsync(cancellationToken)).OrderBy(x => x.TariffId == tariffId ? 0 : 1).Select(x => x.Content).FirstOrDefault();
        if (source is not null)
        {
            try
            {
                if (TrendyolTariffWorkbook.Fill(source, periods.Select(x => x.PeriodText).ToList(), picks) is { } filled)
                    return ServiceResult<CommissionTariffExportFile>.Ok(new(filled.Content, name, filled.Filled, Conflicts: filled.Conflicts));
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
            {
                // A kept file that no longer reads is written again from the rows below.
            }
        }
        // Written again from the exported period's rows, each with what its periods' picks resolve to.
        var byBarcode = picks.GroupBy(x => x.Barcode, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        var chosen = new Dictionary<Guid, (decimal Price, decimal Rate)>();
        var choiceDays = new Dictionary<Guid, int>();
        var conflicts = 0;
        foreach (var row in rows)
        {
            if (!byBarcode.TryGetValue(row.Barcode, out var rowPicks) || TrendyolTariffWorkbook.Resolve(rowPicks) is not { } resolved) continue;
            chosen[row.Id] = (resolved.Price, CommissionTariffBands.BandFor(row, resolved.Price)?.Rate ?? 0m);
            if (resolved.Days != tariffDays) choiceDays[row.Id] = resolved.Days;
            if (resolved.Conflict) conflicts++;
        }
        var written = TrendyolTariffWorkbook.Write(tariffDays, rows, tariff.PeriodText, chosen, choiceDays);
        return ServiceResult<CommissionTariffExportFile>.Ok(new(written.Content, name, written.Filled, FromSourceFile: false, Conflicts: conflicts));
    }

    // The price each row of a period gets from the period's picks: its own pick (a band or a price of the seller's own),
    // else the pick for every row; rows with neither are left out.
    private async Task<List<(CommissionTariffRow Row, decimal Price)>> ChosenAsync(Guid tenantId, Guid connectionId, List<CommissionTariffRow> rows, CommissionTariffExportRequest request, CancellationToken cancellationToken)
    {
        Invalid(request, out var bulk);
        var picks = (request.Choices ?? []).GroupBy(x => x.RowId).ToDictionary(x => x.Key, x => x.Last());
        var facts = await ProfitFactsAsync(tenantId, connectionId, rows, cancellationToken);
        var chosen = new List<(CommissionTariffRow, decimal)>();
        foreach (var row in rows)
        {
            var fact = facts(row);
            CommissionChoiceView? choice;
            if (picks.TryGetValue(row.Id, out var pick))
                choice = pick.Price is { } own ? CommissionTariffChoices.PricedAt(row, own, fact) : pick.Tier is { } tier ? CommissionTariffChoices.Of(row, fact).FirstOrDefault(x => x.Tier == tier) : null;
            else choice = string.IsNullOrEmpty(bulk) ? null : CommissionTariffChoices.Pick(CommissionTariffChoices.Of(row, fact), bulk, request.Tier, request.MinRate, request.MinAmount, request.MaxRate, request.MaxAmount);
            if (choice is not null) chosen.Add((row, choice.Price));
        }
        return chosen;
    }

    public async Task<ServiceResult<CommissionTariffSelectionView>> SelectionAsync(Guid tenantId, Guid connectionId, Guid tariffId, CancellationToken cancellationToken)
    {
        if (!await db.CommissionTariffs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == tariffId, cancellationToken))
            return ServiceResult<CommissionTariffSelectionView>.Fail("RESOURCE_NOT_FOUND", "Komisyon tarifesi bulunamadı.", 404);
        return ServiceResult<CommissionTariffSelectionView>.Ok(await SelectionViewAsync(tenantId, tariffId, cancellationToken));
    }

    // The saved picks replaced as a whole (the page sends its whole state after each change). Saves of one tariff run
    // one at a time (the tariff's row is locked), so two quick saves never mix.
    public async Task<ServiceResult<CommissionTariffSelectionView>> SaveSelectionAsync(Guid tenantId, Guid? userId, Guid connectionId, Guid tariffId, CommissionTariffExportRequest request, CancellationToken cancellationToken)
    {
        if (!await db.CommissionTariffs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == tariffId, cancellationToken))
            return ServiceResult<CommissionTariffSelectionView>.Fail("RESOURCE_NOT_FOUND", "Komisyon tarifesi bulunamadı.", 404);
        if (Invalid(request, out var bulk) is { } invalid) return ServiceResult<CommissionTariffSelectionView>.Fail(invalid.Code, invalid.Message, invalid.Status);
        var choices = (request.Choices ?? []).GroupBy(x => x.RowId).Select(x => x.Last()).ToList();
        var ids = choices.Select(x => x.RowId).ToArray();
        var known = ids.Length == 0 ? [] : (await db.CommissionTariffRows.AsNoTracking().Where(x => x.TenantId == tenantId && x.TariffId == tariffId && ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM catalog.commission_tariffs WHERE "TenantId"={tenantId} AND "Id"={tariffId} FOR UPDATE""", cancellationToken);
        await db.CommissionTariffPicks.Where(x => x.TenantId == tenantId && x.TariffId == tariffId).ExecuteDeleteAsync(cancellationToken);
        var selection = await db.CommissionTariffSelections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.TariffId == tariffId, cancellationToken);
        if (selection is null) db.CommissionTariffSelections.Add(selection = new CommissionTariffSelection { TariffId = tariffId, TenantId = tenantId });
        selection.BulkKind = string.IsNullOrEmpty(bulk) ? null : bulk;
        selection.BulkTier = bulk == "tier" ? request.Tier : null;
        (selection.MinRate, selection.MinAmount, selection.MaxRate, selection.MaxAmount) = selection.BulkKind is null ? (null, null, null, null) : (request.MinRate, request.MinAmount, request.MaxRate, request.MaxAmount);
        selection.UpdatedAt = now;
        selection.UpdatedBy = userId;
        db.CommissionTariffPicks.AddRange(choices.Where(x => known.Contains(x.RowId))
            .Select(x => new CommissionTariffPick { TariffId = tariffId, RowId = x.RowId, TenantId = tenantId, Tier = x.Price is null ? x.Tier : null, Price = x.Price is { } price ? decimal.Round(price, 2, MidpointRounding.AwayFromZero) : null }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return ServiceResult<CommissionTariffSelectionView>.Ok(await SelectionViewAsync(tenantId, tariffId, cancellationToken));
    }

    private async Task<CommissionTariffSelectionView> SelectionViewAsync(Guid tenantId, Guid tariffId, CancellationToken cancellationToken)
    {
        var selection = await db.CommissionTariffSelections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.TariffId == tariffId, cancellationToken);
        var picks = await db.CommissionTariffPicks.AsNoTracking().Where(x => x.TenantId == tenantId && x.TariffId == tariffId)
            .OrderBy(x => x.RowId).Select(x => new CommissionTariffChoice(x.RowId, x.Tier, x.Price)).ToListAsync(cancellationToken);
        return new(selection?.BulkKind, selection?.BulkTier, selection?.MinRate, selection?.MinAmount, selection?.MaxRate, selection?.MaxAmount, picks, selection?.UpdatedAt);
    }

    private static CommissionTariffExportRequest Request(CommissionTariffSelectionView view) =>
        new(view.Bulk ?? string.Empty, view.Tier, view.Choices, view.MinRate, view.MinAmount, view.MaxRate, view.MaxAmount);

    // The error of picks that cannot be kept or written, with the bulk kind read ("recommended" is the old "amount").
    private static ServiceError? Invalid(CommissionTariffExportRequest request, out string? bulk)
    {
        bulk = request.Bulk?.Trim().ToLowerInvariant() switch { "recommended" => "amount", var value => value };
        if (bulk is not (null or "" or "tier" or "amount" or "rate") || bulk == "tier" && request.Tier is not (>= 1 and <= 4)
            || request.MinRate is < -1000 or > 100 || request.MinAmount is < -10_000_000 or > 10_000_000
            || request.MaxRate is < -1000 or > 100 || request.MaxAmount is < -10_000_000 or > 10_000_000)
            return new("COMMISSION_TARIFF_EXPORT_INVALID", "Toplu seçim geçersiz.", 400);
        if (request.Choices is { Count: > 50_000 } || request.Choices?.Any(x => x.Tier is not (null or (>= 1 and <= 4)) || x.Price is <= 0 or > 10_000_000) == true)
            return new("COMMISSION_TARIFF_EXPORT_INVALID", "Seçimlerden biri geçersiz.", 400);
        return null;
    }

    // What each row's profit needs, from the panel's variant with the row's barcode (else its seller SKU): its cost and
    // the VAT rate of its offer on this store, and the store's current cargo tariff (ReportsService.CargoTariffAsync).
    private async Task<Func<CommissionTariffRow, UnitProfitFacts>> ProfitFactsAsync(Guid tenantId, Guid connectionId, IReadOnlyList<CommissionTariffRow> rows, CancellationToken cancellationToken)
    {
        var barcodes = rows.Select(x => x.Barcode).ToArray();
        var skus = rows.Where(x => x.SellerSku != null).Select(x => x.SellerSku!).ToArray();
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Barcode != null && barcodes.Contains(x.Barcode) || skus.Contains(x.Sku)))
            .Select(x => new { x.Id, x.Barcode, x.Sku, x.CostPrice })
            .ToListAsync(cancellationToken);
        var ids = variants.Select(x => x.Id).ToArray();
        var vats = (await db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && ids.Contains(x.VariantId) && x.VatRate > 0)
            .Select(x => new { x.VariantId, x.VatRate }).ToListAsync(cancellationToken)).GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First().VatRate);
        var cargo = (await ReportsService.CargoTariffAsync(db, tenantId, connectionId, timeProvider.GetUtcNow(), cancellationToken))?.PerOrder;
        var byBarcode = new Dictionary<string, UnitProfitFacts>(StringComparer.Ordinal);
        var bySku = new Dictionary<string, UnitProfitFacts>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var fact = new UnitProfitFacts(variant.CostPrice is > 0 ? variant.CostPrice : null, vats.TryGetValue(variant.Id, out var vat) ? vat : null, cargo);
            if (variant.Barcode is not null) byBarcode.TryAdd(variant.Barcode, fact);
            bySku.TryAdd(variant.Sku, fact);
        }
        var none = new UnitProfitFacts(null, null, cargo);
        return row => byBarcode.GetValueOrDefault(row.Barcode) ?? (row.SellerSku is null ? null : bySku.GetValueOrDefault(row.SellerSku)) ?? none;
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

    // Each code's stock on the panel: the variant with it as barcode or SKU, on hand in the main location.
    private async Task<Dictionary<string, decimal>> StocksAsync(Guid tenantId, string[] barcodes, string[] skus, CancellationToken cancellationToken)
    {
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Barcode != null && barcodes.Contains(x.Barcode) || skus.Contains(x.Sku)))
            .Select(x => new { x.Id, x.Barcode, x.Sku }).ToListAsync(cancellationToken);
        var ids = variants.Select(x => x.Id).ToArray();
        var onHand = await db.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && ids.Contains(x.VariantId))
            .GroupBy(x => x.VariantId).Select(group => new { VariantId = group.Key, OnHand = group.Sum(x => x.OnHand) }).ToDictionaryAsync(x => x.VariantId, x => x.OnHand, cancellationToken);
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var variant in variants.Where(x => onHand.ContainsKey(x.Id)))
        {
            if (variant.Barcode is not null) result.TryAdd(variant.Barcode, onHand[variant.Id]);
            result.TryAdd(variant.Sku, onHand[variant.Id]);
        }
        return result;
    }

    internal static string? ColorOf(string? optionSignature) =>
        optionSignature?.Split('|').Select(part => part.Split(':', 2)).Where(pair => pair.Length == 2)
            .Where(pair => pair[0].Trim().Equals("Renk", StringComparison.OrdinalIgnoreCase) || pair[0].Trim().Equals("Color", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair[1].Trim()).FirstOrDefault(value => value.Length > 0);

    public async Task<ServiceResult<bool>> DeleteAsync(Guid tenantId, Guid? userId, Guid connectionId, Guid tariffId, string correlationId, CancellationToken cancellationToken)
    {
        var tariff = await db.CommissionTariffs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == tariffId, cancellationToken);
        if (tariff is null) return ServiceResult<bool>.Fail("RESOURCE_NOT_FOUND", "Komisyon tarifesi bulunamadı.", 404);
        // Every period of the file goes, the page listing them as one tariff; the rows, picks and kept file go with each
        // period (the database cascades the delete).
        var periods = await db.CommissionTariffs.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.FileHash == tariff.FileHash).OrderBy(x => x.PeriodStart).ToListAsync(cancellationToken);
        db.CommissionTariffs.RemoveRange(periods);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "COMMISSION_TARIFF_DELETED", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = $"{tariff.FileName}: {string.Join(", ", periods.Select(x => x.PeriodText))}", CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    private async Task<CommissionTariffView> ViewAsync(CommissionTariff tariff, DateTimeOffset now, Guid groupId, CancellationToken cancellationToken)
    {
        // A row counts as matched when a panel variant carries its barcode or seller SKU, or this connection's link
        // carries the barcode.
        var matched = await db.CommissionTariffRows.AsNoTracking()
            .Where(row => row.TenantId == tariff.TenantId && row.TariffId == tariff.Id
                && (db.ProductVariants.Any(variant => variant.TenantId == tariff.TenantId && (variant.Barcode == row.Barcode || row.SellerSku != null && variant.Sku == row.SellerSku))
                    || db.MarketplaceVariantLinks.Any(link => link.TenantId == tariff.TenantId && link.ConnectionId == tariff.ConnectionId && link.ExternalBarcode == row.Barcode)))
            .CountAsync(cancellationToken);
        return new(tariff.Id, tariff.PlatformCode, tariff.PeriodStart, tariff.PeriodEnd, tariff.PeriodText, tariff.FileName, tariff.UploadedAt, tariff.RowCount, matched,
            now >= tariff.PeriodStart && now < tariff.PeriodEnd, tariff.PeriodLabel, groupId);
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
    public static List<CommissionChoiceView> Of(CommissionTariffRow row, UnitProfitFacts facts) =>
        CommissionTariffBands.Bands(row)
            .Select(band => CommissionTariffBands.TargetPrice(band, row.BasisPrice) is { } price and > 0 ? Priced(band, price, facts) : null)
            .OfType<CommissionChoiceView>().ToList();

    // A price of the seller's own, in the band it falls in (none when it falls in no band).
    public static CommissionChoiceView? PricedAt(CommissionTariffRow row, decimal price, UnitProfitFacts facts)
    {
        var rounded = decimal.Round(price, 2, MidpointRounding.AwayFromZero);
        return CommissionTariffBands.BandFor(row, rounded) is { } band ? Priced(band, rounded, facts) : null;
    }

    private static CommissionChoiceView Priced(CommissionBand band, decimal price, UnitProfitFacts facts)
    {
        var commission = decimal.Round(price * band.Rate / 100m, 2, MidpointRounding.AwayFromZero);
        var (profit, costs) = UnitProfit.Of(price, commission, facts);
        return new(band.Tier, price, band.Rate, commission, profit, costs);
    }

    // The band that leaves the most profit; none when the profit is not known.
    public static int? Recommended(IReadOnlyList<CommissionChoiceView> choices) => Pick(choices, "amount", null, null, null)?.Tier;

    // The profit as a share of the product cost (Kâr Oranı, as the order profit report and Melontik give it); unknown
    // without a profit or a cost above zero.
    public static decimal? OnCost(CommissionChoiceView choice) =>
        choice is { Remaining: { } left, Costs.Cost: > 0 } ? Math.Round(left / choice.Costs.Cost * 100m, 2, MidpointRounding.AwayFromZero) : null;

    // The pick for every row: one band ("tier"), the band leaving the most ("amount") or the largest profit on its cost
    // ("rate"); none when it leaves less than MinRate per cent of the cost or MinAmount lira, more than MaxRate or
    // MaxAmount, or the profit is not known for a comparison that needs it.
    public static CommissionChoiceView? Pick(IReadOnlyList<CommissionChoiceView> choices, string kind, int? tier, decimal? minRate, decimal? minAmount,
        decimal? maxRate = null, decimal? maxAmount = null)
    {
        var choice = kind switch
        {
            "tier" => choices.FirstOrDefault(x => x.Tier == tier),
            "amount" => choices.Where(x => x.Remaining is not null).OrderByDescending(x => x.Remaining).ThenBy(x => x.Rate).FirstOrDefault(),
            "rate" => choices.Where(x => OnCost(x) is not null).OrderByDescending(OnCost).ThenBy(x => x.Rate).FirstOrDefault(),
            _ => null
        };
        if (choice is null) return null;
        if (minRate is { } rate && (OnCost(choice) is not { } share || share < rate)) return null;
        if (minAmount is { } amount && (choice.Remaining is not { } kept || kept < amount)) return null;
        if (maxRate is { } top && (OnCost(choice) is not { } over || over > top)) return null;
        if (maxAmount is { } most && (choice.Remaining is not { } above || above > most)) return null;
        return choice;
    }
}
