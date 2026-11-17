using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The products page's export and import: every listed variant to a workbook, and the fields the seller picks back from
// it (ProductTransferContracts lists the fields). Cost and desi change the variant only. Stock is set the way the
// product editor sets it (ledger entry, projection to the stores with external writes on). A store price changes the
// existing offer and its price history, as the editor's price matrix does. Title, brand, barcode, model code, panel price
// and VAT are panel writes that queue nothing for a marketplace; a brand is found or opened by name (PanelBrandByName,
// the rule every import shares) and written to every colour card of the model, as Toplu Değer Ata writes it. The product's version is left alone for cost, desi, stock
// and store prices, since a version bump would read as a local content edit and keep later marketplace imports from
// refreshing the product's title and description; a title, code, panel price or VAT change bumps it as the editor does.
public sealed class ProductTransferService(AppDbContext db, TimeProvider timeProvider) : IProductTransferService
{
    public const long MaximumFileBytes = 10 * 1024 * 1024;
    public const int MaximumChanges = ProductTransferFileParser.MaximumRows;
    private const long MaximumExpandedBytes = 64 * 1024 * 1024;
    private const int Chunk = 2_000;
    private const string ImportReason = "Ürün dosyası içe aktarma";
    public const string ModelFieldMixedCode = "PRODUCT_TRANSFER_MODEL_FIELD_MIXED";
    public const string LockConflictCode = "PRODUCT_TRANSFER_BUSY";
    // The history note of a colour or size a value reached through its model (another colour's row changed it).
    public const string ModelAppliedNote = "Modelden uygulandı: aynı modelin başka bir rengi dosyada değişti.";

