using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The products page's export and import: every listed variant to a workbook, and ProductVariant.CostPrice and Desi back
// from it. Only the catalog's own values change: nothing is queued for a marketplace, and the product's version is left
// alone, since a version bump would read as a local content edit and keep later marketplace imports from refreshing the
// product's title and description.
public sealed class ProductTransferService(AppDbContext db, TimeProvider timeProvider) : IProductTransferService
{
    public const long MaximumFileBytes = 10 * 1024 * 1024;
    public const int MaximumChanges = ProductTransferFileParser.MaximumRows;
    private const long MaximumExpandedBytes = 64 * 1024 * 1024;
    private const int Chunk = 2_000;

    public async Task<ServiceResult<ProductTransferSummaryView>> SummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var variants = ListedVariants(tenantId);
        var total = await variants.CountAsync(cancellationToken);
        var missingCost = await variants.CountAsync(variant => variant.CostPrice == null, cancellationToken);
        var missingDesi = await variants.CountAsync(variant => variant.Desi == null, cancellationToken);
        return ServiceResult<ProductTransferSummaryView>.Ok(new ProductTransferSummaryView(total, missingCost, missingDesi));
    }

    public async Task<ServiceResult<byte[]>> ExportAsync(Guid tenantId, ProductTransferExportFilter filter, Guid? brandId, Guid? categoryId, CancellationToken cancellationToken)
    {
        var variants = ListedVariants(tenantId);
        if (filter == ProductTransferExportFilter.MissingCost) variants = variants.Where(variant => variant.CostPrice == null);
        if (filter == ProductTransferExportFilter.MissingDesi) variants = variants.Where(variant => variant.Desi == null);
        var rows = await (
            from variant in variants
            join product in db.Products on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
            where (brandId == null || product.BrandId == brandId) && (categoryId == null || product.CategoryId == categoryId)
            join brand in db.Brands on new { product.TenantId, Id = product.BrandId ?? Guid.Empty } equals new { brand.TenantId, brand.Id } into brands
            from brand in brands.DefaultIfEmpty()
            join category in db.Categories on new { product.TenantId, Id = product.CategoryId ?? Guid.Empty } equals new { category.TenantId, category.Id } into categories
            from category in categories.DefaultIfEmpty()
            orderby product.Title, product.Id, variant.SortOrder, variant.Sku
            select new
            {
                variant.Id,
                product.Title,
                ProductStatus = product.Status,
                variant.OptionSignature,
                variant.Sku,
                variant.Barcode,
                variant.ModelCode,
                Brand = brand == null ? null : brand.Name,
                Category = category == null ? null : category.Name,
                variant.DefaultSalePrice,
                ProductSalePrice = product.DefaultSalePrice,
                variant.CostPrice,
                variant.Desi
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Price, stock and VAT as the product list shows them: the first offer (an active one before others) and the
        // main location's stock. They are for reference; the import never reads them.
        var offers = await db.ChannelOffers.AsNoTracking()
            .Where(offer => offer.TenantId == tenantId)
            .OrderByDescending(offer => offer.Status == "ACTIVE").ThenBy(offer => offer.Id)
            .Select(offer => new { offer.VariantId, offer.SalePrice, offer.VatRate })
            .ToListAsync(cancellationToken);
        var offerByVariant = offers.GroupBy(offer => offer.VariantId).ToDictionary(group => group.Key, group => group.First());
        var stockByVariant = (await db.InventoryItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LocationCode == "MAIN")
            .Select(item => new { item.VariantId, item.OnHand })
            .ToListAsync(cancellationToken))
            .GroupBy(item => item.VariantId).ToDictionary(group => group.Key, group => group.First().OnHand);

        var data = new List<IReadOnlyList<object?>>(rows.Count + 1)
        {
            new object?[] { "Ürün Adı", "Varyant", "Stok Kodu", "Barkod", "Model Kodu", "Marka", "Kategori", "Durum", "Satış Fiyatı", "Stok", "KDV Oranı", "Maliyet (₺)", "Desi" }
        };
        data.AddRange(rows.Select(row =>
        {
            var offer = offerByVariant.GetValueOrDefault(row.Id);
            return (IReadOnlyList<object?>)new object?[]
            {
                row.Title,
                NullIfEmpty(row.OptionSignature),
                row.Sku,
                row.Barcode,
                row.ModelCode,
                row.Brand,
                row.Category,
                row.ProductStatus == ProductStatus.Draft ? "Taslak" : "Satışa Açık",
                Rounded(CatalogPricePolicy.EffectiveSalePrice(offer?.SalePrice, row.DefaultSalePrice, row.ProductSalePrice)),
                stockByVariant.TryGetValue(row.Id, out var onHand) ? onHand : 0m,
                offer is null ? null : "%" + offer.VatRate.ToString("0.##", CultureInfo.GetCultureInfo("tr-TR")),
                Rounded(row.CostPrice),
                Rounded(row.Desi)
            };
        }));
        var notes = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Ürün dosyası nasıl düzenlenir?" },
            new object?[] { "Her satır bir varyanttır. Yüklediğinizde yalnız Maliyet (₺) ve Desi sütunları okunur; diğer sütunlar bilgi içindir ve değiştirmeniz hiçbir şeyi değiştirmez." },
            new object?[] { "Maliyet: 1 adetin Türk lirası maliyeti, ürün kartındaki \"Maliyet\" alanıyla aynı değer. Kâr raporu bu değeri, müşterinin ödediği KDV dahil satış tutarından düşer. 0 geçerli bir maliyettir." },
            new object?[] { "Desi: kargo hacim ağırlığı. Raporlar kargo ücretini, pazaryeri kargo tutarı gelmediğinde bu değerle tahmin eder. 0'dan büyük olmalıdır." },
            new object?[] { "Sayılar 123,45 ya da 1.234,56 biçiminde yazılabilir; ₺ ve TL işaretleri yok sayılır. Yalnız noktalı 1.234 belirsizdir ve reddedilir: 1234 ya da 1,234 yazın." },
            new object?[] { "Boş hücre değiştirilmez. Kayıtlı bir değeri silmek için hücreye - ya da sil yazın." },
            new object?[] { "Satır önce Stok Kodu, bulunamazsa Barkod ile eşleştirilir. Bu iki sütunu değiştirmeyin." },
            new object?[] { "Satış fiyatı, stok ve KDV oranı bu dosyayla değiştirilmez: bunları ürün düzenleme sayfasından değiştirin." },
            new object?[] { "Dosyayı yükledikten sonra değişiklikler önce önizlenir; siz onaylamadan hiçbir şey kaydedilmez. Pazaryerlerine hiçbir şey gönderilmez." },
            new object?[] { "Dosyayı .xlsx olarak kaydedin (CSV de kabul edilir) ve en fazla 20.000 satır, 10 MiB yükleyin. Formül içeren hücreler okunmaz." }
        };
        var workbook = SimpleXlsxWriter.Write(
        [
            new SimpleXlsxSheet("Ürünler", data, [48, 26, 22, 18, 18, 18, 22, 13, 14, 10, 11, 14, 10]),
            new SimpleXlsxSheet("Açıklama", notes, [140])
        ]);
        return ServiceResult<byte[]>.Ok(workbook);
    }

    public async Task<ServiceResult<ProductTransferImportPreview>> PreviewAsync(Guid tenantId, string fileName, Stream content, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length is 0 or > MaximumFileBytes)
            return ServiceResult<ProductTransferImportPreview>.Fail("PRODUCT_TRANSFER_FILE_SIZE", "Dosya boş olamaz ve en fazla 10 MiB olabilir.", 413);
        var safeName = Path.GetFileName(fileName);
        if (safeName.Length > 260) safeName = safeName[..260];
        IReadOnlyList<ProductTransferFileRow> rows;
        try
        {
            buffer.Position = 0;
            rows = string.Equals(Path.GetExtension(safeName), ".csv", StringComparison.OrdinalIgnoreCase)
                ? ProductTransferFileParser.ParseCsv(buffer)
                : ProductTransferFileParser.ParseXlsx(buffer, MaximumExpandedBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
        {
            return ServiceResult<ProductTransferImportPreview>.Fail("PRODUCT_TRANSFER_FILE_INVALID", exception is InvalidDataException ? exception.Message : "Dosya okunamadı; Ravencia'dan indirilen ürün dosyasını yükleyin.", 422);
        }
        if (rows.Count == 0)
            return ServiceResult<ProductTransferImportPreview>.Fail("PRODUCT_TRANSFER_FILE_EMPTY", "Dosyada ürün satırı yok.", 422);

        // Every variant of the tenant, archived ones included: a row is matched against the catalog as it is, and the
        // keys are compared in memory with the shared key so legacy stored forms ("ABC_01") still match.
        var candidates = await (
            from variant in db.ProductVariants.AsNoTracking()
            where variant.TenantId == tenantId
            join product in db.Products.AsNoTracking() on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
            select new ProductTransferCandidate(variant.Id, variant.ProductId, variant.Version, variant.Sku, variant.Barcode, product.Title, variant.OptionSignature, variant.CostPrice, variant.Desi))
            .ToListAsync(cancellationToken);
        var preview = ProductTransferImportPlanner.Plan(safeName, rows, candidates);
        return ServiceResult<ProductTransferImportPreview>.Ok(preview);
    }

    public async Task<ServiceResult<ProductTransferApplyResult>> ApplyAsync(Guid tenantId, Guid userId, ApplyProductTransferCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var changes = command.Changes ?? [];
        if (changes.Count == 0) return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_CHANGES_REQUIRED", "Kaydedilecek değişiklik yok.", 400);
        if (changes.Count > MaximumChanges) return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_CHANGES_TOO_MANY", $"Tek seferde en fazla {MaximumChanges:N0} varyant kaydedilebilir.", 400);
        if (changes.Select(change => change.VariantId).Distinct().Count() != changes.Count)
            return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_CHANGES_DUPLICATE", "Aynı varyant birden çok kez gönderildi.", 400);
        if (changes.Any(change => !change.SetCost && !change.SetDesi))
            return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_CHANGES_EMPTY", "Her değişiklik maliyet ya da desiyi değiştirmelidir.", 400);
        if (changes.Any(change => change.SetCost && (change.Cost is < 0 or > ProductTransferFileParser.MaximumCost || change.Cost is decimal cost && decimal.Round(cost, 2) != cost)))
            return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_COST_INVALID", "Maliyet 0 ile 10.000.000 ₺ arasında ve en fazla iki ondalıklı olmalıdır.", 400);
        if (changes.Any(change => change.SetDesi && (change.Desi is <= 0 or > ProductTransferFileParser.MaximumDesi || change.Desi is decimal desi && decimal.Round(desi, 2) != desi)))
            return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_DESI_INVALID", "Desi 0'dan büyük, en fazla 10.000 ve en fazla iki ondalıklı olmalıdır.", 400);

        var ids = changes.Select(change => change.VariantId).ToArray();
        var variants = new List<ProductVariant>(ids.Length);
        foreach (var chunk in ids.Chunk(Chunk))
            variants.AddRange(await db.ProductVariants.Where(variant => variant.TenantId == tenantId && chunk.Contains(variant.Id)).ToListAsync(cancellationToken));
        if (variants.Count != ids.Length)
            return Stale("Bazı varyantlar artık katalogda yok; dosyayı yeniden yükleyin.");

        var byId = variants.ToDictionary(variant => variant.Id);
        // A variant that already holds the requested values is done (a repeated save, or the same edit made elsewhere);
        // any other change since the preview was read means the preview is out of date and nothing is written. Every
        // variant is checked before any is changed, so a refusal leaves nothing half applied.
        if (changes.Any(change => byId[change.VariantId] is var variant && !Holds(variant, change) && variant.Version != change.Version))
            return Stale("Bazı varyantlar önizlemeden sonra değişti; dosyayı yeniden yükleyin.");

        var now = timeProvider.GetUtcNow();
        int written = 0, costsUpdated = 0, costsCleared = 0, desiUpdated = 0, desiCleared = 0;
        var products = new HashSet<Guid>();
        foreach (var change in changes)
        {
            var variant = byId[change.VariantId];
            if (Holds(variant, change)) continue;
            if (change.SetCost && variant.CostPrice != change.Cost)
            {
                variant.CostPrice = change.Cost;
                if (change.Cost is null) costsCleared++; else costsUpdated++;
            }
            if (change.SetDesi && variant.Desi != change.Desi)
            {
                variant.Desi = change.Desi;
                if (change.Desi is null) desiCleared++; else desiUpdated++;
            }
            variant.UpdatedAt = now;
            variant.Version++;
            products.Add(variant.ProductId);
            written++;
        }
        if (written == 0) return ServiceResult<ProductTransferApplyResult>.Ok(new ProductTransferApplyResult(0, 0, 0, 0, 0, 0));

        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = "PRODUCT_TRANSFER_IMPORTED",
            TargetType = "ProductVariant",
            TargetId = null,
            Reason = $"Ürün dosyası içe aktarıldı: {written} varyant ({products.Count} ürün); maliyet {costsUpdated} güncellendi, {costsCleared} silindi; desi {desiUpdated} güncellendi, {desiCleared} silindi.",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Stale("Bazı varyantlar kayıt sırasında değişti; hiçbir değişiklik kaydedilmedi. Dosyayı yeniden yükleyin.");
        }
        return ServiceResult<ProductTransferApplyResult>.Ok(new ProductTransferApplyResult(written, costsUpdated, costsCleared, desiUpdated, desiCleared, products.Count));
    }

    private static bool Holds(ProductVariant variant, ProductTransferChange change) =>
        (!change.SetCost || variant.CostPrice == change.Cost) && (!change.SetDesi || variant.Desi == change.Desi);

    // The variants the export lists and the summary counts: those of products that are not archived, themselves not
    // archived.
    private IQueryable<ProductVariant> ListedVariants(Guid tenantId) =>
        from variant in db.ProductVariants.AsNoTracking()
        where variant.TenantId == tenantId && variant.Status != ProductStatus.Archived
        join product in db.Products.AsNoTracking() on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
        where product.Status != ProductStatus.Archived
        select variant;

    private static ServiceResult<ProductTransferApplyResult> Stale(string message) => ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_STALE", message, 409);
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static decimal? Rounded(decimal? value) => value is decimal number ? Math.Round(number, 2, MidpointRounding.AwayFromZero) : null;
}
