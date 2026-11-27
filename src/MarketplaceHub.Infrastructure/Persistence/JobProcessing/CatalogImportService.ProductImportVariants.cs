using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

using CategoryAttributeContext = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.CategoryAttributeContext;
using LocalCategoryAttribute = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.LocalCategoryAttribute;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class CatalogImportService
{
    private async Task<Category?> UpsertCatalogCategory(Guid tenantId, string? name, CancellationToken cancellationToken)
    {
        var leafName = CategoryLeafName(name, name);
        var normalized = NormalizeCatalogKey(leafName, 160);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var legacyNormalized = NormalizeCatalogKey(name, 160);
        var keys = CatalogLookupKeys(leafName).Concat(CatalogLookupKeys(name)).Distinct(StringComparer.Ordinal).ToArray();
        var category = db.Categories.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ParentId == null && (CatalogKeyNormalizer.SameKey(x.NormalizedName, normalized) || CatalogKeyNormalizer.SameKey(x.NormalizedName, legacyNormalized)))
            ?? await db.Categories.Where(x => x.TenantId == tenantId && x.ParentId == null && keys.Contains(x.NormalizedName)).OrderBy(x => x.NormalizedName == normalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (category is not null)
        {
            // A panel category keeps its name, path and state. Its leaf flag follows its children: a category
            // the user split into sub-categories is no longer a leaf and cannot take products.
            if (!category.IsActive) return null;
            var isLeaf = !await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.ParentId == category.Id && x.IsActive, cancellationToken);
            if (category.IsLeaf != isLeaf) { category.IsLeaf = isLeaf; category.UpdatedAt = timeProvider.GetUtcNow(); category.Version++; }
            return isLeaf ? category : null;
        }
        category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = leafName, NormalizedName = normalized, Path = leafName, Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = timeProvider.GetUtcNow(), UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
        db.Categories.Add(category);
        return category;
    }

    // Fills the desi of the variants linked to this snapshot's variants on this connection, only where the local desi
    // is empty. Nothing else of the product or variant changes, and a desi already set is never overwritten.
    private async Task FillMissingImportedDesi(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var desiByExternalId = snapshot.Variants
            .Where(variant => variant.Desi is not null && !string.IsNullOrWhiteSpace(variant.ExternalVariantId))
            .GroupBy(variant => Short(variant.ExternalVariantId, 256), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Desi!.Value, StringComparer.Ordinal);
        if (desiByExternalId.Count == 0) return;
        var externalIds = desiByExternalId.Keys.ToArray();
        var targets = await (from variantLink in db.MarketplaceVariantLinks
                             join variant in db.ProductVariants on new { variantLink.TenantId, variantLink.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                             where variantLink.TenantId == tenantId && variantLink.ConnectionId == connectionId
                                 && externalIds.Contains(variantLink.ExternalId)
                             select new { variantLink.ExternalId, Variant = variant }).ToListAsync(cancellationToken);
        // KATALOG-14 D5 step B2b: "empty" is the desi every reader sees (the model's), not the variant's copy.
        await EffectiveCatalog.RebaseAsync(db, cancellationToken);
        foreach (var target in targets)
        {
            if (target.Variant.Desi is not null || !desiByExternalId.TryGetValue(target.ExternalId, out var desi)) continue;
            target.Variant.Desi = desi;
            target.Variant.UpdatedAt = now;
            target.Variant.Version++;
            run.UpdatedCount++;
        }
    }

    private async Task<ProductVariant?> UpsertCatalogVariant(Guid tenantId, Guid connectionId, Product product, RemoteCatalogVariant remote, int sortOrder, CategoryAttributeContext? categoryContext, ConnectionInventoryPolicy? inventoryPolicy, DateTimeOffset now, CancellationToken cancellationToken, bool observeOnly = false, bool preferBarcode = false, bool onlyExistingVariants = false, bool updatePrices = false, bool updateStock = false)
    {
        var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
        var skuNormalized = NormalizeCatalogKey(sku, 160);
        if (string.IsNullOrWhiteSpace(skuNormalized)) return null;
        var barcode = string.IsNullOrWhiteSpace(remote.Barcode) ? null : Short(remote.Barcode.Trim(), 160);
        // A variant without a barcode stores NULL: "" would collide under the unique (tenant, barcode) index
        // with the next barcode-less variant (K14).
        var barcodeNormalized = NullIfEmpty(NormalizeCatalogKey(barcode, 160));
        var skuKeys = CatalogLookupKeys(sku);
        var barcodeKeys = CatalogLookupKeys(barcode);
        var externalVariantId = Short(remote.ExternalVariantId, 256);
        var optionSignature = categoryContext is null ? OptionSignature(remote.Options) : await PanelOptionSignatureAsync(tenantId, connectionId, categoryContext, remote.Options, cancellationToken);
        var link = await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalVariantId, cancellationToken);
        ProductVariant? variant = null;
        if (link is not null)
        {
            var linkedVariant = await db.ProductVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == link.VariantId, cancellationToken);
            // A stale Shopify variant link must not override a barcode match
            // inside the resolved local parent product.
            if (linkedVariant is not null
                && linkedVariant.ProductId == product.Id
                && (!preferBarcode || CatalogKeyNormalizer.SameKey(linkedVariant.BarcodeNormalized, barcodeNormalized) || string.IsNullOrEmpty(linkedVariant.BarcodeNormalized) && barcodeNormalized is null))
                variant = linkedVariant;
        }
        // Lookups accept both stored key forms and prefer the shared key, so a legacy row and a recomputed
        // row of the same code never make a single-row lookup throw.
        if (preferBarcode && barcodeNormalized is not null)
            variant ??= db.ProductVariants.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && CatalogKeyNormalizer.SameKey(x.BarcodeNormalized, barcodeNormalized))
                ?? await db.ProductVariants.Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized)).OrderBy(x => x.BarcodeNormalized == barcodeNormalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (preferBarcode && onlyExistingVariants && variant is null && barcodeNormalized is not null)
            variant = db.ProductVariants.Local.FirstOrDefault(x => x.TenantId == tenantId && CatalogKeyNormalizer.SameKey(x.BarcodeNormalized, barcodeNormalized))
                ?? await db.ProductVariants.Where(x => x.TenantId == tenantId && x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized)).OrderBy(x => x.BarcodeNormalized == barcodeNormalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (!preferBarcode)
            variant ??= db.ProductVariants.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && CatalogKeyNormalizer.SameKey(x.SkuNormalized, skuNormalized))
                ?? await db.ProductVariants.Where(x => x.TenantId == tenantId && skuKeys.Contains(x.SkuNormalized)).OrderBy(x => x.ProductId == product.Id ? 0 : 1).ThenBy(x => x.SkuNormalized == skuNormalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (variant is not null && variant.ProductId != product.Id && !(preferBarcode && onlyExistingVariants))
        {
            await RecordIssue(tenantId, $"product-sync-variant-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_CONFLICT", "Pazar yeri varyantı başka bir yerel üründe kullanılan stok koduyla eşleşti; mevcut kayıt korunarak atlandı.", cancellationToken);
            return null;
        }
        if (!preferBarcode && variant is null && barcodeNormalized is not null)
            variant = await db.ProductVariants.Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized)).OrderBy(x => x.BarcodeNormalized == barcodeNormalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (variant is null && onlyExistingVariants)
            return null;

        var isNewVariant = variant is null;
        if (variant is null)
        {
            variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, SortOrder = sortOrder, Sku = sku, SkuNormalized = skuNormalized, Barcode = barcode, BarcodeNormalized = barcodeNormalized, ModelCode = Short(remote.ModelCode, 160), OptionSignature = optionSignature, Status = remote.Archived ? ProductStatus.Archived : ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
            // Owner rule of 18 Nov 2026: a variant first read from a marketplace takes that marketplace's price as its panel
            // price (list and sale); a later import never changes it. No price (empty or 0) writes none.
            if (PanelPriceOfferPolicy.ImportedPanelPrices(remote.SalePrice, remote.ListPrice) is { } panelPrice)
            {
                (variant.DefaultListPrice, variant.DefaultSalePrice) = (panelPrice.ListPrice, panelPrice.SalePrice);
                // Ürün Hareketleri: the panel price the import gave the new variant, with its marketplace.
                if (ProductChangeLog.DefaultPrice(tenantId, variant.ProductId, variant.Id, (null, null), (panelPrice.ListPrice, panelPrice.SalePrice), ProductChangeSources.MarketplaceImport, null,
                        $"product-import:{connectionId:N}", now, "İlk çekildiği pazaryerinin fiyatı panel fiyatı olarak kaydedildi.") is { } priceEntry)
                    db.ProductChangeEntries.Add(priceEntry);
            }
            db.ProductVariants.Add(variant);
            run.InsertedCount++;
        }
        else
        {
            var nextModelCode = preferBarcode ? null : Short(remote.ModelCode, 160);
            // C-9: the marketplace changing this variant's model code to one a card it does not know holds (no link on
            // this connection) would merge two products into one family; the panel keeps its code and says why.
            if (!observeOnly && ModelCodeUniquenessPolicy.ChangedCodes([(variant.ModelCode, nextModelCode)]).Count > 0
                && await ModelCodeOwnership.FindUnlinkedHolderAsync(db, tenantId, connectionId, product.BrandId, nextModelCode!, product.Id, cancellationToken) is { } modelCodeHolder)
            {
                await RecordIssue(tenantId, $"product-sync-model-code-conflict:{connectionId}:{variant.Id:N}", ModelCodeUniquenessPolicy.ChangeConflictCode,
                    $"Pazaryerindeki yeni model kodu ({nextModelCode}) '{modelCodeHolder.Title}' ürününde kullanılıyor; iki ürün birleşmesin diye {variant.Sku} varyantının model kodu değiştirilmedi.", cancellationToken);
                nextModelCode = variant.ModelCode;
            }
            // A stored key in the legacy form that names the same code is kept until the data recompute, so
            // rewriting it can never collide with another row under the unique index. "" becomes NULL.
            var nextSkuNormalized = CatalogKeyNormalizer.SameKey(variant.SkuNormalized, skuNormalized) ? variant.SkuNormalized : skuNormalized;
            var nextBarcodeNormalized = CatalogKeyNormalizer.SameKey(variant.BarcodeNormalized, barcodeNormalized) ? variant.BarcodeNormalized : barcodeNormalized;
            if (!observeOnly && (variant.SortOrder != sortOrder || variant.Sku != sku || variant.SkuNormalized != nextSkuNormalized || variant.Barcode != barcode || variant.BarcodeNormalized != nextBarcodeNormalized || variant.ModelCode != nextModelCode || variant.OptionSignature != optionSignature))
            {
                // The variant's own status is the user's (report item 19); the listing's archived/closed state is kept
                // on its link (SaleState) and its offer, never copied onto the panel variant.
                variant.SortOrder = sortOrder; variant.Sku = sku; variant.SkuNormalized = nextSkuNormalized; variant.Barcode = barcode; variant.BarcodeNormalized = nextBarcodeNormalized; variant.ModelCode = nextModelCode; variant.OptionSignature = optionSignature; variant.UpdatedAt = now; variant.Version++;
                run.UpdatedCount++;
            }
        }
        // The marketplace desi fills an empty local desi (also for observed catalogs: it only adds a value). A desi
        // the person entered or an earlier import set is never overwritten. "Empty" is the model's desi (step B2b).
        if (!isNewVariant) await EffectiveCatalog.RebaseAsync(db, cancellationToken);
        if (variant.Desi is null && remote.Desi is { } remoteDesi)
        {
            variant.Desi = remoteDesi;
            if (!isNewVariant) { variant.UpdatedAt = now; variant.Version++; run.UpdatedCount++; }
        }
        if (link is not null && link.VariantId != variant.Id)
        {
            var conflictingVariantLink = db.MarketplaceVariantLinks.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id && x.Id != link.Id)
                ?? await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id && x.Id != link.Id, cancellationToken);
            if (conflictingVariantLink is not null)
            {
                await RecordIssue(tenantId, $"product-sync-variant-link-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_LINK_CONFLICT", "Aynı yerel varyantın başka bir Shopify varyant linki zaten var; ikinci link güvenli biçimde atlandı.", cancellationToken);
                return null;
            }
            link.VariantId = variant.Id;
            link.Version++;
        }
        if (link is null)
        {
            var existingVariantLink = db.MarketplaceVariantLinks.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id)
                ?? await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id, cancellationToken);
            if (existingVariantLink is null)
            {
                var importedLink = new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variant.Id, ExternalId = externalVariantId, Version = 1 };
                ListingSaleStateReads.ApplyToNewLink(importedLink, remote.SaleState, remote.SaleStateReason, now);
                db.MarketplaceVariantLinks.Add(importedLink);
                run.InsertedCount++;
            }
            // A link written for a panel publication before the marketplace named the variant (a "listing:" placeholder)
            // takes the variant id the import read, unless another link already holds that id.
            else if (MarketplaceVariantLinkHealth.IsListingPlaceholder(existingVariantLink.ExternalId)
                && !await ExternalIdTaken(tenantId, connectionId, externalVariantId, existingVariantLink.Id, cancellationToken))
            {
                existingVariantLink.ExternalId = externalVariantId;
                existingVariantLink.Version++;
            }
            else if (!string.Equals(existingVariantLink.ExternalId, externalVariantId, StringComparison.Ordinal))
            {
                await RecordIssue(tenantId, $"product-sync-variant-link-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_LINK_CONFLICT", "Aynı yerel varyantın başka bir pazar yeri varyant bağlantısı zaten var; ikinci bağlantı güvenli biçimde atlandı.", cancellationToken);
                return null;
            }
        }
        if (!observeOnly || isNewVariant)
            await UpsertCatalogOptions(tenantId, connectionId, product.Id, variant.Id, remote.Options, categoryContext, cancellationToken);
        if ((!observeOnly || isNewVariant) && categoryContext is not null)
            await UpsertProductAttributeAssignments(tenantId, connectionId, product, variant, remote.Options, categoryContext, cancellationToken);
        await UpsertCatalogOfferAndInventory(tenantId, connectionId, variant, remote, inventoryPolicy, now, cancellationToken, updateOffer: isNewVariant || updatePrices, observeOnly: observeOnly, updateStock: isNewVariant || updateStock, forceStockUpdate: !isNewVariant && updateStock, updatePanelPrice: updatePrices && !isNewVariant);
        if ((!observeOnly || isNewVariant) && remote.ImageUrls is not null)
            await UpsertCatalogMedia(tenantId, product, variant.Id, remote.ImageUrls, $"{product.Title} · {optionSignature}", cancellationToken);
        // A Trendyol size added to a card the panel already holds, without a list of its own from Trendyol (any size after
        // its colour's first), starts with the editor's add rule (InheritedVariantPictureAssetIds).
        if (isNewVariant && !observeOnly && db.Entry(product).State != EntityState.Added && remote.ImageUrls is not { Count: > 0 })
        {
            var altText = Short($"{product.Title} · {optionSignature}", 320);
            foreach (var (assetId, index) in (await InheritedVariantPictureAssetIds(tenantId, product, optionSignature, cancellationToken)).Select((assetId, index) => (assetId, index)))
                db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, VariantId = variant.Id, FileAssetId = assetId, MediaRole = index == 0 ? "PRIMARY" : "GALLERY", SortOrder = index, AltText = altText, Status = "ACTIVE" });
        }
        return variant;
    }

    private static RemoteCatalogProduct MergeCatalogSnapshots(IEnumerable<RemoteCatalogProduct> source)
    {
        var snapshots = source.ToList();
        var first = snapshots[0];
        var variants = snapshots
            .SelectMany(snapshot => snapshot.Variants)
            .GroupBy(VariantMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToList();
                var last = entries[^1];
                var hasImagePayload = entries.Any(entry => entry.ImageUrls is not null);
                var imageUrls = CatalogImageIdentity.DistinctUrls(entries
                    .Where(entry => entry.ImageUrls is not null)
                    .SelectMany(entry => entry.ImageUrls!));
                return last with { ImageUrls = hasImagePayload ? imageUrls : null };
            })
            .ToList();
        var images = CatalogImageIdentity.DistinctUrls(
            snapshots.SelectMany(snapshot => snapshot.ImageUrls.Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? []))));
        return first with
        {
            ProductMainId = snapshots.Select(x => x.ProductMainId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            Title = snapshots.Select(x => x.Title).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? first.Title,
            Description = snapshots.Select(x => x.Description).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? first.Description,
            BrandExternalId = snapshots.Select(x => x.BrandExternalId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            BrandName = snapshots.Select(x => x.BrandName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            CategoryExternalId = snapshots.Select(x => x.CategoryExternalId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            CategoryName = snapshots.Select(x => x.CategoryName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            ImageUrls = images,
            Variants = variants,
            RawJson = snapshots[^1].RawJson
        };
    }

    private static string VariantMergeKey(RemoteCatalogVariant variant) =>
        !string.IsNullOrWhiteSpace(variant.ExternalVariantId)
            ? $"id:{variant.ExternalVariantId}"
            : $"sku:{NormalizeCatalogKey(variant.Sku, 160)}";

    private async Task UpsertCatalogOfferAndInventory(Guid tenantId, Guid connectionId, ProductVariant variant, RemoteCatalogVariant remote, ConnectionInventoryPolicy? inventoryPolicy, DateTimeOffset now, CancellationToken cancellationToken, bool updateOffer = true, bool observeOnly = false, bool updateStock = true, bool forceStockUpdate = false, bool updatePanelPrice = false)
    {
        if (observeOnly)
        {
            await UpsertShopifyInventoryObservations(tenantId, connectionId, variant.Id, remote, now, cancellationToken);
        }
        if (updateStock && remote.StockQuantity is decimal stockQuantity)
        {
            var observedRemoteQuantity = decimal.Round(Math.Max(0m, stockQuantity), 4, MidpointRounding.ToEven);
            var applyRemoteQuantityToOnHand = InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(inventoryPolicy?.AuthorityMode);
            var inventory = db.InventoryItems.Local.FirstOrDefault(x => x.TenantId == tenantId && x.VariantId == variant.Id && x.LocationCode == "MAIN")
                ?? await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variant.Id && x.LocationCode == "MAIN", cancellationToken);
            if (inventory is null)
            {
                var opening = new InventoryItem
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    VariantId = variant.Id,
                    LocationCode = "MAIN",
                    // The first catalog snapshot initializes an untouched
                    // inventory row. Subsequent writes still require the
                    // explicit remote-authoritative mode.
                    OnHand = observeOnly && !forceStockUpdate ? 0 : observedRemoteQuantity,
                    Reserved = 0,
                    Available = observeOnly && !forceStockUpdate ? 0 : observedRemoteQuantity,
                    ObservedRemoteQuantity = observedRemoteQuantity,
                    ObservedRemoteAt = now,
                    ReconciledAt = now,
                    ProjectionVersion = 1,
                    Version = 1
                };
                db.InventoryItems.Add(opening);
                // The opening stock is a stock move like any other, so Ürün Hareketleri shows where the first number
                // came from. It is not an on-hand movement for MarketplaceStockImportPolicy (it is the remote number).
                if (opening.OnHand != 0)
                {
                    run.StockChangedVariantIds.Add(variant.Id);
                    var openingEventId = $"catalog-opening:{connectionId:N}:{opening.Id:N}";
                    StockMovementWriter.Record(db, tenantId, opening.Id, opening.OnHand, CatalogStockSource(ProductActivityTypes.ImportOpening, connectionId, openingEventId, now, now));
                }
            }
            else
            {
                var reconciliationWasMissing = inventory.ReconciledAt is null;
                var seedInitialOnHand = InventoryAuthorityPolicy.ShouldSeedInitialOnHand(inventory);
                var applyQuantityToOnHand = (!observeOnly || forceStockUpdate) && (forceStockUpdate || applyRemoteQuantityToOnHand || seedInitialOnHand);
                var targetOnHand = applyQuantityToOnHand ? await MarketplaceStockTargetAsync(tenantId, inventory, observedRemoteQuantity, now, cancellationToken) : inventory.OnHand;
                var projectionChanged = applyQuantityToOnHand && inventory.OnHand != targetOnHand;
                var observationChanged = inventory.ObservedRemoteQuantity != observedRemoteQuantity;
                if (projectionChanged)
                {
                    run.StockChangedVariantIds.Add(variant.Id);
                    // K-2: the change goes through the stock ledger and reaches every connection selling the variant,
                    // like any other stock movement.
                    // The row is keyed by the projection version the move gives the item; the move bumps the row version.
                    var eventId = $"catalog-stock:{connectionId:N}:{inventory.Id:N}:v{inventory.ProjectionVersion + 1}";
                    var change = StockMovementWriter.SetOnHand(db, inventory, targetOnHand, CatalogStockSource(MarketplaceStockImportPolicy.MovementType, connectionId, eventId, MarketplaceStockReadAt(now), now));
                    await StockProjectionOutbox.EnqueueAsync(db, tenantId, [change], now, cancellationToken);
                }
                inventory.ObservedRemoteQuantity = observedRemoteQuantity;
                inventory.ObservedRemoteAt = now;
                inventory.ReconciledAt = now;
                if (!projectionChanged && (observationChanged || reconciliationWasMissing))
                    inventory.Version++;
            }
        }

        if (!updateOffer) return;
        if (remote.SalePrice is null && remote.ListPrice is null) return;
        var salePrice = decimal.Round(Math.Max(0m, remote.SalePrice ?? remote.ListPrice ?? 0m), 4, MidpointRounding.ToEven);
        var listPrice = decimal.Round(Math.Max(salePrice, Math.Max(0m, remote.ListPrice ?? salePrice)), 4, MidpointRounding.ToEven);
        var currency = NormalizeCurrency(remote.Currency);
        var status = remote.Archived ? "INACTIVE" : "ACTIVE";
        var offer = db.ChannelOffers.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id)
            ?? await db.ChannelOffers.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id, cancellationToken);
        var cardVatRate = offer is null && remote.VatRate is null ? await CardOrModelVatRateAsync(tenantId, variant.ProductId, variant.ModelCode, cancellationToken) : null;
        var (vatRate, vatUnknown) = CatalogOfferVatRate(remote.VatRate, offer?.VatRate, cardVatRate);
        var importPlatformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        var observationSource = CatalogImportSource(importPlatformCode, observeOnly);
        // "Fiyat ve Stokları Güncelle" (owner decision of 18 Nov 2026, the confirmed overwrite): the marketplace's price becomes
        // the panel price of an existing variant (a price of 0 or none changes nothing), unless this offer keeps its own
        // price (Özel Fiyat on: only the offer takes it, below). One rule: the other offers without Özel Fiyat are pulled to
        // the new panel price with their previous price kept as the known one and their send marks moved, so nothing is
        // queued here; the difference goes with each variant's next stock send.
        if (updatePanelPrice && offer?.FollowsPanelPrice != false && PanelPriceOfferPolicy.ImportedPanelPrices(remote.SalePrice ?? remote.ListPrice, remote.ListPrice) is { } marketplacePanel
            && (variant.DefaultSalePrice != marketplacePanel.SalePrice || variant.DefaultListPrice != marketplacePanel.ListPrice))
        {
            if (ProductChangeLog.DefaultPrice(tenantId, variant.ProductId, variant.Id, (variant.DefaultListPrice, variant.DefaultSalePrice), (marketplacePanel.ListPrice, marketplacePanel.SalePrice),
                    ProductChangeSources.MarketplaceImport, null, $"product-import:{connectionId:N}", now, $"Fiyat ve Stokları Güncelle: {importPlatformCode} fiyatı panel fiyatı olarak kaydedildi.") is { } priceEntry)
                db.ProductChangeEntries.Add(priceEntry);
            (variant.DefaultListPrice, variant.DefaultSalePrice) = (marketplacePanel.ListPrice, marketplacePanel.SalePrice);
            variant.UpdatedAt = now;
            // The other connections' offers follow once per product (FlushPanelRepricesAsync), not once per variant.
            run.PendingPanelReprices[variant.Id] = new(marketplacePanel.SalePrice, null, marketplacePanel.ListPrice, null, true);
        }
        // One rule (owner decision of 18 Nov 2026): an offer without Özel Fiyat on a variant with a saved panel price keeps
        // the panel price as its stored price, so the price on screen is the price that goes. The price read here is what the
        // marketplace holds: it is kept as the known value (LastSent* without a code), so the difference goes with the next
        // stock send and the send log shows it as the previous price. Özel Fiyat on: the offer takes the marketplace price.
        var panel = offer?.FollowsPanelPrice == false ? null : await SavedPanelPriceAsync(tenantId, variant, importPlatformCode, cancellationToken);
        if (offer is null)
        {
            if (vatUnknown) await RecordOfferVatUnknownAsync(tenantId, connectionId, cancellationToken);
            offer = new ChannelOffer
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                VariantId = variant.Id,
                ListPrice = panel?.ListPrice ?? listPrice,
                SalePrice = panel?.SalePrice ?? salePrice,
                Currency = currency,
                VatRate = vatRate,
                VatInclusion = "INCLUDED",
                RoundingMode = "HALF_EVEN",
                SafetyStock = decimal.Round(Math.Max(0m, inventoryPolicy?.DefaultSafetyStock ?? 0m), 4, MidpointRounding.ToEven),
                Status = status,
                PriceVersion = 1,
                Version = 1
            };
            // The price came from the marketplace, so it counts as already sent there (at the panel price, the difference
            // rides with the next stock send: the marketplace's value is the known one).
            // B-6: the mark of this platform's own send (N11, Pazarama and PTT AVM hash other values than Trendyol's format).
            offer.LastPriceHash = PriceInventoryComposer.CurrentPriceHash(importPlatformCode ?? "", offer);
            if (panel is not null) (offer.LastSentSalePrice, offer.LastSentListPrice) = (salePrice, listPrice);
            db.ChannelOffers.Add(offer);
            run.PriceChangedVariantIds.Add(variant.Id);
            db.ChannelPriceHistory.Add(new ChannelPriceHistory
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OfferId = offer.Id,
                PriceVersion = offer.PriceVersion,
                ListPrice = offer.ListPrice,
                SalePrice = offer.SalePrice,
                Currency = offer.Currency,
                Reason = $"{observationSource}_CATALOG_IMPORT",
                ActorSource = $"SYSTEM:{observationSource}",
                EffectiveAt = now
            });
            return;
        }

        // The marketplace reports VAT-inclusive prices; an offer kept without VAT stores the net
        // amount that grosses up to the same price again.
        if (panel is { } panelPrice)
        {
            // The marketplace's (gross) price is the known one; the stored price stays (or becomes) the panel price.
            (offer.LastSentSalePrice, offer.LastSentListPrice) = (salePrice, listPrice);
            salePrice = ChannelOfferPricePolicy.StoredFromGross(panelPrice.SalePrice, vatRate, offer.VatInclusion, offer.RoundingMode);
            listPrice = Math.Max(salePrice, ChannelOfferPricePolicy.StoredFromGross(panelPrice.ListPrice, vatRate, offer.VatInclusion, offer.RoundingMode));
        }
        else
        {
            salePrice = ChannelOfferPricePolicy.StoredFromGross(salePrice, vatRate, offer.VatInclusion, offer.RoundingMode);
            listPrice = Math.Max(salePrice, ChannelOfferPricePolicy.StoredFromGross(listPrice, vatRate, offer.VatInclusion, offer.RoundingMode));
        }
        var priceChanged = offer.ListPrice != listPrice || offer.SalePrice != salePrice || !string.Equals(offer.Currency, currency, StringComparison.OrdinalIgnoreCase) || offer.VatRate != vatRate;
        var statusChanged = offer.Status != status;
        if (!priceChanged && !statusChanged) return;
        offer.ListPrice = listPrice;
        offer.SalePrice = salePrice;
        offer.Currency = currency;
        offer.VatRate = vatRate;
        offer.Status = status;
        offer.Version++;
        if (priceChanged)
        {
            run.PriceChangedVariantIds.Add(variant.Id);
            offer.PriceVersion++;
            // The new price is the one the marketplace already holds; without this marker the next
            // price-and-stock send would push it back as a local change.
            offer.LastPriceHash = PriceInventoryComposer.CurrentPriceHash(importPlatformCode ?? "", offer);
            db.ChannelPriceHistory.Add(new ChannelPriceHistory
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OfferId = offer.Id,
                PriceVersion = offer.PriceVersion,
                ListPrice = offer.ListPrice,
                SalePrice = offer.SalePrice,
                Currency = offer.Currency,
                Reason = $"{observationSource}_CATALOG_IMPORT",
                ActorSource = $"SYSTEM:{observationSource}",
                EffectiveAt = now
            });
        }
    }

    // The source a catalog import writes on the price history (Reason "<source>_CATALOG_IMPORT", ActorSource
    // "SYSTEM:<source>"): the connection's marketplace (ISLEYICI-3, 15 Nov 2026). Before, every observe-only import
    // (Hepsiburada, N11, Pazarama, PTT AVM too) was written as SHOPIFY; the old guess stays only for a connection not found.
    internal static string CatalogImportSource(string? platformCode, bool observeOnly) =>
        string.IsNullOrWhiteSpace(platformCode) ? observeOnly ? "SHOPIFY" : "TRENDYOL" : platformCode.Trim().ToUpperInvariant();

    // The VAT a catalog import gives an offer (ISLEYICI-2, 15 Nov 2026). A marketplace that does not report it (Shopify
    // never does; a Hepsiburada listing without vatRate) leaves an existing offer's VAT as it was, and a new offer takes the
    // card's VAT; 0 only when neither is known, and Unknown says so. Before, a missing VAT was written as 0.
    internal static (decimal Rate, bool Unknown) CatalogOfferVatRate(decimal? remote, decimal? offer, decimal? card)
    {
        if (remote is { } remoteRate) return (decimal.Round(Math.Max(0m, remoteRate), 4, MidpointRounding.ToEven), false);
        if (offer is { } offerRate) return (offerRate, false);
        if (card is { } cardRate) return (decimal.Round(Math.Max(0m, cardRate), 4, MidpointRounding.ToEven), false);
        return (0m, true);
    }

    private Task RecordOfferVatUnknownAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        RecordIssue(tenantId, $"offer-vat-unknown:{connectionId:N}", "OFFER_VAT_UNKNOWN",
            "Pazar yeri ürün çekiminde KDV oranı bildirmedi ve ürünün modelinde de KDV yok; yeni açılan tekliflerin KDV oranı %0 yazıldı. Modelin KDV oranını girin ve tekliflerin KDV oranını kontrol edin.", cancellationToken);

    // The VAT an offer of a card takes when the marketplace reports none (review, 17 Nov 2026): the card's as every reader
    // reads it (EffectiveCatalog: its model's), and for a new colour card this import has not saved yet (whose copy is
    // still empty, the model's VAT reaching it only at the save, ModelMirror) its brand's model of its code (a model is its
    // brand and its code; a card without a brand yet: the code's one branded model, as the reconcile links it). Before, such a colour
    // of a model with a VAT got a %0 offer and an OFFER_VAT_UNKNOWN warning.
    // The panel prices the product's variants took (Fiyat ve Stokları Güncelle) reach the offers without Özel Fiyat on the
    // other connections in one query: their previous price stays known, their send marks move, nothing is queued.
    private async Task FlushPanelRepricesAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (run.PendingPanelReprices.Count == 0) return;
        run.TenantPlatforms ??= await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.Id, x => x.PlatformCode, cancellationToken);
        // A store with no other connection has no other offer to pull: no query.
        if (!run.TenantPlatforms.Keys.Any(id => id != connectionId)) { run.PendingPanelReprices.Clear(); return; }
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        var source = CatalogImportSource(platformCode, false);
        await PanelPriceOffers.RepriceFollowersAsync(db, tenantId, new Dictionary<Guid, PanelPriceOffers.PanelPriceChange>(run.PendingPanelReprices), new HashSet<Guid>(),
            $"{source}_CATALOG_IMPORT", $"SYSTEM:{source}", now, cancellationToken, keepSendMarks: true, knownPlatforms: run.TenantPlatforms, exceptConnectionId: connectionId);
        run.PendingPanelReprices.Clear();
    }

    // The saved panel price of a variant (its own, else its card's) as the platform takes it; null without one.
    private async Task<(decimal ListPrice, decimal SalePrice)?> SavedPanelPriceAsync(Guid tenantId, ProductVariant variant, string? platformCode, CancellationToken cancellationToken)
    {
        if (variant.DefaultSalePrice is > 0) return PanelPriceOfferPolicy.PanelPrices(platformCode, variant.DefaultSalePrice, null, variant.DefaultListPrice, null);
        var card = db.Products.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Id == variant.ProductId) is { } local ? (local.DefaultSalePrice, local.DefaultListPrice)
            : await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == variant.ProductId).Select(x => new ValueTuple<decimal?, decimal?>(x.DefaultSalePrice, x.DefaultListPrice)).FirstOrDefaultAsync(cancellationToken);
        return PanelPriceOfferPolicy.PanelPrices(platformCode, variant.DefaultSalePrice, card.Item1, variant.DefaultListPrice, card.Item2);
    }

    private async Task<decimal?> CardOrModelVatRateAsync(Guid tenantId, Guid productId, string? modelCode, CancellationToken cancellationToken)
    {
        var local = db.Products.Local.FirstOrDefault(x => x.Id == productId);
        // KATALOG-14 phase D5 B2: only a card this import is adding has a VAT of its own yet (the listing's); a stored card's
        // VAT is its model's (EffectiveCatalog below), never its copy column.
        if (local is not null && db.Entry(local).State == EntityState.Added && local.VatRate is { } localRate) return localRate;
        if (await EffectiveCatalog.Products(db).Where(x => x.TenantId == tenantId && x.Id == productId).Select(x => x.VatRate).SingleOrDefaultAsync(cancellationToken) is { } stored) return stored;
        if (ProductFamilyKeyPolicy.ModelCodeKey(modelCode) is not { } key) return null;
        var brandId = local is not null ? local.BrandId : await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == productId).Select(x => x.BrandId).SingleOrDefaultAsync(cancellationToken);
        var models = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelCodeKey == key).Select(x => new { x.BrandId, x.VatRate }).ToListAsync(cancellationToken);
        var branded = models.Where(x => x.BrandId is not null).ToList();
        return (models.FirstOrDefault(x => x.BrandId == brandId) ?? (brandId is null && branded.Count == 1 ? branded[0] : null))?.VatRate;
    }

    // The VAT of an offer the raw import / mapping path opens (SeedChannelOfferAsync): the same rule as the catalog import
    // (CatalogOfferVatRate) — the marketplace's value, else the product card's VAT, else 0 with the single
    // OFFER_VAT_UNKNOWN issue of the connection (ISLEYICI-2).
    internal async Task<decimal> NewImportedOfferVatRateAsync(Guid tenantId, Guid connectionId, Guid variantId, decimal? remoteVatRate, CancellationToken cancellationToken)
    {
        decimal? cardVatRate = null;
        if (remoteVatRate is null)
        {
            var local = db.ProductVariants.Local.FirstOrDefault(x => x.Id == variantId);
            var card = local is not null ? new { local.ProductId, local.ModelCode }
                : await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == variantId).Select(x => new { x.ProductId, x.ModelCode }).SingleOrDefaultAsync(cancellationToken);
            if (card is not null) cardVatRate = await CardOrModelVatRateAsync(tenantId, card.ProductId, card.ModelCode, cancellationToken);
        }
        var (vatRate, vatUnknown) = CatalogOfferVatRate(remoteVatRate, null, cardVatRate);
        if (vatUnknown) await RecordOfferVatUnknownAsync(tenantId, connectionId, cancellationToken);
        return vatRate;
    }

    // When the marketplace stock being written was read: the earliest page read of the model group being finalized,
    // else the write itself. An earlier moment is the safe side (more panel movements count as after the read).
    private DateTimeOffset MarketplaceStockReadAt(DateTimeOffset now) =>
        run.CatalogStockReadAt is { } readAt && readAt < now ? readAt.ToUniversalTime() : now;

    private async Task<decimal> MarketplaceStockTargetAsync(Guid tenantId, InventoryItem inventory, decimal remoteQuantity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var readAt = MarketplaceStockReadAt(now);
        var orderReserved = await db.StockReservations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.InventoryItemId == inventory.Id && x.SourceType == "ORDER_LINE" && x.Status == ReservationStatus.Active)
            .SumAsync(x => (decimal?)x.Quantity, cancellationToken) ?? 0m;
        var afterRead = await db.StockLedgerEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.InventoryItemId == inventory.Id && x.RecordedAt > readAt
                && (MarketplaceStockImportPolicy.OnHandMovementTypes.Contains(x.MovementType) || MarketplaceStockImportPolicy.ReservationMovementTypes.Contains(x.MovementType)))
            .GroupBy(x => MarketplaceStockImportPolicy.ReservationMovementTypes.Contains(x.MovementType))
            .Select(group => new { Reservation = group.Key, Delta = group.Sum(x => x.QuantityDelta) })
            .ToListAsync(cancellationToken);
        return MarketplaceStockImportPolicy.TargetOnHand(
            remoteQuantity,
            orderReserved,
            afterRead.Where(x => x.Reservation).Sum(x => x.Delta),
            afterRead.Where(x => !x.Reservation).Sum(x => x.Delta));
    }

    private async Task UpsertShopifyInventoryObservations(Guid tenantId, Guid connectionId, Guid variantId, RemoteCatalogVariant remote, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var levels = remote.InventoryLevels is { Count: > 0 }
            ? remote.InventoryLevels
            : remote.StockQuantity is decimal aggregate
                ? [new RemoteInventoryLevel("__aggregate__", null, aggregate, remote.RawJson)]
                : [];
        if (levels.Count == 0) return;

        var mappings = await db.ConnectionLocationMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE")
            .ToListAsync(cancellationToken);

        foreach (var level in levels)
        {
            var externalLocationId = Short(level.ExternalLocationId, 256);
            var mapping = mappings.FirstOrDefault(x => string.Equals(Short(x.ExternalLocationId, 256), externalLocationId, StringComparison.OrdinalIgnoreCase));
            var quantity = decimal.Round(Math.Max(0m, level.Quantity), 4, MidpointRounding.ToEven);
            var observation = db.ChannelInventoryObservations.Local.FirstOrDefault(x =>
                    x.TenantId == tenantId
                    && x.ConnectionId == connectionId
                    && x.VariantId == variantId
                    && string.Equals(x.ExternalLocationId, externalLocationId, StringComparison.Ordinal))
                ?? await db.ChannelInventoryObservations.SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId
                    && x.ConnectionId == connectionId
                    && x.VariantId == variantId
                    && x.ExternalLocationId == externalLocationId,
                    cancellationToken);

            if (observation is null)
            {
                db.ChannelInventoryObservations.Add(new ChannelInventoryObservation
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ConnectionId = connectionId,
                    VariantId = variantId,
                    LocationId = mapping?.LocationId,
                    ExternalLocationId = externalLocationId,
                    Quantity = quantity,
                    ObservedAt = observedAt,
                    Version = 1
                });
                continue;
            }

            if (observation.LocationId != mapping?.LocationId || observation.Quantity != quantity || observation.ObservedAt != observedAt)
            {
                observation.LocationId = mapping?.LocationId;
                observation.Quantity = quantity;
                observation.ObservedAt = observedAt;
                observation.Version++;
            }
        }
    }
}
