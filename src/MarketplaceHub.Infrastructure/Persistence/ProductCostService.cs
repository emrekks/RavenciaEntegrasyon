using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Bulk entry of ProductVariant.CostPrice from a spreadsheet. Only the catalog's own cost changes: nothing is queued for a
// marketplace, and the product's version is left alone, since a version bump would read as a local content edit and keep
// later marketplace imports from refreshing the product's title and description.
public sealed class ProductCostService(AppDbContext db, TimeProvider timeProvider) : IProductCostService
{
    public const long MaximumFileBytes = 10 * 1024 * 1024;
    public const int MaximumChanges = ProductCostFileParser.MaximumRows;
    private const long MaximumExpandedBytes = 64 * 1024 * 1024;
    private const int Chunk = 2_000;

    public async Task<ServiceResult<ProductCostSummaryView>> SummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var variants = ListedVariants(tenantId);
        var total = await variants.CountAsync(cancellationToken);
        var missing = await variants.CountAsync(variant => variant.CostPrice == null, cancellationToken);
        return ServiceResult<ProductCostSummaryView>.Ok(new ProductCostSummaryView(total, missing));
    }

    public async Task<ServiceResult<byte[]>> TemplateAsync(Guid tenantId, bool missingOnly, Guid? brandId, Guid? categoryId, CancellationToken cancellationToken)
    {
        var variants = ListedVariants(tenantId);
        if (missingOnly) variants = variants.Where(variant => variant.CostPrice == null);
        var rows = await (
            from variant in variants
            join product in db.Products on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
            where (brandId == null || product.BrandId == brandId) && (categoryId == null || product.CategoryId == categoryId)
            join brand in db.Brands on new { product.TenantId, Id = product.BrandId ?? Guid.Empty } equals new { brand.TenantId, brand.Id } into brands
            from brand in brands.DefaultIfEmpty()
            join category in db.Categories on new { product.TenantId, Id = product.CategoryId ?? Guid.Empty } equals new { category.TenantId, category.Id } into categories
            from category in categories.DefaultIfEmpty()
            orderby product.Title, product.Id, variant.SortOrder, variant.Sku
            select new { product.Title, variant.OptionSignature, variant.Sku, variant.Barcode, variant.ModelCode, Brand = brand == null ? null : brand.Name, Category = category == null ? null : category.Name, variant.CostPrice })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var data = new List<IReadOnlyList<object?>>(rows.Count + 1)
        {
            new object?[] { "Ürün Adı", "Varyant", "Stok Kodu", "Barkod", "Model Kodu", "Marka", "Kategori", "Güncel Maliyet", "Yeni Maliyet" }
        };
        data.AddRange(rows.Select(row => (IReadOnlyList<object?>)new object?[]
        {
            row.Title, NullIfEmpty(row.OptionSignature), row.Sku, row.Barcode, row.ModelCode, row.Brand, row.Category, row.CostPrice, null
        }));
        var notes = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Maliyet şablonu nasıl doldurulur?" },
            new object?[] { "Yeni Maliyet sütununa her varyantın 1 adet maliyetini Türk lirası olarak yazın: ürün kartındaki \"Maliyet\" alanıyla aynı değer." },
            new object?[] { "Kâr raporu bu değeri, müşterinin ödediği KDV dahil satış tutarından düşer." },
            new object?[] { "Tutar 123,45 ya da 1.234,56 biçiminde yazılabilir; ₺ ve TL işaretleri yok sayılır. 0 geçerli bir maliyettir." },
            new object?[] { "Yalnız noktalı 1.234 belirsizdir (bin mi, bir virgül mü?) ve reddedilir: 1234 ya da 1,234 yazın." },
            new object?[] { "Yeni Maliyet boş bırakılan satır değiştirilmez. Kayıtlı maliyeti silmek için - ya da sil yazın." },
            new object?[] { "Satır önce Stok Kodu, bulunamazsa Barkod ile eşleştirilir. Bu iki sütunu değiştirmeyin; diğer sütunlar yalnız bilgi içindir." },
            new object?[] { "Negatif ya da 10.000.000 ₺ üzerindeki tutarlar, formüller ve birden çok varyanta uyan kodlar hata olarak gösterilir." },
            new object?[] { "Dosyayı yükledikten sonra değişiklikler önce önizlenir; siz onaylamadan hiçbir maliyet kaydedilmez. Pazaryerlerine hiçbir şey gönderilmez." },
            new object?[] { "Dosyayı .xlsx olarak kaydedin (CSV de kabul edilir) ve en fazla 20.000 satır, 10 MiB yükleyin." }
        };
        var workbook = SimpleXlsxWriter.Write(
        [
            new SimpleXlsxSheet("Maliyetler", data, [48, 26, 22, 18, 18, 18, 22, 15, 15]),
            new SimpleXlsxSheet("Açıklama", notes, [120])
        ]);
        return ServiceResult<byte[]>.Ok(workbook);
    }

    public async Task<ServiceResult<ProductCostImportPreview>> PreviewAsync(Guid tenantId, string fileName, Stream content, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length is 0 or > MaximumFileBytes)
            return ServiceResult<ProductCostImportPreview>.Fail("PRODUCT_COST_FILE_SIZE", "Dosya boş olamaz ve en fazla 10 MiB olabilir.", 413);
        var safeName = Path.GetFileName(fileName);
        if (safeName.Length > 260) safeName = safeName[..260];
        IReadOnlyList<ProductCostFileRow> rows;
        try
        {
            buffer.Position = 0;
            rows = string.Equals(Path.GetExtension(safeName), ".csv", StringComparison.OrdinalIgnoreCase)
                ? ProductCostFileParser.ParseCsv(buffer)
                : ProductCostFileParser.ParseXlsx(buffer, MaximumExpandedBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
        {
            return ServiceResult<ProductCostImportPreview>.Fail("PRODUCT_COST_FILE_INVALID", exception is InvalidDataException ? exception.Message : "Dosya okunamadı; Ravencia'dan indirilen maliyet şablonunu yükleyin.", 422);
        }
        if (rows.Count == 0)
            return ServiceResult<ProductCostImportPreview>.Fail("PRODUCT_COST_FILE_EMPTY", "Dosyada maliyet satırı yok.", 422);

        // Every variant of the tenant, archived ones included: a row is matched against the catalog as it is, and the
        // keys are compared in memory with the shared key so legacy stored forms ("ABC_01") still match.
        var candidates = await (
            from variant in db.ProductVariants.AsNoTracking()
            where variant.TenantId == tenantId
            join product in db.Products.AsNoTracking() on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
            select new ProductCostCandidate(variant.Id, variant.ProductId, variant.Version, variant.Sku, variant.Barcode, product.Title, variant.OptionSignature, variant.CostPrice))
            .ToListAsync(cancellationToken);
        var preview = ProductCostImportPlanner.Plan(safeName, rows, candidates);
        return ServiceResult<ProductCostImportPreview>.Ok(preview);
    }

    public async Task<ServiceResult<ProductCostApplyResult>> ApplyAsync(Guid tenantId, Guid userId, ApplyProductCostsCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var changes = command.Changes ?? [];
        if (changes.Count == 0) return ServiceResult<ProductCostApplyResult>.Fail("PRODUCT_COST_CHANGES_REQUIRED", "Kaydedilecek maliyet yok.", 400);
        if (changes.Count > MaximumChanges) return ServiceResult<ProductCostApplyResult>.Fail("PRODUCT_COST_CHANGES_TOO_MANY", $"Tek seferde en fazla {MaximumChanges:N0} maliyet kaydedilebilir.", 400);
        if (changes.Select(change => change.VariantId).Distinct().Count() != changes.Count)
            return ServiceResult<ProductCostApplyResult>.Fail("PRODUCT_COST_CHANGES_DUPLICATE", "Aynı varyant birden çok kez gönderildi.", 400);
        if (changes.Any(change => change.Cost is < 0 or > ProductCostFileParser.MaximumCost || change.Cost is decimal cost && decimal.Round(cost, 2) != cost))
            return ServiceResult<ProductCostApplyResult>.Fail("PRODUCT_COST_VALUE_INVALID", "Maliyet 0 ile 10.000.000 ₺ arasında ve en fazla iki ondalıklı olmalıdır.", 400);

        var ids = changes.Select(change => change.VariantId).ToArray();
        var variants = new List<ProductVariant>(ids.Length);
        foreach (var chunk in ids.Chunk(Chunk))
            variants.AddRange(await db.ProductVariants.Where(variant => variant.TenantId == tenantId && chunk.Contains(variant.Id)).ToListAsync(cancellationToken));
        if (variants.Count != ids.Length)
            return Stale("Bazı varyantlar artık katalogda yok; dosyayı yeniden yükleyin.");

        var byId = variants.ToDictionary(variant => variant.Id);
        var now = timeProvider.GetUtcNow();
        var updated = 0;
        var cleared = 0;
        var products = new HashSet<Guid>();
        // A cost that already holds the requested value is done (a repeated save, or the same edit made elsewhere);
        // any other change since the preview was read means the preview is out of date and nothing is written. Every
        // variant is checked before any is changed, so a refusal leaves nothing half applied.
        if (changes.Any(change => byId[change.VariantId] is var variant && variant.CostPrice != change.Cost && variant.Version != change.Version))
            return Stale("Bazı varyantların maliyeti önizlemeden sonra değişti; dosyayı yeniden yükleyin.");
        foreach (var change in changes)
        {
            var variant = byId[change.VariantId];
            if (variant.CostPrice == change.Cost) continue;
            variant.CostPrice = change.Cost;
            variant.UpdatedAt = now;
            variant.Version++;
            products.Add(variant.ProductId);
            if (change.Cost is null) cleared++; else updated++;
        }
        if (updated + cleared == 0) return ServiceResult<ProductCostApplyResult>.Ok(new ProductCostApplyResult(0, 0, 0));

        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = "PRODUCT_COSTS_IMPORTED",
            TargetType = "ProductVariant",
            TargetId = null,
            Reason = $"Excel ile maliyet: {updated} varyant güncellendi, {cleared} varyantın maliyeti silindi ({products.Count} ürün).",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Stale("Bazı varyantlar kayıt sırasında değişti; hiçbir maliyet kaydedilmedi. Dosyayı yeniden yükleyin.");
        }
        return ServiceResult<ProductCostApplyResult>.Ok(new ProductCostApplyResult(updated, cleared, products.Count));
    }

    // The variants the template lists and the summary counts: those of products that are not archived, themselves not
    // archived.
    private IQueryable<ProductVariant> ListedVariants(Guid tenantId) =>
        from variant in db.ProductVariants.AsNoTracking()
        where variant.TenantId == tenantId && variant.Status != ProductStatus.Archived
        join product in db.Products.AsNoTracking() on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
        where product.Status != ProductStatus.Archived
        select variant;

    private static ServiceResult<ProductCostApplyResult> Stale(string message) => ServiceResult<ProductCostApplyResult>.Fail("PRODUCT_COST_STALE", message, 409);
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