    public async Task<ServiceResult<ProductTransferSummaryView>> SummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var variants = ListedVariants(tenantId);
        var total = await variants.CountAsync(cancellationToken);
        var missingCost = await variants.CountAsync(variant => variant.CostPrice == null || variant.CostPrice <= 0, cancellationToken);
        var missingDesi = await variants.CountAsync(variant => variant.Desi == null, cancellationToken);
        return ServiceResult<ProductTransferSummaryView>.Ok(new ProductTransferSummaryView(total, missingCost, missingDesi));
    }

    public async Task<ServiceResult<byte[]>> ExportAsync(Guid tenantId, ProductTransferExportFilter filter, Guid? brandId, Guid? categoryId, CancellationToken cancellationToken)
    {
        var variants = ListedVariants(tenantId);
        if (filter == ProductTransferExportFilter.MissingCost) variants = variants.Where(variant => variant.CostPrice == null || variant.CostPrice <= 0);
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
                PanelSalePrice = variant.DefaultSalePrice ?? product.DefaultSalePrice,
                PanelListPrice = variant.DefaultListPrice ?? product.DefaultListPrice,
                product.VatRate,
                variant.CostPrice,
                variant.Desi,
                ProductId = product.Id,
                product.Description
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // The panel price (the variant's default prices, else its product's) and the card's VAT: the values an upload
        // writes back, so an unedited file proposes nothing. The main location's stock and each store's own prices.
        var offers = await db.ChannelOffers.AsNoTracking()
            .Where(offer => offer.TenantId == tenantId)
            .OrderByDescending(offer => offer.Status == "ACTIVE").ThenBy(offer => offer.Id)
            .Select(offer => new { offer.VariantId, offer.ConnectionId, offer.SalePrice, offer.ListPrice })
            .ToListAsync(cancellationToken);
        var offerByVariantStore = offers.GroupBy(offer => (offer.VariantId, offer.ConnectionId)).ToDictionary(group => group.Key, group => group.First());
        var stockByVariant = (await db.InventoryItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LocationCode == "MAIN")
            .Select(item => new { item.VariantId, item.OnHand })
            .ToListAsync(cancellationToken))
            .GroupBy(item => item.VariantId).ToDictionary(group => group.Key, group => group.First().OnHand);
        var stores = await StoresAsync(tenantId, cancellationToken);

        var header = new List<object?> { "Ürün Adı", "Varyant", "Stok Kodu", "Barkod", "Model Kodu", "Marka", "Kategori", "Durum", ProductTransferFileParser.PanelSaleHeader, ProductTransferFileParser.PanelListHeader, "KDV Oranı", "Stok", "Maliyet (₺)", "Desi", "Açıklama" };
        var widths = new List<double> { 48, 26, 22, 18, 18, 18, 22, 13, 18, 18, 11, 10, 14, 10, 60 };
        // KATALOG-14 phase D5b (owner, 17 Nov 2026): the model's description on every row of the model. One longer than an
        // Excel cell holds is left empty (an empty cell changes nothing on the way back) and its model is named in the notes.
        var tooLong = rows.Where(row => (row.Description ?? "").Length > ProductTransferFileParser.MaximumDescriptionCell)
            .Select(row => string.IsNullOrWhiteSpace(row.ModelCode) ? row.Title : row.ModelCode.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var store in stores)
        {
            var (sale, list) = ProductTransferFileParser.StorePriceHeaders(store.Name);
            header.Add(sale);
            header.Add(list);
            widths.AddRange([Math.Max(18, sale.Length + 2), Math.Max(18, list.Length + 2)]);
        }
        var data = new List<IReadOnlyList<object?>>(rows.Count + 1) { header };
        data.AddRange(rows.Select(row =>
        {
            var cells = new List<object?>
            {
                row.Title,
                NullIfEmpty(row.OptionSignature),
                row.Sku,
                row.Barcode,
                row.ModelCode,
                row.Brand,
                row.Category,
                row.ProductStatus == ProductStatus.Draft ? "Taslak" : "Satışa Açık",
                Rounded(row.PanelSalePrice),
                Rounded(row.PanelListPrice),
                row.VatRate is decimal vat ? "%" + vat.ToString("0.##", CultureInfo.GetCultureInfo("tr-TR")) : null,
                stockByVariant.TryGetValue(row.Id, out var onHand) ? WholeStock(onHand) : null,
                Rounded(row.CostPrice),
                Rounded(row.Desi),
                string.IsNullOrEmpty(row.Description) || row.Description.Length > ProductTransferFileParser.MaximumDescriptionCell ? null : row.Description
            };
            foreach (var store in stores)
            {
                var storeOffer = offerByVariantStore.GetValueOrDefault((row.Id, store.ConnectionId));
                cells.Add(Rounded(storeOffer?.SalePrice));
                cells.Add(Rounded(storeOffer?.ListPrice));
            }
            return (IReadOnlyList<object?>)cells;
        }));
        var notes = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Ürün dosyası nasıl düzenlenir?" },
            new object?[] { "Her satır bir varyanttır. Yüklerken hangi alanların panele yazılacağını siz seçersiniz: Ürün Adı, Açıklama, Marka, Barkod, Model Kodu, Panel Fiyatı, KDV Oranı, Stok, Desi, Maliyet ve mağaza fiyatları. Seçmediğiniz sütunlar dosyada olsa da okunmaz." },
            new object?[] { "Model, marka ile model kodudur: aynı model kodu başka bir markada o markanın ayrı modelidir. Marka değişikliği modeli bütün renkleriyle yeni markaya taşır; o markada aynı model kodlu bir model zaten varsa birleştirilmez, değişiklik reddedilir. Panelde aynı adlı marka varsa o kullanılır (büyük/küçük harf, Türkçe karakter ve boşluk farkı gözetilmez), yoksa yeni marka açılır; kapatılmış bir marka yeniden açılmaz, o satır hatalı olur. Marka silinemez." },
            new object?[] { "Varyant, Kategori ve Durum sütunları yalnız bilgi içindir; değiştirmeniz hiçbir şeyi değiştirmez. Stok Kodu satırın eşleştirme anahtarıdır ve değiştirilemez." },
            new object?[] { "Açıklama da modele aittir. HTML yazılabilir; editördeki gibi temizlenir, en fazla 60.000 karakter olur ve boş olamaz. Boş hücre açıklamayı değiştirmez, silmez." },
            new object?[] { "Ürün Adı, Açıklama, KDV Oranı, Desi ve Marka modele aittir (aynı marka ve model kodlu bütün renkler ve bedenler): bir satırdaki değişiklik modelin bütün renklerine ve bedenlerine uygulanır (ad, her rengin kendi renk adıyla). Yalnız bir satırı değiştirmeniz yeterlidir; eski değeri taşıyan diğer satırlar değişmemiş sayılır, aynı modelin satırlarına iki farklı yeni değer yazılırsa o modelin satırları reddedilir. Barkod tek bir varyantta olabilir ve silinemez; barkodu değiştirirken satır yalnız Stok Kodu ile eşleştirilir. Model kodunu silmek için - yazın; satışa açık ya da kapalı üründe model kodu ve desi zorunludur, yalnız taslakta silinebilir." },
            new object?[] { "Panel Satış Fiyatı ve Panel Liste Fiyatı ürünün panel ana fiyatıdır; liste fiyatı satış fiyatından küçük olamaz. Panel fiyatı ve KDV panele kaydedilir, pazaryerine fiyat göndermez." },
            new object?[] { "Maliyet: 1 adetin Türk lirası maliyeti, ürün kartındaki \"Maliyet\" alanıyla aynı değer. Kâr raporu bu değeri, müşterinin ödediği KDV dahil satış tutarından düşer. 0 maliyet girilmemiş sayılır: kayıtlı maliyeti siler, kâr hesaplanmaz ve varyant eksik maliyet listesinde kalır." },
            new object?[] { $"Desi: kargo hacim ağırlığı. Raporlar kargo ücretini, pazaryeri kargo tutarı gelmediğinde bu değerle tahmin eder. 0'dan büyük ve en fazla {ProductDesi.MaximumText} olmalıdır." },
            new object?[] { "Stok: ana depodaki eldeki stok, tam sayı. Ürün sayfasındaki stok değişikliği gibi kaydedilir: stok gönderimi açık mağazalara yeni stok gönderilir." },
            new object?[] { "Mağaza fiyatları: her mağaza için satış ve liste fiyatı. Yalnız o mağazada fiyat kaydı olan varyantlar güncellenir; liste fiyatı satış fiyatından küçük olamaz. Fiyat panele kaydedilir, kendisi pazaryerine fiyat göndermez." },
            new object?[] { "Sayılar 123,45 ya da 1.234,56 biçiminde yazılabilir; ₺ ve TL işaretleri yok sayılır. Yalnız noktalı 1.234 belirsizdir ve reddedilir: 1234 ya da 1,234 yazın." },
            new object?[] { "Boş hücre değiştirilmez. Kayıtlı bir maliyeti, desiyi ya da model kodunu silmek için hücreye - ya da sil yazın (desi ve model kodu yalnız taslak üründe); stok, fiyat, KDV, ürün adı, marka ve barkod silinemez." },
            new object?[] { "Satır önce Stok Kodu, bulunamazsa Barkod ile eşleştirilir. Stok Kodu sütununu ve sütun başlıklarını değiştirmeyin." },
            new object?[] { "Dosyayı yükledikten sonra değişiklikler önce önizlenir; siz onaylamadan hiçbir şey kaydedilmez." },
            new object?[] { "Dosyayı .xlsx olarak kaydedin (CSV de kabul edilir) ve en fazla 20.000 satır, 10 MiB yükleyin. Formül içeren hücreler okunmaz." }
        };
        if (tooLong.Count > 0)
            notes.Insert(3, new object?[] { $"Bu Modelin Açıklaması Excel İçin Çok Uzun, Editörden Düzenleyin: {string.Join(", ", tooLong)}. Bu modellerin Açıklama hücresi boş bırakıldı; boş hücre açıklamayı değiştirmez." });
        var workbook = SimpleXlsxWriter.Write(
        [
            new SimpleXlsxSheet("Ürünler", data, widths),
            new SimpleXlsxSheet("Açıklama", notes, [150])
        ]);
        return ServiceResult<byte[]>.Ok(workbook);
    }

    public async Task<ServiceResult<ProductTransferImportPreview>> PreviewAsync(Guid tenantId, string fileName, Stream content, IReadOnlyCollection<string> fields, CancellationToken cancellationToken)
    {
        if (fields.Count == 0 || fields.Any(field => !ProductTransferFields.All.Contains(field)))
            return ServiceResult<ProductTransferImportPreview>.Fail("PRODUCT_TRANSFER_FIELDS_INVALID", "Aktarılacak en az bir alan seçin: ürün adı, açıklama, marka, barkod, model kodu, panel fiyatı, KDV oranı, stok, desi, maliyet ya da mağaza fiyatları.", 400);
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length is 0 or > MaximumFileBytes)
            return ServiceResult<ProductTransferImportPreview>.Fail("PRODUCT_TRANSFER_FILE_SIZE", "Dosya boş olamaz ve en fazla 10 MiB olabilir.", 413);
        var safeName = Path.GetFileName(fileName);
        if (safeName.Length > 260) safeName = safeName[..260];
        var stores = await StoresAsync(tenantId, cancellationToken);
        ProductTransferFile file;
        try
        {
            buffer.Position = 0;
            file = string.Equals(Path.GetExtension(safeName), ".csv", StringComparison.OrdinalIgnoreCase)
                ? ProductTransferFileParser.ParseCsv(buffer, stores)
                : ProductTransferFileParser.ParseXlsx(buffer, MaximumExpandedBytes, stores);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
        {
            return ServiceResult<ProductTransferImportPreview>.Fail("PRODUCT_TRANSFER_FILE_INVALID", exception is InvalidDataException ? exception.Message : "Dosya okunamadı; Ravencia'dan indirilen ürün dosyasını yükleyin.", 422);
        }
        if (file.Rows.Count == 0)
            return ServiceResult<ProductTransferImportPreview>.Fail("PRODUCT_TRANSFER_FILE_EMPTY", "Dosyada ürün satırı yok.", 422);

        // Every variant of the tenant, archived ones included: a row is matched against the catalog as it is, and the
        // keys are compared in memory with the shared key so legacy stored forms ("ABC_01") still match.
        var variants = await (
            from variant in db.ProductVariants.AsNoTracking()
            where variant.TenantId == tenantId
            join product in EffectiveCatalog.Products(db) on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
            select new
            {
                variant.Id,
                variant.ProductId,
                variant.Version,
                variant.Sku,
                variant.Barcode,
                product.Title,
                variant.OptionSignature,
                variant.CostPrice,
                variant.Desi,
                ProductVersion = product.Version,
                variant.ModelCode,
                PanelSalePrice = variant.DefaultSalePrice ?? product.DefaultSalePrice,
                PanelListPrice = variant.DefaultListPrice ?? product.DefaultListPrice,
                product.VatRate,
                product.BrandId,
                ProductStatus = product.Status
            })
            .ToListAsync(cancellationToken);
        var brandNames = await db.Brands.AsNoTracking().Where(brand => brand.TenantId == tenantId).ToDictionaryAsync(brand => brand.Id, brand => brand.Name, cancellationToken);
        // A brand the file names that the user switched off: its rows are refused (PanelBrandByName never revives one).
        var switchedOffBrandKeys = new List<string>();
        if (fields.Contains(ProductTransferFields.Brand))
        {
            var brandLookup = new PanelBrandByName(db, tenantId);
            var names = file.Rows.Where(row => row.Brand.Kind == ProductTransferValueKind.Set).Select(row => row.Brand.Text).DistinctBy(PanelBrandByName.Key).ToList();
            await brandLookup.PreloadAsync(names, cancellationToken);
            foreach (var name in names)
                if ((await brandLookup.FindAsync(name, cancellationToken)).SwitchedOff is not null) switchedOffBrandKeys.Add(PanelBrandByName.Key(name));
        }
        var stockByVariant = fields.Contains(ProductTransferFields.Stock)
            ? (await db.InventoryItems.AsNoTracking().Where(item => item.TenantId == tenantId && item.LocationCode == "MAIN").Select(item => new { item.VariantId, item.OnHand }).ToListAsync(cancellationToken))
                .GroupBy(item => item.VariantId).ToDictionary(group => group.Key, group => group.First().OnHand)
            : [];
        var storeIds = stores.Select(store => store.ConnectionId).ToArray();
        var offersByVariant = fields.Contains(ProductTransferFields.Prices)
            ? (await db.ChannelOffers.AsNoTracking().Where(offer => offer.TenantId == tenantId && storeIds.Contains(offer.ConnectionId))
                .Select(offer => new { offer.VariantId, Offer = new ProductTransferOffer(offer.ConnectionId, offer.Id, offer.Version, offer.SalePrice, offer.ListPrice) }).ToListAsync(cancellationToken))
                .GroupBy(item => item.VariantId).ToDictionary(group => group.Key, group => (IReadOnlyList<ProductTransferOffer>)group.Select(item => item.Offer).ToList())
            : [];
        // The model's description each card reads (only when the description is picked: it can be long).
        var descriptions = fields.Contains(ProductTransferFields.Description)
            ? await EffectiveCatalog.Products(db).Where(product => product.TenantId == tenantId).Select(product => new { product.Id, product.Description }).ToDictionaryAsync(product => product.Id, product => (string?)product.Description, cancellationToken)
            : [];
        var candidates = variants.Select(variant => new ProductTransferCandidate(
            variant.Id, variant.ProductId, variant.Version, variant.Sku, variant.Barcode, variant.Title, variant.OptionSignature, variant.CostPrice, variant.Desi,
            stockByVariant.TryGetValue(variant.Id, out var onHand) ? onHand : null,
            offersByVariant.GetValueOrDefault(variant.Id),
            variant.ProductVersion,
            variant.ModelCode,
            variant.PanelSalePrice,
            variant.PanelListPrice,
            variant.VatRate,
            variant.BrandId is Guid brandId ? brandNames.GetValueOrDefault(brandId) : null,
            ProductDraftPolicy.IsDraft(variant.ProductStatus),
            descriptions.GetValueOrDefault(variant.ProductId))).ToList();
        var preview = ProductTransferImportPlanner.Plan(safeName, file.Rows, candidates, fields, stores, file.AvailableFields, switchedOffBrandKeys);
        return ServiceResult<ProductTransferImportPreview>.Ok(preview);
    }

    public async Task<ServiceResult<ProductTransferApplyResult>> ApplyAsync(Guid tenantId, Guid userId, ApplyProductTransferCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var changes = command.Changes ?? [];
        var productChanges = command.ProductChanges ?? [];
        if (changes.Count == 0 && productChanges.Count == 0) return Invalid("PRODUCT_TRANSFER_CHANGES_REQUIRED", "Kaydedilecek değişiklik yok.");
        if (changes.Count > MaximumChanges || productChanges.Count > MaximumChanges) return Invalid("PRODUCT_TRANSFER_CHANGES_TOO_MANY", $"Tek seferde en fazla {MaximumChanges:N0} varyant kaydedilebilir.");
        if (changes.Select(change => change.VariantId).Distinct().Count() != changes.Count || productChanges.Select(change => change.ProductId).Distinct().Count() != productChanges.Count)
            return Invalid("PRODUCT_TRANSFER_CHANGES_DUPLICATE", "Aynı varyant ya da ürün birden çok kez gönderildi.");
        if (changes.Any(change => !change.WritesVariant() && change.Stock is null && (change.Prices ?? []).Count == 0) || productChanges.Any(change => change.Title is null && change.VatRate is null && change.Brand is null && change.Description is null))
            return Invalid("PRODUCT_TRANSFER_CHANGES_EMPTY", "Her değişiklik en az bir alanı değiştirmelidir.");
        if (changes.Any(change => change.SetCost && (change.Cost is < 0 or > ProductTransferFileParser.MaximumCost || change.Cost is decimal cost && decimal.Round(cost, 2) != cost)))
            return Invalid("PRODUCT_TRANSFER_COST_INVALID", "Maliyet 0 ile 10.000.000 ₺ arasında ve en fazla iki ondalıklı olmalıdır.");
        if (changes.Any(change => change.SetDesi && change.Desi is decimal desi && !ProductDesi.IsValid(desi)))
            return Invalid("PRODUCT_TRANSFER_DESI_INVALID", $"Desi 0'dan büyük, en fazla {ProductDesi.MaximumText} ve en fazla iki ondalıklı olmalıdır.");
        if (changes.Any(change => change.Stock is { } stock && (stock.OnHand is < 0 or > ProductTransferFileParser.MaximumStock || stock.OnHand != decimal.Truncate(stock.OnHand))))
            return Invalid("PRODUCT_TRANSFER_STOCK_INVALID", "Stok 0 ile 1.000.000.000 arasında bir tam sayı olmalıdır.");
        var priceChanges = changes.SelectMany(change => (change.Prices ?? []).Select(price => (change.VariantId, Price: price))).ToList();
        if (priceChanges.Any(item => !ValidPrices(item.Price.SalePrice, item.Price.ListPrice)))
            return Invalid("PRODUCT_TRANSFER_PRICE_INVALID", "Mağaza fiyatı 0'dan büyük, en fazla 10.000.000 ₺ ve iki ondalıklı olmalı; liste fiyatı satış fiyatından küçük olamaz.");
        if (priceChanges.Select(item => item.Price.OfferId).Distinct().Count() != priceChanges.Count)
            return Invalid("PRODUCT_TRANSFER_CHANGES_DUPLICATE", "Aynı mağaza fiyatı birden çok kez gönderildi.");
        if (changes.Any(change => change.PanelPrice is { } panel && !ValidPrices(panel.SalePrice, panel.ListPrice)))
            return Invalid("PRODUCT_TRANSFER_PANEL_PRICE_INVALID", "Panel fiyatı 0'dan büyük, en fazla 10.000.000 ₺ ve iki ondalıklı olmalı; liste fiyatı satış fiyatından küçük olamaz.");
        if (changes.Any(change => change.Barcode is { } code && !ValidCode(code)) || changes.Any(change => change.SetModelCode && change.ModelCode is { } model && !ValidCode(model)))
            return Invalid("PRODUCT_TRANSFER_CODE_INVALID", "Barkod ve model kodu boş olamaz, en fazla 160 karakter olmalı, harf ya da rakam içermeli ve formül gibi başlayamaz.");
        var barcodeKeys = changes.Where(change => change.Barcode is not null).Select(change => (change.VariantId, Key: CatalogKeyNormalizer.Key(change.Barcode))).ToList();
        if (barcodeKeys.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != barcodeKeys.Count)
            return Invalid("PRODUCT_TRANSFER_BARCODE_DUPLICATE", "Aynı barkod birden çok varyanta yazılamaz.");
        if (productChanges.Any(change => change.Title is { } title && (title.Trim() != title || title.Length is 0 or > ProductTransferFileParser.MaximumTitleLength || ImportFileReader.LooksLikeFormula(title))))
            return Invalid("PRODUCT_TRANSFER_TITLE_INVALID", "Ürün adı boş olamaz, en fazla 320 karakter olmalı ve formül gibi başlayamaz.");
        if (productChanges.Any(change => change.VatRate is < 0 or > 100 || change.VatRate is decimal vat && decimal.Round(vat, 2) != vat))
            return Invalid("PRODUCT_TRANSFER_VAT_INVALID", "KDV oranı 0 ile 100 arasında ve en fazla iki ondalıklı olmalıdır.");
        // KATALOG-14 phase D5b: the description is cleaned and checked again (the client's plan is not trusted), as the preview
        // cleaned it; the cleaned text is what is saved.
        var descriptionOf = new Dictionary<Guid, string>();
        foreach (var change in productChanges.Where(change => change.Description is not null))
        {
            var (cleaned, error) = ImportFileReader.LooksLikeFormula(change.Description!.Trim()) ? (null, "Açıklama: formül benzeri içerik kabul edilmez.") : ProductTransferImportPlanner.CleanDescription(change.Description!);
            if (cleaned is null) return Invalid("PRODUCT_TRANSFER_DESCRIPTION_INVALID", error ?? "Açıklama geçersiz.");
            descriptionOf[change.ProductId] = cleaned;
        }
        if (productChanges.Any(change => change.Brand is { } brandName && (brandName.Trim() != brandName || brandName.Length > PanelBrandByName.MaximumNameLength || PanelBrandByName.Key(brandName).Length == 0 || ImportFileReader.LooksLikeFormula(brandName))))
            return Invalid("PRODUCT_TRANSFER_BRAND_INVALID", "Marka boş olamaz, en fazla 160 karakter olmalı, harf ya da rakam içermeli ve formül gibi başlayamaz.");

        var ids = changes.Select(change => change.VariantId).ToArray();
        var variants = new List<ProductVariant>(ids.Length);
        foreach (var chunk in ids.Chunk(Chunk))
            variants.AddRange(await db.ProductVariants.Where(variant => variant.TenantId == tenantId && chunk.Contains(variant.Id)).ToListAsync(cancellationToken));
        if (variants.Count != ids.Length)
            return Stale("Bazı varyantlar artık katalogda yok; dosyayı yeniden yükleyin.");
        var stockIds = changes.Where(change => change.Stock is not null).Select(change => change.VariantId).ToArray();
        var items = new List<InventoryItem>(stockIds.Length);
        foreach (var chunk in stockIds.Chunk(Chunk))
            items.AddRange(await db.InventoryItems.Where(item => item.TenantId == tenantId && item.LocationCode == "MAIN" && chunk.Contains(item.VariantId)).ToListAsync(cancellationToken));
        var offerIds = priceChanges.Select(item => item.Price.OfferId).ToArray();
        var offers = new List<ChannelOffer>(offerIds.Length);
        foreach (var chunk in offerIds.Chunk(Chunk))
            offers.AddRange(await db.ChannelOffers.Where(offer => offer.TenantId == tenantId && chunk.Contains(offer.Id)).ToListAsync(cancellationToken));

        var byId = variants.ToDictionary(variant => variant.Id);
        // The products a change touches: those with a title or VAT change, and those of a panel price change (a product
        // with a single variant keeps its own default prices equal to the variant's, as the quick edit does).
        var panelProductIds = changes.Where(change => change.PanelPrice is not null).Select(change => byId[change.VariantId].ProductId).ToHashSet();
        var codeProductIds = changes.Where(change => change.Barcode is not null || change.SetModelCode).Select(change => byId[change.VariantId].ProductId);
        var productIds = productChanges.Select(change => change.ProductId).Concat(panelProductIds).Concat(codeProductIds).Distinct().ToArray();
        var loadedProducts = new List<Product>(productIds.Length);
        foreach (var chunk in productIds.Chunk(Chunk))
            loadedProducts.AddRange(await db.Products.Where(product => product.TenantId == tenantId && chunk.Contains(product.Id)).ToListAsync(cancellationToken));
        var productById = loadedProducts.ToDictionary(product => product.Id);
        if (productChanges.Any(change => !productById.ContainsKey(change.ProductId)) || panelProductIds.Any(id => !productById.ContainsKey(id)))
            return Stale("Bazı ürünler artık katalogda yok; dosyayı yeniden yükleyin.");
        // A card on sale or closed keeps its desi and model code (ProductRequiredFields); only a draft may lose them.
        var clearingIds = changes.Where(change => change.SetDesi && change.Desi is null || change.SetModelCode && change.ModelCode is null).Select(change => byId[change.VariantId].ProductId).Distinct().ToArray();
        var nonDraftIds = new HashSet<Guid>();
        foreach (var chunk in clearingIds.Chunk(Chunk))
            nonDraftIds.UnionWith(await EffectiveCatalog.Products(db).Where(product => product.TenantId == tenantId && chunk.Contains(product.Id) && product.Status != ProductStatus.Draft).Select(product => product.Id).ToListAsync(cancellationToken));
        if (changes.FirstOrDefault(change => nonDraftIds.Contains(byId[change.VariantId].ProductId) && (change.SetDesi && change.Desi is null || change.SetModelCode && change.ModelCode is null)) is { } emptying)
        {
            var emptied = ProductRequiredFields.Label(emptying.SetDesi && emptying.Desi is null ? ProductRequiredFields.Desi : ProductRequiredFields.ModelCode);
            return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_REQUIRED_FIELD", $"{byId[emptying.VariantId].Sku} stok kodlu varyantın ürünü taslak değil; {emptied} zorunlu alandır ve silinemez. Hiçbir değişiklik kaydedilmedi; dosyayı düzeltip yeniden yükleyin.", 422);
        }
        // The brands the file names, as they are now; a brand switched off since the preview is refused.
        var brandLookup = new PanelBrandByName(db, tenantId);
        await brandLookup.PreloadAsync(productChanges.Select(change => change.Brand), cancellationToken);
        var currentBrandIds = new Dictionary<Guid, Guid?>();
        foreach (var change in productChanges.Where(change => change.Brand is not null))
        {
            var match = await brandLookup.FindAsync(change.Brand, cancellationToken);
            if (match.SwitchedOff is not null)
                return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_BRAND_SWITCHED_OFF", $"«{change.Brand}» markası panelde kapalı; hiçbir değişiklik kaydedilmedi. Markayı açın ya da dosyayı düzeltip yeniden yükleyin.", 409);
            currentBrandIds[change.ProductId] = match.Id;
        }
        var variantCounts = new Dictionary<Guid, int>();
        foreach (var chunk in panelProductIds.Chunk(Chunk))
            foreach (var row in await db.ProductVariants.AsNoTracking().Where(variant => variant.TenantId == tenantId && chunk.Contains(variant.ProductId)).GroupBy(variant => variant.ProductId).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken))
                variantCounts[row.Key] = row.Count;

        var itemByVariant = items.GroupBy(item => item.VariantId).ToDictionary(group => group.Key, group => group.First());
        var offerById = offers.ToDictionary(offer => offer.Id);
        if (stockIds.Any(id => !itemByVariant.ContainsKey(id)) || priceChanges.Any(item => !offerById.TryGetValue(item.Price.OfferId, out var offer) || offer.VariantId != item.VariantId))
            return Stale("Bazı stok ya da fiyat kayıtları artık yok; dosyayı yeniden yükleyin.");
        // A value that already holds what is asked is done (a repeated save, or the same edit made elsewhere); any
        // other change since the preview was read means the preview is out of date and nothing is written. Everything
        // is checked before anything is changed, so a refusal leaves nothing half applied.
        if (changes.Any(change => byId[change.VariantId] is var variant && change.WritesVariant() && !HoldsCatalog(variant, change) && variant.Version != change.Version)
            || changes.Any(change => change.Stock is { } stock && itemByVariant[change.VariantId] is var item && item.OnHand != stock.OnHand && item.OnHand != stock.ExpectedOnHand)
            || priceChanges.Any(change => offerById[change.Price.OfferId] is var offer && !HoldsPrice(offer, change.Price) && offer.Version != change.Price.Version)
            || productChanges.Any(change => productById[change.ProductId] is var product && !HoldsProduct(product, change, currentBrandIds.GetValueOrDefault(change.ProductId)) && product.Version != change.Version))
            return Stale("Bazı varyantlar önizlemeden sonra değişti; dosyayı yeniden yükleyin.");
        // A barcode is unique in the tenant: one another variant holds now (even one this upload also changes) is refused.
        var wantedKeys = barcodeKeys.Where(item => byId[item.VariantId].Barcode is var current && CatalogKeyNormalizer.Key(current) != item.Key).Select(item => item.Key).ToArray();
        foreach (var chunk in wantedKeys.Chunk(Chunk))
        {
            var holders = await db.ProductVariants.AsNoTracking().Where(variant => variant.TenantId == tenantId && variant.BarcodeNormalized != null && chunk.Contains(variant.BarcodeNormalized)).Select(variant => new { variant.Id, variant.Sku, variant.BarcodeNormalized }).ToListAsync(cancellationToken);
            if (holders.FirstOrDefault(holder => barcodeKeys.Any(item => item.Key == holder.BarcodeNormalized && item.VariantId != holder.Id)) is { } taken)
                return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_BARCODE_TAKEN", $"Yazılmak istenen bir barkod başka bir varyantta ({taken.Sku}) kayıtlı; dosyayı düzeltip yeniden yükleyin.", 409);
        }

        // C-9: a model code the file changes to one another product holds (a product this upload does not change) would
        // join that product's family; the upload is refused before anything is written.
        var modelCodeChanges = changes.Where(change => change.SetModelCode).ToList();
        // A model is its brand and its code (owner, 17 Nov 2026): only a card of the row's own brand holding the code counts.
        var fileProductIds = changes.Select(change => byId[change.VariantId].ProductId).Distinct().ToArray();
        var codeProducts = modelCodeChanges.Select(change => byId[change.VariantId].ProductId).Distinct().ToArray();
        var brandOfRecoded = codeProducts.Length == 0 ? [] : await EffectiveCatalog.Products(db).Where(product => product.TenantId == tenantId && codeProducts.Contains(product.Id)).ToDictionaryAsync(product => product.Id, product => product.BrandId, cancellationToken);
        foreach (var brandGroup in modelCodeChanges.GroupBy(change => brandOfRecoded.GetValueOrDefault(byId[change.VariantId].ProductId)))
        {
            var changedModelCodes = ModelCodeUniquenessPolicy.ChangedCodes(brandGroup.Select(change => (byId[change.VariantId].ModelCode, change.ModelCode)));
            if (changedModelCodes.Count > 0
                && await ModelCodeOwnership.FindOtherAsync(db, tenantId, brandGroup.Key, changedModelCodes, fileProductIds, cancellationToken) is { } modelCodeHolder)
                return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_MODEL_CODE_TAKEN", $"Dosyadaki yeni bir model kodu '{modelCodeHolder.Title}' ürününde kullanılıyor; yazılırsa iki ürün tek ürünün renkleri gibi birleşir. Dosyayı düzeltip yeniden yükleyin.", 409);
        }
        // KATALOG-1: the model code is the model's: every row of a card the upload re-codes ends on one code (the preview
        // refuses it too; checked again here, as the client's plan is not trusted).
        var recodedChanges = modelCodeChanges.Where(change => ProductFamilyKeyPolicy.ModelCodeKey(byId[change.VariantId].ModelCode) != ProductFamilyKeyPolicy.ModelCodeKey(change.ModelCode)).ToList();
        if (recodedChanges.Count > 0)
        {
            var recodedProductIds = recodedChanges.Select(change => byId[change.VariantId].ProductId).Distinct().ToArray();
            var askedCodes = modelCodeChanges.GroupBy(change => change.VariantId).ToDictionary(group => group.Key, group => group.First().ModelCode);
            var cardRows = new List<(Guid ProductId, Guid Id, string? ModelCode)>();
            foreach (var chunk in recodedProductIds.Chunk(Chunk))
                cardRows.AddRange((await db.ProductVariants.AsNoTracking().Where(variant => variant.TenantId == tenantId && chunk.Contains(variant.ProductId))
                    .Select(variant => new { variant.ProductId, variant.Id, variant.ModelCode }).ToListAsync(cancellationToken)).Select(row => (row.ProductId, row.Id, row.ModelCode)));
            foreach (var card in cardRows.GroupBy(row => row.ProductId))
            {
                var finals = card.Select(row => askedCodes.TryGetValue(row.Id, out var asked) ? asked : row.ModelCode).ToList();
                if (finals.Select(ProductFamilyKeyPolicy.ModelCodeKey).OfType<string>().Distinct(StringComparer.Ordinal).Count() > 1)
                    return ServiceResult<ProductTransferApplyResult>.Fail(CatalogService.ModelCodeMixedCode, "Model kodu bir kartın tüm satırlarında birlikte değişir; dosya bir kartın yalnız bazı satırlarına yeni model kodu yazıyor. Hiçbir değişiklik kaydedilmedi; ürünün tüm satırlarına aynı model kodunu yazıp yeniden yükleyin.", 422);
            }
        }
        // KATALOG-3: the name and the VAT are the model's; the rows of one model may not ask for two values (the preview
        // refuses it; checked again here, as the client's plan is not trusted). The cards of a model are those the write
        // below gives the value to; a name is compared without its card's colour word, as the preview compares it.
        var modelFieldChanges = productChanges.Where(change => change.Title is not null || change.VatRate is not null || change.Description is not null).ToList();
        var fieldCards = new Dictionary<Guid, HashSet<Guid>>();
        var colours = new Dictionary<Guid, string?>();
        if (modelFieldChanges.Count > 0)
        {
            fieldCards = await ModelCardsAsync(tenantId, modelFieldChanges.Select(change => change.ProductId).ToArray(), cancellationToken);
            var colourCardIds = fieldCards.Values.SelectMany(cards => cards).Distinct().ToArray();
            foreach (var chunk in colourCardIds.Chunk(Chunk))
                foreach (var card in (await db.ProductVariants.AsNoTracking().Where(variant => variant.TenantId == tenantId && chunk.Contains(variant.ProductId))
                             .OrderBy(variant => variant.SortOrder).ThenBy(variant => variant.Id).Select(variant => new { variant.ProductId, variant.OptionSignature }).ToListAsync(cancellationToken))
                             .GroupBy(variant => variant.ProductId))
                    colours[card.Key] = card.Select(variant => ProductFamilyColorOrder.ColorOf(variant.OptionSignature)).FirstOrDefault(colour => !string.IsNullOrWhiteSpace(colour));
            foreach (var change in modelFieldChanges)
                foreach (var other in modelFieldChanges.Where(other => other.ProductId != change.ProductId && fieldCards[change.ProductId].Contains(other.ProductId)))
                {
                    var label = change.Title is { } title && other.Title is { } otherTitle
                        && ProductTransferImportPlanner.ModelTitleKey(title, colours.GetValueOrDefault(change.ProductId)) != ProductTransferImportPlanner.ModelTitleKey(otherTitle, colours.GetValueOrDefault(other.ProductId))
                            ? "ürün adı"
                            : change.VatRate is decimal vat && other.VatRate is decimal otherVat && vat != otherVat ? "KDV oranı"
                            : descriptionOf.TryGetValue(change.ProductId, out var text) && descriptionOf.TryGetValue(other.ProductId, out var otherText) && text != otherText ? "açıklama" : null;
                    var modelName = label is null ? null : await (from product in db.Products.AsNoTracking()
                                                                  join model in db.ProductModels.AsNoTracking() on product.FamilyId equals model.Id
                                                                  where product.TenantId == tenantId && product.Id == change.ProductId
                                                                  select model.ModelCode).FirstOrDefaultAsync(cancellationToken);
                    if (label is not null)
                        return ServiceResult<ProductTransferApplyResult>.Fail(ModelFieldMixedCode, $"Aynı modelin ({(string.IsNullOrWhiteSpace(modelName) ? productById[change.ProductId].Title : modelName)}) satırlarında farklı {label} var; {label} modelin tüm renklerinde aynıdır, tüm satırlara aynı değeri yazın. Hiçbir değişiklik kaydedilmedi; dosyayı düzeltip yeniden yükleyin.", 422);
                }
        }

        // KATALOG-14 phase D5b: the desi is the model's, one value on every size of every colour: the rows of one model may
        // not ask for two (the preview refuses it; checked again here).
        var desiChanges = changes.Where(change => change.SetDesi && change.Desi is not null).ToList();
        var modelOfDesi = new Dictionary<Guid, Guid?>();
        if (desiChanges.Count > 0)
        {
            var desiProductIds = desiChanges.Select(change => byId[change.VariantId].ProductId).Distinct().ToArray();
            var modelOf = new Dictionary<Guid, (Guid? ModelId, string? Code)>();
            foreach (var chunk in desiProductIds.Chunk(Chunk))
                foreach (var row in await (from product in db.Products.AsNoTracking()
                                           join model in db.ProductModels.AsNoTracking() on product.FamilyId equals model.Id into models
                                           from model in models.DefaultIfEmpty()
                                           where product.TenantId == tenantId && chunk.Contains(product.Id)
                                           select new { product.Id, product.FamilyId, Code = model == null ? null : model.ModelCode }).ToListAsync(cancellationToken))
                {
                    modelOf[row.Id] = (row.FamilyId, row.Code);
                    modelOfDesi[row.Id] = row.FamilyId;
                }
            foreach (var model in desiChanges.GroupBy(change => modelOf.GetValueOrDefault(byId[change.VariantId].ProductId).ModelId ?? byId[change.VariantId].ProductId))
                if (model.Select(change => change.Desi).Distinct().Count() > 1)
                {
                    var code = modelOf.GetValueOrDefault(byId[model.First().VariantId].ProductId).Code is { Length: > 0 } held ? held : byId[model.First().VariantId].Sku;
                    return ServiceResult<ProductTransferApplyResult>.Fail(ModelFieldMixedCode, $"Aynı modelin ({code}) satırlarında farklı desi var; desi modelin tüm renklerinde aynıdır, tüm satırlara aynı değeri yazın. Hiçbir değişiklik kaydedilmedi; dosyayı düzeltip yeniden yükleyin.", 422);
                }
        }

        var now = timeProvider.GetUtcNow();
        var applyId = Guid.CreateVersion7();
        int costsUpdated = 0, costsCleared = 0, desiUpdated = 0, desiCleared = 0, stocksUpdated = 0, pricesUpdated = 0;
        int descriptionsUpdated = 0, titlesUpdated = 0, barcodesUpdated = 0, modelCodesUpdated = 0, modelCodesCleared = 0, panelPricesUpdated = 0, vatUpdated = 0, brandsUpdated = 0, brandsCreated = 0;
        var written = new HashSet<Guid>();
        var products = new HashSet<Guid>();
        // Products whose own row changes (version bump) and those whose marketplace links get the local-edit mark.
        var touchedProducts = new HashSet<Guid>();
        var contentProducts = new HashSet<Guid>();
        // The colours (and sizes) a value of another colour's row reached through the model: their history rows say so.
        var fromModel = new HashSet<(Guid ProductId, Guid? VariantId, string Field)>();
        var projections = new List<StockProjectionChange>();
        var stockSetItemIds = new List<Guid>();
        var repricedOffers = new List<ChannelOffer>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reservedByItem = await ReservedAsync(tenantId, items.Select(item => item.Id).ToArray(), cancellationToken);
        foreach (var change in changes)
        {
            var variant = byId[change.VariantId];
            if (change.WritesVariant() && !HoldsCatalog(variant, change))
            {
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
                if (change.Barcode is { } barcode && variant.Barcode != barcode)
                {
                    variant.Barcode = barcode;
                    variant.BarcodeNormalized = CatalogKeyNormalizer.Key(barcode);
                    barcodesUpdated++;
                    contentProducts.Add(variant.ProductId);
                }
                if (change.SetModelCode && variant.ModelCode != change.ModelCode)
                {
                    variant.ModelCode = change.ModelCode;
                    if (change.ModelCode is null) modelCodesCleared++; else modelCodesUpdated++;
                    contentProducts.Add(variant.ProductId);
                }
                if (change.PanelPrice is { } panel && (variant.DefaultSalePrice != panel.SalePrice || variant.DefaultListPrice != panel.ListPrice))
                {
                    if (ProductChangeLog.DefaultPrice(tenantId, variant.ProductId, variant.Id, (variant.DefaultListPrice, variant.DefaultSalePrice), (panel.ListPrice, panel.SalePrice),
                            ProductChangeSources.FileImport, userId, correlationId, now) is { } priceChange)
                        db.ProductChangeEntries.Add(priceChange);
                    variant.DefaultSalePrice = panel.SalePrice;
                    variant.DefaultListPrice = panel.ListPrice;
                    if (variantCounts.GetValueOrDefault(variant.ProductId) == 1)
                    {
                        productById[variant.ProductId].DefaultSalePrice = panel.SalePrice;
                        productById[variant.ProductId].DefaultListPrice = panel.ListPrice;
                    }
                    touchedProducts.Add(variant.ProductId);
                    panelPricesUpdated++;
                }
                variant.UpdatedAt = now;
                variant.Version++;
                Written(variant);
            }
            if (change.Stock is { } stock && itemByVariant[change.VariantId] is var item && item.OnHand != stock.OnHand)
            {
                // As InventoryService.SetOnHandAsync (SIPARIS-STOK-8): a manual adjustment through the one stock writer,
                // the count rounded as the manual count rounds it (a whole number here), then the projection outbox.
                item.Reserved = reservedByItem.GetValueOrDefault(item.Id);
                var sourceEventId = $"product-transfer:{applyId:N}:{variant.Id:N}";
                projections.Add(StockMovementWriter.SetOnHand(db, item, decimal.Round(stock.OnHand, 4, MidpointRounding.ToEven),
                    new StockLedgerSource("MANUAL_ADJUSTMENT", "MANUAL", ImportReason, sourceEventId, sourceEventId, now, now, correlationId, userId), $"manual:{sourceEventId}"));
                stockSetItemIds.Add(item.Id);
                stocksUpdated++;
                Written(variant);
            }
            foreach (var price in change.Prices ?? [])
            {
                var offer = offerById[price.OfferId];
                if (HoldsPrice(offer, price)) continue;
                offer.SalePrice = price.SalePrice;
                offer.ListPrice = price.ListPrice;
                offer.PriceVersion++;
                offer.Version++;
                db.ChannelPriceHistory.Add(new ChannelPriceHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OfferId = offer.Id, PriceVersion = offer.PriceVersion, ListPrice = offer.ListPrice, SalePrice = offer.SalePrice, Currency = offer.Currency, Reason = ImportReason, ActorSource = $"USER:{userId:D}", EffectiveAt = now });
                repricedOffers.Add(offer);
                pricesUpdated++;
                Written(variant);
            }
        }
        foreach (var change in productChanges)
        {
            var product = productById[change.ProductId];
            if (change.Title is { } title && product.Title != title)
            {
                product.Title = title;
                titlesUpdated++;
                touchedProducts.Add(product.Id);
                contentProducts.Add(product.Id);
            }
            // The description goes to the model and every colour (ModelMirror, in this save's transaction).
            if (descriptionOf.TryGetValue(change.ProductId, out var description) && product.Description != description)
            {
                product.Description = description;
                descriptionsUpdated++;
                touchedProducts.Add(product.Id);
                contentProducts.Add(product.Id);
            }
            if (change.VatRate is decimal vatRate && product.VatRate != vatRate)
            {
                // As the editor and the bulk VAT: a card without "KDV Dahil mi" takes the default, included.
                product.VatRate = vatRate;
                product.VatInclusion = product.VatInclusion?.Trim().ToUpperInvariant() == "EXCLUDED" ? "EXCLUDED" : "INCLUDED";
                vatUpdated++;
                touchedProducts.Add(product.Id);
            }
        }
        // KATALOG-3: the name and the VAT are the model's too (the editor writes them to every colour card and shows the
        // cards' majority): a file row's name or VAT goes to every colour card of its model under the same brand, each card
        // with its own colour word in the name, so a later editor save does not quietly take the old value back. A card the
        // file names itself keeps the file's own value; a model code under two brands (ambiguous) is never crossed.
        if (modelFieldChanges.Count > 0)
        {
            var ownTitles = productChanges.Where(change => change.Title is not null).Select(change => change.ProductId).ToHashSet();
            var ownVats = productChanges.Where(change => change.VatRate is not null).Select(change => change.ProductId).ToHashSet();
            foreach (var change in modelFieldChanges)
            {
                foreach (var cardId in fieldCards[change.ProductId].Where(id => id != change.ProductId))
                {
                    if (!productById.TryGetValue(cardId, out var card))
                    {
                        card = await db.Products.SingleAsync(item => item.TenantId == tenantId && item.Id == cardId, cancellationToken);
                        productById[cardId] = card;
                    }
                    // KATALOG-14 phase D5b: a model is its code alone, so every colour of it takes the value (no brand check),
                    // through the tracker, so Ürün Hareketleri records each colour too ("Modelden uygulandı").
                    if (change.Title is { } title && !ownTitles.Contains(cardId)
                        && ProductFamilySharedFields.TitleFor(title, colours.GetValueOrDefault(change.ProductId), colours.GetValueOrDefault(cardId)) is var cardTitle && card.Title != cardTitle)
                    {
                        card.Title = cardTitle;
                        titlesUpdated++;
                        touchedProducts.Add(cardId);
                        contentProducts.Add(cardId);
                        fromModel.Add((cardId, null, ProductChangeFields.Title));
                    }
                    if (change.VatRate is decimal modelVat && !ownVats.Contains(cardId) && card.VatRate != modelVat)
                    {
                        card.VatRate = modelVat;
                        card.VatInclusion = card.VatInclusion?.Trim().ToUpperInvariant() == "EXCLUDED" ? "EXCLUDED" : "INCLUDED";
                        vatUpdated++;
                        touchedProducts.Add(cardId);
                        fromModel.Add((cardId, null, ProductChangeFields.VatRate));
                        fromModel.Add((cardId, null, ProductChangeFields.VatInclusion));
                    }
                    if (descriptionOf.TryGetValue(change.ProductId, out var modelDescription) && !descriptionOf.ContainsKey(cardId) && card.Description != modelDescription)
                    {
                        card.Description = modelDescription;
                        touchedProducts.Add(cardId);
                        contentProducts.Add(cardId);
                        fromModel.Add((cardId, null, ProductChangeFields.Description));
                    }
                }
            }
        }
        // The brand is the model's: it goes to every colour card of the model that has the row's own brand. A card of the
        // same code under another brand is never re-branded by another card's row (owner, 15.11.2026: such a group is only
        // warned about); its own row in the file changes it.
        var brandChanges = productChanges.Where(change => change.Brand is not null).ToList();
        if (brandChanges.Count > 0)
        {
            var modelCards = await ModelCardsAsync(tenantId, brandChanges.Select(change => change.ProductId).ToArray(), cancellationToken);
            var brandBefore = new Dictionary<Guid, Guid?>();
            foreach (var cardId in modelCards.Values.SelectMany(ids => ids).Concat(brandChanges.Select(change => change.ProductId)).Distinct())
            {
                if (!productById.TryGetValue(cardId, out var known))
                {
                    known = await db.Products.SingleAsync(item => item.TenantId == tenantId && item.Id == cardId, cancellationToken);
                    productById[cardId] = known;
                }
                brandBefore[cardId] = known.BrandId;
            }
            foreach (var change in brandChanges)
            {
                var match = await brandLookup.FindOrCreateAsync(change.Brand, now, cancellationToken);
                if (match.Created) brandsCreated++;
                foreach (var cardId in modelCards[change.ProductId])
                {
                    if (cardId != change.ProductId && brandBefore[cardId] != brandBefore[change.ProductId]) continue;
                    if (!productById.TryGetValue(cardId, out var card))
                    {
                        card = await db.Products.SingleAsync(item => item.TenantId == tenantId && item.Id == cardId, cancellationToken);
                        productById[cardId] = card;
                    }
                    if (card.BrandId == match.Id) continue;
                    card.BrandId = match.Id;
                    brandsUpdated++;
                    if (cardId != change.ProductId) fromModel.Add((cardId, null, ProductChangeFields.Brand));
                    touchedProducts.Add(cardId);
                    contentProducts.Add(cardId);
                }
            }
        }
        // KATALOG-14 phase D5b: the desi is the model's: the other sizes of the model take the file's value through the tracker
        // too (ModelMirror would write them raw), so Ürün Hareketleri records each of them ("Modelden uygulandı").
        if (desiChanges.Count > 0)
        {
            var desiOfModel = new Dictionary<Guid, decimal>();
            foreach (var change in desiChanges)
                if (modelOfDesi.GetValueOrDefault(byId[change.VariantId].ProductId) is Guid modelId) desiOfModel[modelId] = change.Desi!.Value;
            var modelIds = desiOfModel.Keys.ToArray();
            var fileVariantIds = changes.Select(change => change.VariantId).ToHashSet();
            var siblings = await (from variant in db.ProductVariants
                                  join product in db.Products on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
                                  where variant.TenantId == tenantId && product.FamilyId != null && modelIds.Contains(product.FamilyId.Value)
                                  select new { Variant = variant, ModelId = product.FamilyId!.Value }).ToListAsync(cancellationToken);
            foreach (var sibling in siblings.Where(item => !fileVariantIds.Contains(item.Variant.Id)))
            {
                var desi = desiOfModel[sibling.ModelId];
                if (sibling.Variant.Desi == desi) continue;
                sibling.Variant.Desi = desi;
                fromModel.Add((sibling.Variant.ProductId, sibling.Variant.Id, ProductChangeFields.Desi));
            }
        }
        touchedProducts.UnionWith(contentProducts);
        if (written.Count == 0 && touchedProducts.Count == 0) return ServiceResult<ProductTransferApplyResult>.Ok(new ProductTransferApplyResult(0, 0, 0, 0, 0, 0, 0, 0));

        // A title, barcode or model code edit is a local content edit: the products' marketplace links are marked as an
        // editor save marks them, so a later marketplace import does not overwrite it. Nothing is sent.
        foreach (var id in touchedProducts)
        {
            if (!productById.TryGetValue(id, out var product))
            {
                product = await db.Products.SingleAsync(item => item.TenantId == tenantId && item.Id == id, cancellationToken);
                productById[id] = product;
            }
            product.UpdatedAt = now;
            product.Version++;
            products.Add(id);
        }
        await CatalogLocalEdits.MarkLinksDirtyAsync(db, tenantId, contentProducts, CatalogLocalEdits.ProductField, cancellationToken);
        // A stock count set from the file is the owner's count, as in InventoryService: it closes the shortfall notices.
        await StockMovementWriter.CloseShortfallNoticesAsync(db, tenantId, stockSetItemIds, now, cancellationToken);
        await StockProjectionOutbox.EnqueueAsync(db, tenantId, projections, now, cancellationToken);
        // E: file prices are sent like panel edits: only the offers whose price changed, to their own connection.
        await StockProjectionOutbox.EnqueuePriceChangesAsync(db, tenantId, repricedOffers, now, cancellationToken);
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = "PRODUCT_TRANSFER_IMPORTED",
            TargetType = "ProductVariant",
            TargetId = null,
            Reason = $"Ürün dosyası içe aktarıldı: {written.Count} varyant ({products.Count} ürün); maliyet {costsUpdated} güncellendi, {costsCleared} silindi; desi {desiUpdated} güncellendi, {desiCleared} silindi; stok {stocksUpdated}; mağaza fiyatı {pricesUpdated}; ürün adı {titlesUpdated}; açıklama {descriptionsUpdated}; barkod {barcodesUpdated}; model kodu {modelCodesUpdated} güncellendi, {modelCodesCleared} silindi; panel fiyatı {panelPricesUpdated}; KDV {vatUpdated}; marka {brandsUpdated} ürün kartı ({brandsCreated} yeni marka).",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        // Ürün Hareketleri: maliyet, desi, barkod, model kodu, ürün adı and KDV as the file changed them.
        await ProductFieldChangeRecorder.RecordAsync(db, tenantId, ProductChangeSources.FileImport, userId, correlationId, now, cancellationToken);
        foreach (var entry in db.ChangeTracker.Entries<ProductChangeEntry>().Where(entry => entry.State == EntityState.Added && entry.Entity.Note is null && entry.Entity.Field is not null
                     && fromModel.Contains((entry.Entity.ProductId, entry.Entity.VariantId, entry.Entity.Field))))
            entry.Entity.Note = ModelAppliedNote;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ModelMirrorReport.Clear();
            db.ChangeTracker.Clear();
            return Stale("Bazı kayıtlar kayıt sırasında değişti; hiçbir değişiklik kaydedilmedi. Dosyayı yeniden yükleyin.");
        }
        catch (Exception exception) when (SerializableWriteRetry.IsLockConflict(exception))
        {
            // KATALOG-14 phase D5b review 3: another save of the same models (Toplu Değer Ata, a marketplace import) met
            // this one; nothing of the file is written and the owner can load it again.
            await transaction.RollbackAsync(CancellationToken.None);
            db.ModelMirrorReport.Clear();
            db.ChangeTracker.Clear();
            return ServiceResult<ProductTransferApplyResult>.Fail(LockConflictCode, "Aynı modellerin ürünleri şu anda başka bir işlemde (Toplu Değer Ata, pazaryeri aktarımı) kaydediliyor; hiçbir değişiklik kaydedilmedi. Birkaç saniye sonra dosyayı yeniden yükleyin.", 409);
        }
        catch (ModelRuleException conflict)
        {
            // KATALOG-14 phase D5b: the file broke the model-code rule (another brand, a code another model holds); nothing
            // of it is written.
            await transaction.RollbackAsync(CancellationToken.None);
            db.ModelMirrorReport.Clear();
            db.ChangeTracker.Clear();
            return ServiceResult<ProductTransferApplyResult>.Fail(conflict.Code, conflict.Message, 409);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // A barcode taken by another variant between the check above and the save.
            await transaction.RollbackAsync(CancellationToken.None);
            db.ModelMirrorReport.Clear();
            db.ChangeTracker.Clear();
            return ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_BARCODE_TAKEN", "Yazılmak istenen bir barkod kayıt sırasında başka bir varyanta verildi; hiçbir değişiklik kaydedilmedi. Dosyayı yeniden yükleyin.", 409);
        }
        return ServiceResult<ProductTransferApplyResult>.Ok(new ProductTransferApplyResult(
            written.Count, costsUpdated, costsCleared, desiUpdated, desiCleared, stocksUpdated, pricesUpdated, products.Count,
            titlesUpdated, barcodesUpdated, modelCodesUpdated, modelCodesCleared, panelPricesUpdated, vatUpdated, brandsUpdated, brandsCreated,
            ModelMirror.ColoursReached(db.ModelMirrorReport), descriptionsUpdated));

        void Written(ProductVariant variant)
        {
            written.Add(variant.Id);
            products.Add(variant.ProductId);
        }
    }

    private static bool ValidPrices(decimal sale, decimal list) =>
        sale > 0 && sale <= ProductTransferFileParser.MaximumPrice && list <= ProductTransferFileParser.MaximumPrice && list >= sale && decimal.Round(sale, 2) == sale && decimal.Round(list, 2) == list;

    private static bool ValidCode(string code) =>
        code.Trim() == code && code.Length is > 0 and <= ProductTransferFileParser.MaximumCodeLength && CatalogKeyNormalizer.Key(code).Length > 0 && !ImportFileReader.LooksLikeFormula(code);

    // The stores the export has price columns for: every connection that is not deleted and has offers, in a fixed
    // order. Two stores with one name get "(2)", "(3)" so each column pair names one store.
    private async Task<IReadOnlyList<ProductTransferStoreView>> StoresAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && connection.Status != "DELETED"
                && db.ChannelOffers.Any(offer => offer.TenantId == tenantId && offer.ConnectionId == connection.Id))
            .OrderBy(connection => connection.PlatformCode).ThenBy(connection => connection.DisplayName).ThenBy(connection => connection.Id)
            .Select(connection => new { connection.Id, connection.DisplayName })
            .ToListAsync(cancellationToken);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        return connections.Select(connection =>
        {
            var name = connection.DisplayName.Trim();
            var key = ProductTransferFileParser.HeaderKey(name);
            seen[key] = seen.GetValueOrDefault(key) + 1;
            return new ProductTransferStoreView(connection.Id, seen[key] == 1 ? name : $"{name} ({seen[key]})");
        }).ToList();
    }

    private async Task<Dictionary<Guid, decimal>> ReservedAsync(Guid tenantId, Guid[] itemIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, decimal>();
        foreach (var chunk in itemIds.Chunk(Chunk))
            foreach (var row in await db.StockReservations.Where(reservation => reservation.TenantId == tenantId && chunk.Contains(reservation.InventoryItemId) && reservation.Status == ReservationStatus.Active)
                         .GroupBy(reservation => reservation.InventoryItemId).Select(group => new { group.Key, Quantity = group.Sum(reservation => reservation.Quantity) }).ToListAsync(cancellationToken))
                result[row.Key] = row.Quantity;
        return result;
    }

    private static bool HoldsCatalog(ProductVariant variant, ProductTransferChange change) =>
        (!change.SetCost || variant.CostPrice == change.Cost) && (!change.SetDesi || variant.Desi == change.Desi)
        && (change.Barcode is null || variant.Barcode == change.Barcode) && (!change.SetModelCode || variant.ModelCode == change.ModelCode)
        && (change.PanelPrice is not { } panel || variant.DefaultSalePrice == panel.SalePrice && variant.DefaultListPrice == panel.ListPrice);

    private static bool HoldsProduct(Product product, ProductTransferProductChange change, Guid? currentBrandId) =>
        (change.Title is null || product.Title == change.Title) && (change.VatRate is null || product.VatRate == change.VatRate)
        && (change.Description is null || ProductTransferImportPlanner.CleanDescription(change.Description).Value is not { } description || product.Description == description)
        && (change.Brand is null || currentBrandId is Guid brandId && product.BrandId == brandId);

    // Each product with the colour cards of its model: the visible cards of its brand sharing its model code (a model is its
    // brand and its code, owner 17 Nov 2026; archived ones left out), the product itself always. A card without a model code
    // is a model of its own.
    private async Task<Dictionary<Guid, HashSet<Guid>>> ModelCardsAsync(Guid tenantId, Guid[] productIds, CancellationToken cancellationToken)
    {
        var codes = (await db.ProductVariants.AsNoTracking()
                .Where(variant => variant.TenantId == tenantId && productIds.Contains(variant.ProductId) && variant.ModelCode != null && variant.ModelCode != "")
                .Select(variant => new { variant.ProductId, variant.ModelCode, variant.SortOrder, variant.Id }).ToListAsync(cancellationToken))
            .GroupBy(variant => variant.ProductId)
            .ToDictionary(group => group.Key, group => ProductFamilyKeyPolicy.ModelCodeKey(group.OrderBy(variant => variant.SortOrder).ThenBy(variant => variant.Id).First().ModelCode));
        var keys = codes.Values.OfType<string>().ToHashSet(StringComparer.Ordinal);
        var cardsByKey = new Dictionary<(string Key, Guid? Brand), HashSet<Guid>>();
        var brandOf = new Dictionary<Guid, Guid?>();
        if (keys.Count > 0)
        {
            // KATALOG-8: only the cards that may hold one of the keys are read; the key still decides below.
            var candidates = ModelCodeOwnership.ProductIdsWithKeys(db, tenantId, keys);
            var rows = await (from variant in db.ProductVariants.AsNoTracking()
                              join product in EffectiveCatalog.Products(db) on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
                              where variant.TenantId == tenantId && variant.ModelCode != null && variant.ModelCode != "" && product.Status != ProductStatus.Archived
                                  && candidates.Contains(product.Id)
                              select new { product.Id, product.BrandId, variant.ModelCode }).Distinct().ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                brandOf[row.Id] = row.BrandId;
                if (ProductFamilyKeyPolicy.ModelCodeKey(row.ModelCode) is { } key && keys.Contains(key))
                {
                    if (!cardsByKey.TryGetValue((key, row.BrandId), out var cards)) cardsByKey[(key, row.BrandId)] = cards = [];
                    cards.Add(row.Id);
                }
            }
        }
        return productIds.ToDictionary(id => id, id => new HashSet<Guid>(codes.GetValueOrDefault(id) is { } key && brandOf.TryGetValue(id, out var brand) && cardsByKey.TryGetValue((key, brand), out var cards) ? cards : []) { id });
    }

    private static bool HoldsPrice(ChannelOffer offer, ProductTransferPriceChange price) => offer.SalePrice == price.SalePrice && offer.ListPrice == price.ListPrice;

    // The variants the export lists and the summary counts: those of products that are not archived, themselves not
    // archived.
    private IQueryable<ProductVariant> ListedVariants(Guid tenantId) =>
        from variant in db.ProductVariants.AsNoTracking()
        where variant.TenantId == tenantId && variant.Status != ProductStatus.Archived
        join product in EffectiveCatalog.Products(db) on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
        where product.Status != ProductStatus.Archived
        select variant;

    private static ServiceResult<ProductTransferApplyResult> Invalid(string code, string message) => ServiceResult<ProductTransferApplyResult>.Fail(code, message, 400);
    private static ServiceResult<ProductTransferApplyResult> Stale(string message) => ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_STALE", message, 409);
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    // Stock is a count: a whole number goes out as an integer cell ("2", not "2,00").
    private static object WholeStock(decimal onHand) => decimal.Truncate(onHand) == onHand ? (object)decimal.ToInt64(onHand) : onHand;

    private static decimal? Rounded(decimal? value) => value is decimal number ? Math.Round(number, 2, MidpointRounding.AwayFromZero) : null;
}
