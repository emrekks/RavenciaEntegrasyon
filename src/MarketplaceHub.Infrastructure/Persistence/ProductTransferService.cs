using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The products page's export and import: every listed variant to a workbook, and the fields the seller picks back from
// it. Cost and desi change the variant only. Stock is set the way the product editor sets it (ledger entry, projection
// to the stores with external writes on). A store price changes the existing offer and its price history, as the
// editor's price matrix does, and queues no price send. The product's version is left alone, since a version bump
// would read as a local content edit and keep later marketplace imports from refreshing the product's title and
// description.
public sealed class ProductTransferService(AppDbContext db, TimeProvider timeProvider) : IProductTransferService
{
    public const long MaximumFileBytes = 10 * 1024 * 1024;
    public const int MaximumChanges = ProductTransferFileParser.MaximumRows;
    private const long MaximumExpandedBytes = 64 * 1024 * 1024;
    private const int Chunk = 2_000;
    private const string ImportReason = "Ürün dosyası içe aktarma";

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

        // The panel price and VAT as the product list shows them (the first offer, an active one before others), the
        // main location's stock and each store's own prices.
        var offers = await db.ChannelOffers.AsNoTracking()
            .Where(offer => offer.TenantId == tenantId)
            .OrderByDescending(offer => offer.Status == "ACTIVE").ThenBy(offer => offer.Id)
            .Select(offer => new { offer.VariantId, offer.ConnectionId, offer.SalePrice, offer.ListPrice, offer.VatRate })
            .ToListAsync(cancellationToken);
        var offerByVariant = offers.GroupBy(offer => offer.VariantId).ToDictionary(group => group.Key, group => group.First());
        var offerByVariantStore = offers.GroupBy(offer => (offer.VariantId, offer.ConnectionId)).ToDictionary(group => group.Key, group => group.First());
        var stockByVariant = (await db.InventoryItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LocationCode == "MAIN")
            .Select(item => new { item.VariantId, item.OnHand })
            .ToListAsync(cancellationToken))
            .GroupBy(item => item.VariantId).ToDictionary(group => group.Key, group => group.First().OnHand);
        var stores = await StoresAsync(tenantId, cancellationToken);

        var header = new List<object?> { "Ürün Adı", "Varyant", "Stok Kodu", "Barkod", "Model Kodu", "Marka", "Kategori", "Durum", "Satış Fiyatı", "KDV Oranı", "Stok", "Maliyet (₺)", "Desi" };
        var widths = new List<double> { 48, 26, 22, 18, 18, 18, 22, 13, 14, 11, 10, 14, 10 };
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
            var offer = offerByVariant.GetValueOrDefault(row.Id);
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
                Rounded(CatalogPricePolicy.EffectiveSalePrice(offer?.SalePrice, row.DefaultSalePrice, row.ProductSalePrice)),
                offer is null ? null : "%" + offer.VatRate.ToString("0.##", CultureInfo.GetCultureInfo("tr-TR")),
                stockByVariant.TryGetValue(row.Id, out var onHand) ? onHand : null,
                Rounded(row.CostPrice),
                Rounded(row.Desi)
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
            new object?[] { "Her satır bir varyanttır. Yüklerken hangi alanların panele yazılacağını siz seçersiniz: Maliyet, Desi, Stok ve mağaza fiyatları. Seçmediğiniz sütunlar dosyada olsa da okunmaz." },
            new object?[] { "Ürün adı, varyant, model kodu, marka, kategori, durum, Satış Fiyatı ve KDV Oranı sütunları yalnız bilgi içindir; değiştirmeniz hiçbir şeyi değiştirmez." },
            new object?[] { "Maliyet: 1 adetin Türk lirası maliyeti, ürün kartındaki \"Maliyet\" alanıyla aynı değer. Kâr raporu bu değeri, müşterinin ödediği KDV dahil satış tutarından düşer. 0 geçerli bir maliyettir." },
            new object?[] { "Desi: kargo hacim ağırlığı. Raporlar kargo ücretini, pazaryeri kargo tutarı gelmediğinde bu değerle tahmin eder. 0'dan büyük olmalıdır." },
            new object?[] { "Stok: ana depodaki eldeki stok, tam sayı. Ürün sayfasındaki stok değişikliği gibi kaydedilir: stok gönderimi açık mağazalara yeni stok gönderilir." },
            new object?[] { "Mağaza fiyatları: her mağaza için satış ve liste fiyatı. Yalnız o mağazada fiyat kaydı olan varyantlar güncellenir; liste fiyatı satış fiyatından küçük olamaz. Fiyat panele kaydedilir, kendisi pazaryerine fiyat göndermez." },
            new object?[] { "Sayılar 123,45 ya da 1.234,56 biçiminde yazılabilir; ₺ ve TL işaretleri yok sayılır. Yalnız noktalı 1.234 belirsizdir ve reddedilir: 1234 ya da 1,234 yazın." },
            new object?[] { "Boş hücre değiştirilmez. Kayıtlı bir maliyeti ya da desiyi silmek için hücreye - ya da sil yazın; stok ve fiyat silinemez." },
            new object?[] { "Satır önce Stok Kodu, bulunamazsa Barkod ile eşleştirilir. Bu iki sütunu ve sütun başlıklarını değiştirmeyin." },
            new object?[] { "Dosyayı yükledikten sonra değişiklikler önce önizlenir; siz onaylamadan hiçbir şey kaydedilmez." },
            new object?[] { "Dosyayı .xlsx olarak kaydedin (CSV de kabul edilir) ve en fazla 20.000 satır, 10 MiB yükleyin. Formül içeren hücreler okunmaz." }
        };
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
            return ServiceResult<ProductTransferImportPreview>.Fail("PRODUCT_TRANSFER_FIELDS_INVALID", "Aktarılacak en az bir alan seçin: maliyet, desi, stok ya da mağaza fiyatları.", 400);
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
            join product in db.Products.AsNoTracking() on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
            select new { variant.Id, variant.ProductId, variant.Version, variant.Sku, variant.Barcode, product.Title, variant.OptionSignature, variant.CostPrice, variant.Desi })
            .ToListAsync(cancellationToken);
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
        var candidates = variants.Select(variant => new ProductTransferCandidate(
            variant.Id, variant.ProductId, variant.Version, variant.Sku, variant.Barcode, variant.Title, variant.OptionSignature, variant.CostPrice, variant.Desi,
            stockByVariant.TryGetValue(variant.Id, out var onHand) ? onHand : null,
            offersByVariant.GetValueOrDefault(variant.Id))).ToList();
        var preview = ProductTransferImportPlanner.Plan(safeName, file.Rows, candidates, fields, stores, file.AvailableFields);
        return ServiceResult<ProductTransferImportPreview>.Ok(preview);
    }

    public async Task<ServiceResult<ProductTransferApplyResult>> ApplyAsync(Guid tenantId, Guid userId, ApplyProductTransferCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var changes = command.Changes ?? [];
        if (changes.Count == 0) return Invalid("PRODUCT_TRANSFER_CHANGES_REQUIRED", "Kaydedilecek değişiklik yok.");
        if (changes.Count > MaximumChanges) return Invalid("PRODUCT_TRANSFER_CHANGES_TOO_MANY", $"Tek seferde en fazla {MaximumChanges:N0} varyant kaydedilebilir.");
        if (changes.Select(change => change.VariantId).Distinct().Count() != changes.Count)
            return Invalid("PRODUCT_TRANSFER_CHANGES_DUPLICATE", "Aynı varyant birden çok kez gönderildi.");
        if (changes.Any(change => !change.SetCost && !change.SetDesi && change.Stock is null && (change.Prices ?? []).Count == 0))
            return Invalid("PRODUCT_TRANSFER_CHANGES_EMPTY", "Her değişiklik en az bir alanı değiştirmelidir.");
        if (changes.Any(change => change.SetCost && (change.Cost is < 0 or > ProductTransferFileParser.MaximumCost || change.Cost is decimal cost && decimal.Round(cost, 2) != cost)))
            return Invalid("PRODUCT_TRANSFER_COST_INVALID", "Maliyet 0 ile 10.000.000 ₺ arasında ve en fazla iki ondalıklı olmalıdır.");
        if (changes.Any(change => change.SetDesi && (change.Desi is <= 0 or > ProductTransferFileParser.MaximumDesi || change.Desi is decimal desi && decimal.Round(desi, 2) != desi)))
            return Invalid("PRODUCT_TRANSFER_DESI_INVALID", "Desi 0'dan büyük, en fazla 10.000 ve en fazla iki ondalıklı olmalıdır.");
        if (changes.Any(change => change.Stock is { } stock && (stock.OnHand is < 0 or > ProductTransferFileParser.MaximumStock || stock.OnHand != decimal.Truncate(stock.OnHand))))
            return Invalid("PRODUCT_TRANSFER_STOCK_INVALID", "Stok 0 ile 1.000.000.000 arasında bir tam sayı olmalıdır.");
        var priceChanges = changes.SelectMany(change => (change.Prices ?? []).Select(price => (change.VariantId, Price: price))).ToList();
        if (priceChanges.Any(item => item.Price.SalePrice is <= 0 or > ProductTransferFileParser.MaximumPrice || item.Price.ListPrice > ProductTransferFileParser.MaximumPrice || item.Price.ListPrice < item.Price.SalePrice
            || decimal.Round(item.Price.SalePrice, 2) != item.Price.SalePrice || decimal.Round(item.Price.ListPrice, 2) != item.Price.ListPrice))
            return Invalid("PRODUCT_TRANSFER_PRICE_INVALID", "Mağaza fiyatı 0'dan büyük, en fazla 10.000.000 ₺ ve iki ondalıklı olmalı; liste fiyatı satış fiyatından küçük olamaz.");
        if (priceChanges.Select(item => item.Price.OfferId).Distinct().Count() != priceChanges.Count)
            return Invalid("PRODUCT_TRANSFER_CHANGES_DUPLICATE", "Aynı mağaza fiyatı birden çok kez gönderildi.");

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
        var itemByVariant = items.GroupBy(item => item.VariantId).ToDictionary(group => group.Key, group => group.First());
        var offerById = offers.ToDictionary(offer => offer.Id);
        if (stockIds.Any(id => !itemByVariant.ContainsKey(id)) || priceChanges.Any(item => !offerById.TryGetValue(item.Price.OfferId, out var offer) || offer.VariantId != item.VariantId))
            return Stale("Bazı stok ya da fiyat kayıtları artık yok; dosyayı yeniden yükleyin.");
        // A value that already holds what is asked is done (a repeated save, or the same edit made elsewhere); any
        // other change since the preview was read means the preview is out of date and nothing is written. Everything
        // is checked before anything is changed, so a refusal leaves nothing half applied.
        if (changes.Any(change => byId[change.VariantId] is var variant && !HoldsCatalog(variant, change) && variant.Version != change.Version && (change.SetCost || change.SetDesi))
            || changes.Any(change => change.Stock is { } stock && itemByVariant[change.VariantId] is var item && item.OnHand != stock.OnHand && item.OnHand != stock.ExpectedOnHand)
            || priceChanges.Any(change => offerById[change.Price.OfferId] is var offer && !HoldsPrice(offer, change.Price) && offer.Version != change.Price.Version))
            return Stale("Bazı varyantlar önizlemeden sonra değişti; dosyayı yeniden yükleyin.");

        var now = timeProvider.GetUtcNow();
        var applyId = Guid.CreateVersion7();
        int costsUpdated = 0, costsCleared = 0, desiUpdated = 0, desiCleared = 0, stocksUpdated = 0, pricesUpdated = 0;
        var written = new HashSet<Guid>();
        var products = new HashSet<Guid>();
        var projections = new List<StockProjectionChange>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reservedByItem = await ReservedAsync(tenantId, items.Select(item => item.Id).ToArray(), cancellationToken);
        foreach (var change in changes)
        {
            var variant = byId[change.VariantId];
            if ((change.SetCost || change.SetDesi) && !HoldsCatalog(variant, change))
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
                variant.UpdatedAt = now;
                variant.Version++;
                Written(variant);
            }
            if (change.Stock is { } stock && itemByVariant[change.VariantId] is var item && item.OnHand != stock.OnHand)
            {
                // As InventoryService.SetOnHandAsync: a manual adjustment in the ledger, then the projection outbox.
                var reserved = reservedByItem.GetValueOrDefault(item.Id);
                var delta = stock.OnHand - item.OnHand;
                item.OnHand = stock.OnHand;
                item.Reserved = reserved;
                item.Available = InventoryProjection.Available(stock.OnHand, reserved);
                item.ProjectionVersion++;
                item.Version++;
                var sourceEventId = $"product-transfer:{applyId:N}:{variant.Id:N}";
                db.StockLedgerEntries.Add(new StockLedgerEntry
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    InventoryItemId = item.Id,
                    MovementType = "MANUAL_ADJUSTMENT",
                    QuantityDelta = delta,
                    SourceType = "MANUAL",
                    SourceId = ImportReason,
                    SourceEventId = sourceEventId,
                    IdempotencyKey = sourceEventId,
                    OccurredAt = now,
                    RecordedAt = now,
                    ActorUserId = userId,
                    CorrelationId = correlationId
                });
                projections.Add(new StockProjectionChange(item.VariantId, item.ProjectionVersion, $"manual:{sourceEventId}"));
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
                pricesUpdated++;
                Written(variant);
            }
        }
        if (written.Count == 0) return ServiceResult<ProductTransferApplyResult>.Ok(new ProductTransferApplyResult(0, 0, 0, 0, 0, 0, 0, 0));

        await StockProjectionOutbox.EnqueueAsync(db, tenantId, projections, now, cancellationToken);
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = "PRODUCT_TRANSFER_IMPORTED",
            TargetType = "ProductVariant",
            TargetId = null,
            Reason = $"Ürün dosyası içe aktarıldı: {written.Count} varyant ({products.Count} ürün); maliyet {costsUpdated} güncellendi, {costsCleared} silindi; desi {desiUpdated} güncellendi, {desiCleared} silindi; stok {stocksUpdated}; mağaza fiyatı {pricesUpdated}.",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return Stale("Bazı kayıtlar kayıt sırasında değişti; hiçbir değişiklik kaydedilmedi. Dosyayı yeniden yükleyin.");
        }
        return ServiceResult<ProductTransferApplyResult>.Ok(new ProductTransferApplyResult(written.Count, costsUpdated, costsCleared, desiUpdated, desiCleared, stocksUpdated, pricesUpdated, products.Count));

        void Written(ProductVariant variant)
        {
            written.Add(variant.Id);
            products.Add(variant.ProductId);
        }
    }

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
        (!change.SetCost || variant.CostPrice == change.Cost) && (!change.SetDesi || variant.Desi == change.Desi);

    private static bool HoldsPrice(ChannelOffer offer, ProductTransferPriceChange price) => offer.SalePrice == price.SalePrice && offer.ListPrice == price.ListPrice;

    // The variants the export lists and the summary counts: those of products that are not archived, themselves not
    // archived.
    private IQueryable<ProductVariant> ListedVariants(Guid tenantId) =>
        from variant in db.ProductVariants.AsNoTracking()
        where variant.TenantId == tenantId && variant.Status != ProductStatus.Archived
        join product in db.Products.AsNoTracking() on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
        where product.Status != ProductStatus.Archived
        select variant;

    private static ServiceResult<ProductTransferApplyResult> Invalid(string code, string message) => ServiceResult<ProductTransferApplyResult>.Fail(code, message, 400);
    private static ServiceResult<ProductTransferApplyResult> Stale(string message) => ServiceResult<ProductTransferApplyResult>.Fail("PRODUCT_TRANSFER_STALE", message, 409);
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static decimal? Rounded(decimal? value) => value is decimal number ? Math.Round(number, 2, MidpointRounding.AwayFromZero) : null;
}
