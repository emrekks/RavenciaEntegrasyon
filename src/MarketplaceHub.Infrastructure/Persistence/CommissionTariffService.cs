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

    public async Task<ServiceResult<CommissionTariffRowPage>> RowsAsync(Guid tenantId, Guid connectionId, Guid tariffId, string? search, int page, int pageSize, CancellationToken cancellationToken)
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
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.ProductName).ThenBy(x => x.Barcode).Skip(page * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var barcodes = rows.Select(x => x.Barcode).ToArray();
        var skus = rows.Where(x => x.SellerSku != null).Select(x => x.SellerSku!).ToArray();
        var matched = await PanelMatchesAsync(tenantId, connectionId, barcodes, skus, cancellationToken);
        var items = rows.Select(row =>
        {
            var current = row.BasisPrice is { } basis ? CommissionTariffBands.BandFor(row, basis) : null;
            var next = row.BasisPrice is { } price ? CommissionTariffBands.NextLowerBand(row, price) : null;
            return new CommissionTariffRowView(row.Id, row.Barcode, row.SellerSku, row.ModelCode, row.ProductName, row.Size, row.BasisPrice, row.CurrentSalePrice, row.CurrentRate,
                current?.Tier, current?.Rate, CommissionTariffBands.Bands(row).Select(Band).ToList(), next is null ? null : Band(next),
                matched.Contains(row.Barcode) || row.SellerSku is not null && matched.Contains(row.SellerSku));
        }).ToList();
        return ServiceResult<CommissionTariffRowPage>.Ok(new(items, total, page, pageSize));
    }

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
