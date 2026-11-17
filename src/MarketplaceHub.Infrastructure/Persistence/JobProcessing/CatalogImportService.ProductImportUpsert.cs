using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

using CategoryAttributeContext = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.CategoryAttributeContext;
using LocalCategoryAttribute = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.LocalCategoryAttribute;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class CatalogImportService
{
    private async Task<bool> UpdateCatalogOptionsOnly(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId,
            cancellationToken);
        if (link is null) return false;

        var product = await db.Products.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == link.ProductId,
            cancellationToken);
        if (product is null) return false;

        var updates = new List<(ProductVariant Variant, IReadOnlyDictionary<string, string> Options, string Signature)>();
        var seenVariants = new HashSet<Guid>();
        foreach (var remote in snapshot.Variants)
        {
            var options = remote.Options
                .Where(pair => !IsWebColorOptionKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (options.Count == 0) continue;

            var externalVariantId = Short(remote.ExternalVariantId, 256);
            var variantLink = await db.MarketplaceVariantLinks.SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalVariantId,
                cancellationToken);
            ProductVariant? variant = null;
            if (variantLink is not null)
            {
                variant = await db.ProductVariants.SingleOrDefaultAsync(
                    x => x.TenantId == tenantId && x.ProductId == product.Id && x.Id == variantLink.VariantId,
                    cancellationToken);
            }
            else
            {
                var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
                var skuKeys = CatalogLookupKeys(sku);
                var barcodeKeys = CatalogLookupKeys(remote.Barcode);
                var candidates = await db.ProductVariants
                    .Where(x => x.TenantId == tenantId && x.ProductId == product.Id
                        && (skuKeys.Contains(x.SkuNormalized)
                            || (x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized))))
                    .OrderBy(x => x.Id).Take(2)
                    .ToListAsync(cancellationToken);
                if (candidates.Count == 1) variant = candidates[0];
            }

            if (variant is null || !seenVariants.Add(variant.Id)) return false;
            updates.Add((variant, options, OptionSignature(options)));
        }

        // Avoid partially rewriting a product when even one remotely described
        // variant cannot be matched safely to an existing local variant, or a
        // legacy Web Color cleanup would leave a local-only variant uncovered.
        var optionVariants = snapshot.Variants.Count(remote => remote.Options.Any(pair => !IsWebColorOptionKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)));
        var localVariantCount = await db.ProductVariants.CountAsync(
            x => x.TenantId == tenantId && x.ProductId == product.Id,
            cancellationToken);
        if (updates.Count == 0 || updates.Count != optionVariants || updates.Count != localVariantCount) return false;

        await NormalizeLegacyWebColorOptions(tenantId, product.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var (variant, options, signature) in updates)
        {
            if (!string.Equals(variant.OptionSignature, signature, StringComparison.Ordinal))
            {
                variant.OptionSignature = signature;
                variant.UpdatedAt = now;
                variant.Version++;
                run.UpdatedCount++;
            }
            await UpsertCatalogOptions(tenantId, connectionId, product.Id, variant.Id, options, null, cancellationToken);
        }

        return true;
    }

    private async Task<bool> UpsertCatalogProduct(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, CategoryAttributeContext? categoryContext, ConnectionInventoryPolicy? inventoryPolicy, CancellationToken cancellationToken, bool saveChanges = true, bool onlyNewVariants = false, bool observeOnly = false, bool preferBarcode = false, bool onlyExistingVariants = false, bool updateExistingProducts = true, bool overwriteLocalChanges = false, bool updatePrices = false, bool updateStock = false, bool isHepsiburada = false, bool matchBySku = false, string? observedPlatformName = null, DateTimeOffset? createdAt = null, bool allowNewProduct = true, IReadOnlySet<string>? onlyAddExternalVariantIds = null, bool brandMappedByExternalId = false)
    {
        var now = timeProvider.GetUtcNow();
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        // New sizes added to a card the panel already links ("Yeni Ürünleri Aktar"): nothing of the card changes.
        var addOnly = onlyNewVariants && !observeOnly;
        // The listing's brand is mapped like its category (owner decision 12 Nov 2026, ImportBrandId): the mapping on
        // this connection, else the panel brand of the same name (mapped now), else a new panel brand (mapped now).
        // An observe-only read (Hepsiburada, Shopify, n11, Pazarama, PTT AVM) only gives a brand to a product it opens,
        // so its brand is settled where the product is written; a run that adds sizes to a linked card only reads it.
        var brandPlatformName = observedPlatformName ?? (isHepsiburada ? "Hepsiburada" : matchBySku ? "N11" : observeOnly ? "Shopify" : "Trendyol");
        var mappedBrandId = observeOnly ? null : await ImportBrandId(tenantId, connectionId, snapshot, brandPlatformName, brandMappedByExternalId, create: !addOnly, cancellationToken);
        // Keep the import contract version in the hash. This invalidates older
        // snapshots after changing option-role or brand-mapping interpretation,
        // so catalog records receive the corrected local relationships on the
        // next scan. The mapped brand is part of it: a brand the user maps later
        // reaches the products on the next scan although the listing is unchanged.
        var remoteHash = observeOnly
            ? Hash(JsonSerializer.Serialize(new
            {
                Snapshot = snapshot,
                OptionRoleVersion = "catalog-options-v10-shopify-variant-first-match"
            }))
            : Hash(JsonSerializer.Serialize(new
            {
                Snapshot = snapshot,
                // Reprocess existing catalog products after changing mapped
                // attribute assignment semantics, not only newly fetched rows.
                OptionRoleVersion = "catalog-options-v9-web-color-adapter",
                BrandRule = $"user-mapping-v1:{mappedBrandId:D}"
            }));
        var isNewProduct = false;
        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        Product? product = link is null
            ? null
            : await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == link.ProductId, cancellationToken);
        if (observeOnly)
        {
            // Shopify has no main-product/model-code identity for this
            // integration. Resolve local products from their variants first,
            // even when an older product link already exists. A Shopify product
            // may contain one model's colours while Ravencia stores each colour
            // as a separate Product row, so existing-only sync may need to link
            // variants across more than one local product.
            var allowCrossProductVariantMatch = preferBarcode && onlyExistingVariants;
            var observedPlatform = observedPlatformName ?? (matchBySku ? "N11" : "Shopify");
            var identityCodes = CatalogKeyNormalizer.LookupKeys(snapshot.Variants.Select(variant => variant.Barcode))
                .ToHashSet(StringComparer.Ordinal);
            // n11 keys every listing by the seller stock code, which is the
            // panel SKU; the barcode is only the fallback there.
            var skuCodes = matchBySku
                ? CatalogKeyNormalizer.LookupKeys(snapshot.Variants.Select(variant => variant.Sku)).ToHashSet(StringComparer.Ordinal)
                : [];
            if (identityCodes.Count > 0 || skuCodes.Count > 0)
            {
                var identityProductIds = skuCodes.Count == 0
                    ? []
                    : await db.ProductVariants.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && skuCodes.Contains(x.SkuNormalized))
                        .Select(x => x.ProductId)
                        .Distinct()
                        .OrderBy(id => id).Take(2)
                        .ToListAsync(cancellationToken);
                if (identityProductIds.Count == 0 && identityCodes.Count > 0)
                    identityProductIds = await db.ProductVariants.AsNoTracking()
                        .Where(x => x.TenantId == tenantId
                            && x.BarcodeNormalized != null
                            && identityCodes.Contains(x.BarcodeNormalized))
                        .Select(x => x.ProductId)
                        .Distinct()
                        .OrderBy(id => id).Take(2)
                        .ToListAsync(cancellationToken);
                if (identityProductIds.Count > 1)
                {
                    if (!allowCrossProductVariantMatch)
                    {
                        await RecordIssue(tenantId, $"product-sync-barcode-conflict:{connectionId}:{(skuCodes.Count > 0 ? skuCodes.First() : identityCodes.First())}", "PRODUCT_BARCODE_CONFLICT", $"{observedPlatform} ürününün stok kodu veya barkodu birden fazla yerel üründe bulundu; otomatik eşleştirme yapılmadı.", cancellationToken);
                        return false;
                    }

                    // Keep the existing product link stable when one exists;
                    // individual variants are resolved globally by barcode
                    // below and can therefore belong to sibling colour rows.
                    var representativeProductId = link?.ProductId ?? identityProductIds[0];
                    product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == representativeProductId, cancellationToken);
                }
                if (identityProductIds.Count == 1)
                {
                    var variantMatchedProduct = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == identityProductIds[0], cancellationToken);
                    if (variantMatchedProduct is not null && link is not null && link.ProductId != variantMatchedProduct.Id)
                    {
                        await RecordIssue(tenantId, $"product-sync-link-conflict:{connectionId}:{externalProductId}", "PRODUCT_LINK_CONFLICT", $"{observedPlatform} ürünü aynı bağlantıda başka bir dış ürünle eşleşmiş yerel ürüne bağlanmadı.", cancellationToken);
                        return false;
                    }
                    if (variantMatchedProduct is not null && await db.MarketplaceProductLinks.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == variantMatchedProduct.Id && x.ExternalId != externalProductId, cancellationToken))
                    {
                        await RecordIssue(tenantId, $"product-sync-link-conflict:{connectionId}:{externalProductId}", "PRODUCT_LINK_CONFLICT", $"{observedPlatform} varyantı aynı bağlantıda başka bir dış ürünle eşleşmiş yerel ürüne bağlanmadı.", cancellationToken);
                        return false;
                    }
                    product = variantMatchedProduct;
                }
            }
            // Hepsiburada links to the panel's products the way the import always matched: by barcode, then the seller's
            // stock code, then the model code. A listing none of them finds opens no product (a Hepsiburada code is not
            // a panel product: the SIT test listings carry nothing else); it is reported as unmatched.
            if (isHepsiburada && product is null)
            {
                var stockCodes = CatalogKeyNormalizer.LookupKeys(snapshot.Variants.Select(variant => variant.Sku)).ToHashSet(StringComparer.Ordinal);
                var modelCodes = snapshot.Variants.Select(variant => variant.ModelCode?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
                var matchedProductIds = stockCodes.Count == 0 ? [] : await db.ProductVariants.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && stockCodes.Contains(x.SkuNormalized)).Select(x => x.ProductId).Distinct().OrderBy(id => id).Take(2).ToListAsync(cancellationToken);
                // A model code names every colour and size of the model: the product is the one whose variants carry the
                // listing's colours and sizes, never the only card that happens to hold the model code (Y5).
                var modelCodeUndecided = false;
                if (matchedProductIds.Count == 0 && modelCodes.Length > 0)
                {
                    var modelVariants = await db.ProductVariants.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.ModelCode != null && modelCodes.Contains(x.ModelCode))
                        .Select(x => new ModelCodeVariant(x.ProductId, x.ModelCode!, x.OptionSignature))
                        .ToListAsync(cancellationToken);
                    matchedProductIds = ModelCodeProducts(modelVariants, snapshot.Variants, snapshot.Title);
                    modelCodeUndecided = modelVariants.Count > 0 && matchedProductIds.Count == 0;
                }
                if (matchedProductIds.Count == 1
                    && !await db.MarketplaceProductLinks.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == matchedProductIds[0] && x.ExternalId != externalProductId, cancellationToken))
                    product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == matchedProductIds[0], cancellationToken);
                if (product is null)
                {
                    var remote = snapshot.Variants.FirstOrDefault();
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{Short(externalProductId, 256)}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Hepsiburada ürünü panelde eşleşmedi: barkod {remote?.Barcode ?? "yok"}, stok kodu {remote?.Sku ?? "yok"}, model kodu {remote?.ModelCode ?? "yok"} · {snapshot.Title}. {(matchedProductIds.Count > 1 ? "Birden fazla panel ürünü aday çıktı." : modelCodeUndecided ? "Model kodu panelde var ama hiçbir panel varyantının rengi ve bedeni bu ürününkiyle aynı değil; ürün seçilmedi." : "Panelde aynı barkod, stok kodu veya model kodu yok; Hepsiburada ürünü panelde yeni ürün olarak açılmaz.")}", 1000), cancellationToken);
                    return false;
                }
            }
        }
        // A listing without a product link of its own can still be a panel product's: Trendyol lists each colour as
        // its own content, while a product published from the panel keeps its colours in one product and links only
        // its variants. Its variants find it; the listing then brings only its stock and prices (the panel's content
        // stays), and no second, empty product is opened beside it. A link that already leads to such an empty product
        // (one an earlier import opened) gives way to the owner too, so that product is not filled in again.
        var linkedProductHollow = product is not null && !observeOnly && !await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.ProductId == product.Id, cancellationToken);
        if ((product is null || linkedProductHollow) && !observeOnly)
        {
            var owners = await CatalogProductsOwningVariants(tenantId, connectionId, snapshot, cancellationToken);
            if (owners.Count > 1)
            {
                await RecordIssue(tenantId, $"product-sync-variant-owner-conflict:{connectionId}:{Short(externalProductId, 200)}", "PRODUCT_VARIANT_CONFLICT", Short($"Pazar yeri ürününün varyantları panelde birden fazla ürüne ait; yeni ürün açılmadı, mevcut kayıtlar korundu · {snapshot.Title}", 1000), cancellationToken);
                return false;
            }
            if (owners.Count == 1 && await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == owners[0], cancellationToken) is { } owner)
            {
                await SyncCatalogInventoryForPreservedProduct(tenantId, connectionId, owner, snapshot, inventoryPolicy, now, cancellationToken, updatePrices: updatePrices, updateStock: updateStock);
                if (saveChanges)
                    await db.SaveChangesAsync(cancellationToken);
                run.SkippedCount++;
                return updatePrices || updateStock;
            }
        }
        // "Mevcut Ürünleri Güncelle" never opens a product, and no mode opens one without a variant it can keep.
        if (product is null && (!allowNewProduct || !snapshot.Variants.Any(variant => !string.IsNullOrWhiteSpace(NormalizeCatalogKey(variant.Sku ?? variant.Barcode ?? variant.ExternalVariantId, 160)))))
        {
            run.SkippedCount++;
            return false;
        }
        // KATALOG-14 phase D5b (owner, 17 Nov 2026): a model is its code alone and the brand one of its fields, so a listing
        // of another brand under a code the panel holds opens no card: it is skipped and PRODUCT_MODEL_AMBIGUOUS says so.
        if (product is null && await OtherBrandModelAsync(tenantId, snapshot, observeOnly ? await ImportBrandId(tenantId, connectionId, snapshot, brandPlatformName, brandMappedByExternalId, create: false, cancellationToken) : mappedBrandId, cancellationToken) is { } otherBrand)
        {
            await RecordIssue(tenantId, $"product-model-other-brand:{connectionId:N}:{Short(otherBrand.Key, 160)}", ProductModelLinks.AmbiguousCode,
                Short($"{ModelCodeUniquenessPolicy.OtherBrandMessage(otherBrand.Code, otherBrand.Brand)}: {brandPlatformName} ürünü ({snapshot.BrandName ?? "markasız"} · {snapshot.Title}) panele aktarılmadı, hiçbir şey birleştirilmedi. Doğru marka ya da model kodu hangisiyse pazaryerinde ya da panelde düzeltin.", 1000), cancellationToken);
            run.SkippedCount++;
            return false;
        }
        if (product is null)
        {
            isNewProduct = true;
            var listingVatRate = ListingVatRate(snapshot);
            product = new Product
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Title = ProductTitle(snapshot.Title, externalProductId),
                Description = snapshot.Description ?? "",
                // The card's VAT is the listing's own (the rate its offers below are priced with); a listing that
                // carries none leaves it unknown.
                VatRate = listingVatRate,
                VatInclusion = listingVatRate is null ? null : "INCLUDED",
                // A marketplace listing carries no panel warranty: the new card starts at 0 (no warranty), as the
                // editor's new product form does. An existing card's warranty is never touched by an import.
                WarrantyMonths = 0,
                Status = ProductStatus.Active,
                CreatedAt = createdAt ?? now,
                UpdatedAt = now,
                Version = 1
            };
            db.Products.Add(product);
            run.InsertedCount++;
            if (link is null)
            {
                link = new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ProductId = product.Id, ExternalId = externalProductId, LastImportedPayloadHash = remoteHash, SyncStatus = "SYNCED", Version = 1 };
                db.MarketplaceProductLinks.Add(link);
                run.InsertedCount++;
            }
        }
        else if (link is null && observeOnly)
        {
            link = new MarketplaceProductLink
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                ProductId = product.Id,
                ExternalId = externalProductId,
                LastImportedPayloadHash = remoteHash,
                SyncStatus = "SYNCED",
                Version = 1
            };
            db.MarketplaceProductLinks.Add(link);
            run.InsertedCount++;
        }

        var expectedVariantExternalIds = snapshot.Variants
            .Select(variant => Short(variant.ExternalVariantId, 256))
            .Where(externalId => !string.IsNullOrWhiteSpace(externalId))
            .Select(MarketplaceVariantLinkCoverage.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var linkedVariantExternalIds = expectedVariantExternalIds.Length == 0
            ? []
            : await (from variantLink in db.MarketplaceVariantLinks.AsNoTracking()
                     join variant in db.ProductVariants.AsNoTracking()
                         on new { variantLink.TenantId, variantLink.VariantId }
                         equals new { variant.TenantId, VariantId = variant.Id }
                     where variantLink.TenantId == tenantId
                         && variantLink.ConnectionId == connectionId
                         && variant.ProductId == product!.Id
                         && expectedVariantExternalIds.Contains(variantLink.ExternalId.Trim().ToUpper())
                     select variantLink.ExternalId.Trim().ToUpper()).ToListAsync(cancellationToken);
        var hasCompleteVariantLinks = MarketplaceVariantLinkCoverage.IsComplete(expectedVariantExternalIds, linkedVariantExternalIds);

        // Runs before the unchanged-payload and preserved-product paths, so a product imported before desi was read
        // (or edited locally since) still gets the marketplace desi on its empty variants.
        // Adding new sizes only ("Yeni Ürünleri Aktar") leaves the panel's variants as they are, an empty desi too.
        if (!isNewProduct && !addOnly)
            await FillMissingImportedDesi(tenantId, connectionId, snapshot, now, cancellationToken);

        // A catalog scan can revisit hundreds of products whose last imported
        // payload and local projection are already in sync. In that case the
        // inventory/media reconciliation below only repeats database reads;
        // the version marker is sufficient to prove that no local edit is
        // waiting to be preserved and that this exact remote payload was
        // already applied. Keep the legacy reconciliation path for older links
        // that do not have a trustworthy imported-product version yet.
        if (!onlyNewVariants
            && !isNewProduct
            && !updatePrices
            && !updateStock
            && link is not null
            && string.Equals(link.LastImportedPayloadHash, remoteHash, StringComparison.OrdinalIgnoreCase)
            && link.LastImportedProductVersion == product!.Version
            && string.Equals(link.SyncStatus, "SYNCED", StringComparison.OrdinalIgnoreCase)
            && hasCompleteVariantLinks)
        {
            await RepairMissingCatalogOptionAssignments(tenantId, connectionId, product.Id, snapshot, categoryContext, cancellationToken);
            if (saveChanges)
                await db.SaveChangesAsync(cancellationToken);
            run.SkippedCount++;
            return observeOnly;
        }

        if (!onlyNewVariants && !isNewProduct && link is not null && hasCompleteVariantLinks && string.Equals(link.LastImportedPayloadHash, remoteHash, StringComparison.OrdinalIgnoreCase) && await CatalogSnapshotAlreadyApplied(tenantId, product.Id, snapshot, cancellationToken))
        {
            // Repair missing option values/assignments even when the product payload
            // itself was already imported and no longer needs a full upsert.
            await RepairMissingCatalogOptionAssignments(tenantId, connectionId, product.Id, snapshot, categoryContext, cancellationToken);
            // A previous import may have stored the remote observation while
            // leaving the untouched local projection at zero. Reconcile that
            // legacy state even when the payload itself has not changed.
            await SyncCatalogInventoryForPreservedProduct(tenantId, connectionId, product, snapshot, inventoryPolicy, now, cancellationToken, observeOnly, preferBarcode, updatePrices, updateStock);
            if (saveChanges)
                await db.SaveChangesAsync(cancellationToken);
            run.SkippedCount++;
            // A Shopify read-only observation is still a successful match even
            // when the local product hash is unchanged. The local catalog is
            // intentionally preserved, but progress must report the linked
            // product as processed rather than silently skipped.
            return observeOnly;
        }

        var preserveDueToLocalChanges = link is not null && (!overwriteLocalChanges && ProductImportMergePolicy.PreserveLocalChanges(product.Version, link.LastImportedProductVersion, link.DirtyFieldsJson) || observeOnly && !isNewProduct);
        var preserveDueToImportOption = link is not null && !updateExistingProducts && !isNewProduct;
        var preserveLocal = preserveDueToLocalChanges || preserveDueToImportOption;
        if (!onlyNewVariants && !preserveLocal)
        {
            // A title or description the listing does not carry is unknown, not empty: the panel's stays.
            if (isNewProduct || !string.IsNullOrWhiteSpace(snapshot.Title)) product.Title = ProductTitle(snapshot.Title, externalProductId);
            if (isNewProduct || snapshot.Description is not null) product.Description = snapshot.Description ?? "";
            // The panel product's own status is the user's (report item 19): the import never activates or archives
            // it. What the marketplace says about the listing stays on its links (SaleState) and offers.
            product.UpdatedAt = now;
            product.Version++;
            run.UpdatedCount++;
        }
        else if (preserveLocal)
        {
            run.SkippedCount++;
            if (isHepsiburada)
                await RepairHepsiburadaVariantLinks(tenantId, connectionId, product, snapshot, cancellationToken);
        }

        // K13: a mapped panel category the user switched off or split into sub-categories cannot take
        // products, the same rule UpsertCatalogCategory applies; the product stays without a category and
        // the mapping is reported once per category.
        // An observe-only read (Pazarama, PttAVM, N11, Hepsiburada) gives a product it opens the panel category its
        // marketplace category is mapped to (the import made that mapping), as it gives it the brand; an existing
        // product's category is never touched (preserved above).
        var category = ObservedProductCategory(observeOnly, addOnly, isNewProduct, categoryContext?.LocalCategory)
            ?? (observeOnly || addOnly ? null : categoryContext?.LocalCategory ?? await UpsertCatalogCategory(tenantId, snapshot.CategoryName, cancellationToken));
        if (category is { } mappedCategory && categoryContext is not null && (!mappedCategory.IsActive || !mappedCategory.IsLeaf))
        {
            await RecordIssue(tenantId, $"category-mapping-unusable:{connectionId:D}:{mappedCategory.Id:D}", "CATEGORY_MAPPING_UNUSABLE", $"Pazaryeri kategorisinin eşlendiği \"{Short(mappedCategory.Name, 120)}\" kategorisi {(mappedCategory.IsActive ? "alt kategorilere bölünmüş" : "kapalı")}; ürünler kategorisiz aktarıldı. Eşleştirmeyi etkin bir yaprak kategoriye çevirin.", cancellationToken);
            category = null;
        }
        if (!preserveLocal && !onlyNewVariants)
        {
            // An observe-only read writes only a product it opens (an existing one is preserved above), so its brand
            // is mapped here, for that product alone.
            if (observeOnly && isNewProduct)
                mappedBrandId = await ImportBrandId(tenantId, connectionId, snapshot, brandPlatformName, brandMappedByExternalId, create: true, cancellationToken);
            product.BrandId = ImportedBrandRule.NextProductBrand(isNewProduct, product.BrandId, mappedBrandId);
            product.CategoryId = category?.Id;
        }

        var importedNewVariantCount = 0;
        if (!preserveLocal || onlyNewVariants || observeOnly)
        {
            if (!onlyNewVariants)
                await NormalizeLegacyWebColorOptions(tenantId, product.Id, cancellationToken);
            var existingVariantExternalIds = onlyNewVariants
                ? (await db.MarketplaceVariantLinks.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
                        .Select(x => x.ExternalId)
                        .ToListAsync(cancellationToken))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var importedVariants = new List<ProductVariant>(snapshot.Variants.Count);
            foreach (var (remote, sortOrder) in snapshot.Variants.Select((remote, index) => (remote, index)))
            {
                var externalVariantId = Short(remote.ExternalVariantId, 256);
                if (onlyNewVariants && existingVariantExternalIds!.Contains(externalVariantId))
                    continue;
                // A variant the panel holds under another code (stock code or barcode, no link yet) is not new: it is
                // neither linked nor rewritten here ("Ürün Eşleme" links it).
                if (addOnly && onlyAddExternalVariantIds is not null && !onlyAddExternalVariantIds.Contains(externalVariantId))
                    continue;
                var importedVariant = await UpsertCatalogVariant(tenantId, connectionId, product, remote, sortOrder, categoryContext, inventoryPolicy, now, cancellationToken, observeOnly, preferBarcode, onlyExistingVariants, updatePrices, updateStock);
                if (importedVariant is not null)
                {
                    importedVariants.Add(importedVariant);
                    if (onlyNewVariants) importedNewVariantCount++;
                    existingVariantExternalIds?.Add(externalVariantId);
                }
            }
            // The new sizes a plain pull reported on this card are in the panel now.
            if (!observeOnly && !isNewProduct && importedVariants.Any(variant => db.Entry(variant).State == EntityState.Added))
                await ResolveIssue(tenantId, $"product-new-sizes:{connectionId:D}:{Short(externalProductId, 200)}", cancellationToken);
            // Whatever the import options, a product is never opened without a variant of its own: when every variant
            // turned out to be another panel product's, the new product, its link and options are dropped again.
            if (isNewProduct && importedVariants.Count == 0)
            {
                DropNewCatalogProduct(db, product);
                run.InsertedCount = Math.Max(0, run.InsertedCount - 2);
                run.SkippedCount++;
                await RecordIssue(tenantId, $"product-sync-no-variant:{connectionId}:{Short(externalProductId, 200)}", "PRODUCT_VARIANT_CONFLICT", Short($"Pazar yeri ürünü panelde açılmadı: varyantlarının hiçbiri eklenemedi (stok kodları başka panel ürünlerinde) · {snapshot.Title}", 1000), cancellationToken);
                if (saveChanges)
                    await db.SaveChangesAsync(cancellationToken);
                return false;
            }
            // "Ekli Olmayan Ürünler" brings what the panel lacks and never rewrites a card's own fields: the attributes
            // common to the new sizes alone are not the card's.
            if (categoryContext is not null && importedVariants.Count > 0 && !onlyNewVariants)
                await PromoteCommonImportedAttributes(tenantId, product, importedVariants, categoryContext, cancellationToken);

            if (!onlyNewVariants)
            {
                var productImageUrls = CatalogImageIdentity.DistinctUrls(
                    snapshot.ImageUrls.Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? [])));
                // A listing read without images says nothing about them: the panel's gallery stays.
                if (productImageUrls.Count > 0 || isNewProduct)
                    await UpsertCatalogMedia(tenantId, product, null, productImageUrls, product.Title, cancellationToken);
            }
        }
        else
        {
            // Local product edits must not block stock observations. Match
            // existing variants without touching their local content/options,
            // then apply the inventory part of the remote snapshot. Trendyol
            // media is a separate remote projection, so a manual product edit
            // must not leave an older Shopify gallery in place.
            await SyncCatalogInventoryForPreservedProduct(tenantId, connectionId, product, snapshot, inventoryPolicy, now, cancellationToken, observeOnly, preferBarcode, updatePrices, updateStock, syncMedia: !observeOnly && updateExistingProducts);
            // A plain pull keeps the card whole, a size Trendyol added since included (the owner's rule, 12 Nov 2026: only
            // "Ekli Olmayan Ürünler" or "Mevcut Ürün Bilgilerini Güncelle" add it). The owner is told it is there.
            if (!observeOnly)
            {
                var unadded = (await RemoteVariantsMissingFromPanel(tenantId, connectionId, snapshot, cancellationToken)).Count;
                var issueKey = $"product-new-sizes:{connectionId:D}:{Short(externalProductId, 200)}";
                if (unadded > 0)
                {
                    run.ProductUnaddedSizeCount = unadded;
                    run.UnaddedSizeCount += unadded;
                    await RecordIssue(tenantId, issueKey, "PRODUCT_NEW_SIZES_NOT_IMPORTED", Short($"{product.Title}: Trendyol'da panelde olmayan {UnaddedSizesNote(unadded)}", 1000), cancellationToken);
                }
                else
                    await ResolveIssue(tenantId, issueKey, cancellationToken);
            }
            if (!observeOnly && updateExistingProducts && snapshot.ImageUrls.Count > 0)
            {
                var productImageUrls = CatalogImageIdentity.DistinctUrls(snapshot.ImageUrls);
                await UpsertCatalogMedia(tenantId, product, null, productImageUrls, product.Title, cancellationToken);
            }
        }
        // Adding new sizes to a card the panel holds leaves its link as it was: the card's content was not taken, so the
        // payload hash, the import marks and the local-change flags must not say it was (a later pull would otherwise
        // skip the content as already applied).
        if (addOnly)
        {
            run.ProductImportedNewVariantCount = importedNewVariantCount;
            if (saveChanges)
                await db.SaveChangesAsync(cancellationToken);
            return importedNewVariantCount > 0;
        }
        link ??= await db.MarketplaceProductLinks.SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        link.LastImportedPayloadHash = remoteHash;
        link.LastImportedAt = now;
        if (preserveDueToLocalChanges && !onlyNewVariants)
        {
            link.SyncStatus = "LOCAL_CHANGES_PENDING";
            link.DirtyFieldsJson ??= "[\"product\"]";
        }
        else if (!preserveLocal)
        {
            link.LastImportedProductVersion = product.Version;
            link.SyncStatus = "SYNCED";
            link.DirtyFieldsJson = null;
            link.LastError = null;
        }
        link.Version++;
        if (saveChanges)
            await db.SaveChangesAsync(cancellationToken);
        return onlyNewVariants
            ? importedNewVariantCount > 0
            : observeOnly
                ? !isNewProduct
                : !preserveLocal;
    }

    // Takes back a product this import added and has not saved yet, with every row added for it (its link, options,
    // media). Shared rows the import may have added beside it (a category, a brand another row uses) stay; a brand added
    // for this product alone (an observe-only read opens a brand only for a product it opens) goes with it.
    // The mapped panel category an observe-only read gives a product it opens; null otherwise (an existing product, a
    // plain or add-only read, which choose their category themselves, or no mapped category).
    internal static Category? ObservedProductCategory(bool observeOnly, bool addOnly, bool isNewProduct, Category? mapped) =>
        observeOnly && !addOnly && isNewProduct ? mapped : null;

    internal static void DropNewCatalogProduct(AppDbContext db, Product product)
    {
        var brandId = product.BrandId;
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added).ToList())
        {
            if (ReferenceEquals(entry.Entity, product)
                || entry.Metadata.FindProperty("ProductId") is { } productIdProperty && entry.Property(productIdProperty.Name).CurrentValue is Guid owner && owner == product.Id)
                entry.State = EntityState.Detached;
        }
        if (brandId is not { } id) return;
        var brand = db.ChangeTracker.Entries<Brand>().FirstOrDefault(entry => entry.State == EntityState.Added && entry.Entity.Id == id);
        if (brand is null) return;
        var usedElsewhere = db.ChangeTracker.Entries().Any(entry => entry.State != EntityState.Detached && !ReferenceEquals(entry.Entity, brand.Entity)
            && entry.Metadata.FindProperty("BrandId") is { } brandProperty && entry.Property(brandProperty.Name).CurrentValue is Guid used && used == id);
        if (usedElsewhere) return;
        // The mapping the import wrote for the brand it opened goes with it.
        foreach (var mapping in db.ChangeTracker.Entries<BrandMapping>().Where(entry => entry.State == EntityState.Added && entry.Entity.LocalId == id).ToList())
            mapping.State = EntityState.Detached;
        brand.State = EntityState.Detached;
    }

    // The panel products a listing's variants already belong to: by this connection's variant link, then the stock
    // code, then the barcode. At most two, which is enough to tell one owner from a conflict.
    private async Task<List<Guid>> CatalogProductsOwningVariants(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var externalIds = snapshot.Variants.Select(variant => Short(variant.ExternalVariantId, 256)).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var owners = externalIds.Length == 0 ? [] : await (from variantLink in db.MarketplaceVariantLinks.AsNoTracking()
                                                           join variant in db.ProductVariants.AsNoTracking()
                                                               on new { variantLink.TenantId, variantLink.VariantId }
                                                               equals new { variant.TenantId, VariantId = variant.Id }
                                                           where variantLink.TenantId == tenantId && variantLink.ConnectionId == connectionId && externalIds.Contains(variantLink.ExternalId)
                                                           select variant.ProductId).Distinct().OrderBy(id => id).Take(2).ToListAsync(cancellationToken);
        if (owners.Count > 0) return owners;
        var skuKeys = snapshot.Variants.SelectMany(variant => CatalogLookupKeys(string.IsNullOrWhiteSpace(variant.Sku) ? variant.Barcode ?? variant.ExternalVariantId : variant.Sku)).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (skuKeys.Length > 0)
            owners = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && skuKeys.Contains(x.SkuNormalized)).Select(x => x.ProductId).Distinct().OrderBy(id => id).Take(2).ToListAsync(cancellationToken);
        if (owners.Count > 0) return owners;
        var barcodeKeys = snapshot.Variants.SelectMany(variant => CatalogLookupKeys(variant.Barcode)).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return barcodeKeys.Length == 0 ? [] : await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized)).Select(x => x.ProductId).Distinct().OrderBy(id => id).Take(2).ToListAsync(cancellationToken);
    }

    private async Task RepairHepsiburadaVariantLinks(Guid tenantId, Guid connectionId, Product product, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var localVariants = await db.ProductVariants
            .Where(variant => variant.TenantId == tenantId && variant.ProductId == product.Id)
            .ToListAsync(cancellationToken);
        var localVariantIds = localVariants.Select(variant => variant.Id).ToArray();
        var externalIds = snapshot.Variants
            .Select(variant => MarketplaceVariantLinkCoverage.Normalize(Short(variant.ExternalVariantId, 256)))
            .Where(externalId => externalId.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (localVariantIds.Length > 0 || externalIds.Length > 0)
        {
            var existingLinks = await db.MarketplaceVariantLinks
                .Where(link => link.TenantId == tenantId
                    && link.ConnectionId == connectionId
                    && (localVariantIds.Contains(link.VariantId)
                        || externalIds.Contains(link.ExternalId.Trim().ToUpper())))
                .ToListAsync(cancellationToken);
            var linkedExternalIds = existingLinks
                .Select(link => MarketplaceVariantLinkCoverage.Normalize(link.ExternalId))
                .ToHashSet(StringComparer.Ordinal);
            var linkedVariantIds = existingLinks.Select(link => link.VariantId).ToHashSet();

            foreach (var remote in snapshot.Variants)
            {
                var externalId = Short(remote.ExternalVariantId, 256);
                var externalKey = MarketplaceVariantLinkCoverage.Normalize(externalId);
                if (externalKey.Length == 0 || linkedExternalIds.Contains(externalKey)) continue;

                var skuKey = NormalizeCatalogKey(remote.Sku, 160);
                var externalSkuKey = NormalizeCatalogKey(externalId, 160);
                var barcodeKey = NormalizeCatalogKey(remote.Barcode, 160);
                var candidates = localVariants
                    .Where(variant =>
                        skuKey.Length > 0 && CatalogKeyNormalizer.SameKey(variant.SkuNormalized, skuKey)
                        || externalSkuKey.Length > 0 && CatalogKeyNormalizer.SameKey(variant.SkuNormalized, externalSkuKey)
                        || barcodeKey.Length > 0 && CatalogKeyNormalizer.SameKey(variant.BarcodeNormalized, barcodeKey))
                    .Select(variant => variant.Id)
                    .Distinct()
                    .ToArray();
                if (candidates.Length != 1 || linkedVariantIds.Contains(candidates[0])) continue;

                var link = new MarketplaceVariantLink
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ConnectionId = connectionId,
                    VariantId = candidates[0],
                    ExternalId = externalId,
                    Version = 1
                };
                ListingSaleStateReads.ApplyToNewLink(link, remote.SaleState, remote.SaleStateReason, timeProvider.GetUtcNow());
                db.MarketplaceVariantLinks.Add(link);
                existingLinks.Add(link);
                linkedExternalIds.Add(externalKey);
                linkedVariantIds.Add(link.VariantId);
                run.InsertedCount++;
            }
        }

        var hasUsableMedia = await (from media in db.ProductMedia.AsNoTracking()
                                    join asset in db.FileAssets.AsNoTracking()
                                        on new { media.TenantId, FileAssetId = media.FileAssetId }
                                        equals new { asset.TenantId, FileAssetId = asset.Id }
                                    where media.TenantId == tenantId
                                        && media.ProductId == product.Id
                                        && media.Status == "ACTIVE"
                                        && asset.Status == "ACTIVE"
                                        && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                                    select media.Id).AnyAsync(cancellationToken);
        if (!hasUsableMedia)
        {
            var imageUrls = CatalogImageIdentity.DistinctUrls(
                snapshot.ImageUrls.Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? [])));
            if (imageUrls.Count > 0)
                await UpsertCatalogMedia(tenantId, product, null, imageUrls, product.Title, cancellationToken);
        }
    }

    // The listing's variants the panel does not hold at all: no variant link of this connection names them and no panel
    // variant carries their stock code or barcode (a code known on any card is not a new size).
    private async Task<List<RemoteCatalogVariant>> RemoteVariantsMissingFromPanel(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var externalIds = snapshot.Variants.Select(variant => Short(variant.ExternalVariantId, 256)).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var linked = externalIds.Length == 0
            ? []
            : (await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalIds.Contains(x.ExternalId))
                .Select(x => x.ExternalId)
                .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<(RemoteCatalogVariant Remote, string Sku, string[] SkuKeys, string[] BarcodeKeys)>();
        foreach (var remote in snapshot.Variants)
        {
            var externalId = Short(remote.ExternalVariantId, 256);
            if (string.IsNullOrWhiteSpace(externalId) || linked.Contains(externalId)) continue;
            var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
            var skuKeys = CatalogLookupKeys(sku);
            var barcodeKeys = CatalogLookupKeys(remote.Barcode);
            if (skuKeys.Length == 0) continue;
            if (db.ProductVariants.Local.Any(x => x.TenantId == tenantId && (CatalogKeyNormalizer.SameKey(x.SkuNormalized, sku) || barcodeKeys.Length > 0 && CatalogKeyNormalizer.SameKey(x.BarcodeNormalized, remote.Barcode)))) continue;
            candidates.Add((remote, sku, skuKeys, barcodeKeys));
        }
        if (candidates.Count == 0) return [];
        // The stored keys of every candidate read in one query, not one query per variant: a variant is known when a panel
        // variant's stock code is one of its stock-code keys or that variant's barcode one of its barcode keys, as before.
        var allSkuKeys = candidates.SelectMany(x => x.SkuKeys).Distinct(StringComparer.Ordinal).ToArray();
        var allBarcodeKeys = candidates.SelectMany(x => x.BarcodeKeys).Distinct(StringComparer.Ordinal).ToArray();
        var known = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (allSkuKeys.Contains(x.SkuNormalized) || x.BarcodeNormalized != null && allBarcodeKeys.Contains(x.BarcodeNormalized)))
            .Select(x => new { x.SkuNormalized, x.BarcodeNormalized })
            .ToListAsync(cancellationToken);
        var knownSkus = known.Select(x => x.SkuNormalized).ToHashSet(StringComparer.Ordinal);
        var knownBarcodes = known.Where(x => x.BarcodeNormalized != null).Select(x => x.BarcodeNormalized!).ToHashSet(StringComparer.Ordinal);
        return candidates
            .Where(x => !x.SkuKeys.Any(knownSkus.Contains) && !x.BarcodeKeys.Any(knownBarcodes.Contains))
            .Select(x => x.Remote)
            .ToList();
    }

    // A size added to an existing card starts with the pictures the editor's add rule gives a new row (family-gallery-save
    // inheritAssignments): the pictures every existing size of its colour holds as its own list, joined with those every
    // existing row of its size holds across two other colours at least, in the card gallery's order, once each. Any row of
    // the colour without a list of its own leaves nothing to share, and nothing written means the card gallery applies.
    // The rows are the family's (brand + model code) as the database holds them before this run's new sizes.
    private async Task<List<Guid>> InheritedVariantPictureAssetIds(Guid tenantId, Product product, string optionSignature, CancellationToken cancellationToken)
    {
        var (colour, size) = ColourAndSize(optionSignature);
        if (colour is null && size is null) return [];
        var modelCode = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.ModelCode != null && x.ModelCode != "")
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.ModelCode)
            .FirstOrDefaultAsync(cancellationToken);
        var familyIds = await ProductFamilyScope.ProductIdsAsync(db, tenantId, product.BrandId, modelCode, db.ProductVariants.AsNoTracking(), cancellationToken, product.FamilyId);
        if (!familyIds.Contains(product.Id)) familyIds.Add(product.Id);
        var rows = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && familyIds.Contains(x.ProductId))
            .OrderBy(x => x.ProductId == product.Id ? 0 : 1).ThenBy(x => x.ProductId).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.OptionSignature })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return [];
        var rowIds = rows.Select(x => x.Id).ToArray();
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && item.Status == "ACTIVE"
                               && (item.VariantId != null && rowIds.Contains(item.VariantId.Value) || item.ProductId == product.Id && item.VariantId == null)
                           orderby item.SortOrder, item.Id
                           select new { item.VariantId, item.FileAssetId, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        string Key(Guid assetId, string classification, string path) => classification == "PRODUCT_MEDIA_URL" ? CatalogImageIdentity.Key(path) : assetId.ToString("D");
        var assetByKey = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in media) assetByKey.TryAdd(Key(item.FileAssetId, item.Classification, item.RelativePath), item.FileAssetId);
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in media.Where(x => x.VariantId is null)) rank.TryAdd(Key(item.FileAssetId, item.Classification, item.RelativePath), rank.Count);
        var listByVariant = media.Where(x => x.VariantId is not null).GroupBy(x => x.VariantId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(x => Key(x.FileAssetId, x.Classification, x.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var assignments = rows.Select(row => (Axes: ColourAndSize(row.OptionSignature), Refs: listByVariant.GetValueOrDefault(row.Id) ?? [])).ToList();

        static List<string> Common(IReadOnlyList<List<string>> lists) => lists.Count == 0
            ? []
            : [.. lists[0].Where(key => lists.Skip(1).All(other => other.Contains(key, StringComparer.OrdinalIgnoreCase)))];
        var byColour = colour is null ? [] : Common([.. assignments.Where(row => row.Axes.Colour == colour).Select(row => row.Refs)]);
        var sizeRows = size is null ? [] : assignments.Where(row => row.Axes.Size == size && row.Axes.Colour != colour).ToList();
        var bySize = sizeRows.Select(row => row.Axes.Colour).Distinct(StringComparer.Ordinal).Count() >= 2 ? Common([.. sizeRows.Select(row => row.Refs)]) : [];
        return [.. byColour.Concat(bySize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((key, index) => (Key: key, Index: index, Rank: rank.GetValueOrDefault(key, int.MaxValue)))
            .OrderBy(item => item.Rank).ThenBy(item => item.Index)
            .Select(item => assetByKey[item.Key])];
    }

    private static (string? Colour, string? Size) ColourAndSize(string? optionSignature)
    {
        string? colour = null, size = null;
        foreach (var pair in VariantOptionSignature.Parse(optionSignature))
        {
            if (colour is null && VariantOptionSignature.IsColorName(pair.Key)) colour = NullIfEmpty(CatalogKeyNormalizer.Key(pair.Value, 120));
            else if (size is null && VariantOptionSignature.IsSizeName(pair.Key)) size = NullIfEmpty(CatalogKeyNormalizer.Key(pair.Value, 120));
        }
        return (colour, size);
    }

    // A size's own picture list is the owner's once it is not the import's own copy of the card gallery: Trendyol keeps
    // one picture list per colour content, which the import writes to the colour's first size and to the card gallery
    // alike, so any other list on a size (assigned in the panel) is never the import's to replace or archive, whatever
    // the link's edit marks say (they are cleared by a lookup that overwrites the card).
    private async Task<bool> VariantPicturesAreOwnersAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        var rows = await db.ProductMedia.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == productId && (x.VariantId == variantId || x.VariantId == null) && x.Status == "ACTIVE")
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.VariantId, x.FileAssetId })
            .ToListAsync(cancellationToken);
        var own = rows.Where(x => x.VariantId == variantId).Select(x => x.FileAssetId).ToList();
        if (own.Count == 0) return false;
        return !own.SequenceEqual(rows.Where(x => x.VariantId == null).Select(x => x.FileAssetId));
    }

    // Returns the panel variants it matched (by link, then stock code, then barcode inside the card).
    private async Task<List<Guid>> SyncCatalogInventoryForPreservedProduct(
        Guid tenantId,
        Guid connectionId,
        Product product,
        RemoteCatalogProduct snapshot,
        ConnectionInventoryPolicy? inventoryPolicy,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool observeOnly = false,
        bool preferBarcode = false,
        bool updatePrices = false, bool updateStock = false,
        bool syncMedia = false)
    {
        var matched = new List<Guid>();
        var variants = await db.ProductVariants
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id)
            .ToListAsync(cancellationToken);
        if (variants.Count == 0) return matched;

        var externalIds = snapshot.Variants
            .Select(x => Short(x.ExternalVariantId, 256))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var links = externalIds.Length == 0
            ? new List<MarketplaceVariantLink>()
            : await db.MarketplaceVariantLinks
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalIds.Contains(x.ExternalId))
                .ToListAsync(cancellationToken);

        foreach (var remote in snapshot.Variants)
        {
            var externalId = Short(remote.ExternalVariantId, 256);
            var linkedVariantId = links.FirstOrDefault(x => string.Equals(x.ExternalId, externalId, StringComparison.Ordinal))?.VariantId;
            var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
            var skuNormalized = NormalizeCatalogKey(sku, 160);
            var barcodeNormalized = NormalizeCatalogKey(remote.Barcode, 160);
            var variant = linkedVariantId is Guid variantId
                ? variants.FirstOrDefault(x => x.Id == variantId)
                : null;
            if (preferBarcode)
            {
                if (!string.IsNullOrWhiteSpace(barcodeNormalized))
                    variant ??= variants.FirstOrDefault(x => CatalogKeyNormalizer.SameKey(x.BarcodeNormalized, barcodeNormalized));
            }
            else
            {
                variant ??= variants.FirstOrDefault(x => CatalogKeyNormalizer.SameKey(x.SkuNormalized, skuNormalized));
                if (variant is null && !string.IsNullOrWhiteSpace(barcodeNormalized))
                    variant = variants.FirstOrDefault(x => CatalogKeyNormalizer.SameKey(x.BarcodeNormalized, barcodeNormalized));
            }
            if (variant is null) continue;

            matched.Add(variant.Id);
            await UpsertCatalogOfferAndInventory(tenantId, connectionId, variant, remote, inventoryPolicy, now, cancellationToken, updateOffer: updatePrices, observeOnly: observeOnly, updateStock: updateStock, forceStockUpdate: updateStock);
            if (syncMedia && remote.ImageUrls is not null)
                await UpsertCatalogMedia(tenantId, product, variant.Id, remote.ImageUrls, $"{product.Title} · {OptionSignature(remote.Options)}", cancellationToken);
        }
        return matched;
    }

    // The focused price/stock path (ProductImportScanPolicy.FocusedPriceStock): the listing finds its panel card the way
    // UpsertCatalogProduct finds it (the product link, else the card owning its variants by link, stock code, barcode;
    // a link to a card without variants gives way to that owner too), and every variant its panel variant the way the
    // preserved-card path does (SyncCatalogInventoryForPreservedProduct: link, stock code, barcode). Only the ticked
    // prices (sale and list, with their price history) and stock (through the ledger) are written, by the same
    // UpsertCatalogOfferAndInventory; the listing's sale state was recorded when its page was read (MarkVariantLinksSeen).
    // No category context, attribute, reference, picture, content, desi, brand, category, link or local-change mark
    // changes. A size the panel lacks is not added: it is reported (PRODUCT_NEW_SIZES_NOT_IMPORTED), as a plain pull does.
    private async Task<FocusedPriceStockOutcome> ApplyFocusedPriceStock(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, ConnectionInventoryPolicy? inventoryPolicy, bool updatePrices, bool updateStock, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        var productId = await db.MarketplaceProductLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId)
            .Select(x => (Guid?)x.ProductId)
            .SingleOrDefaultAsync(cancellationToken);
        var product = productId is { } linkedId
            ? await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == linkedId, cancellationToken)
            : null;
        if (product is null || !await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.ProductId == product.Id, cancellationToken))
        {
            var owners = await CatalogProductsOwningVariants(tenantId, connectionId, snapshot, cancellationToken);
            if (owners.Count > 1)
            {
                await RecordIssue(tenantId, $"product-sync-variant-owner-conflict:{connectionId}:{Short(externalProductId, 200)}", "PRODUCT_VARIANT_CONFLICT", Short($"Pazar yeri ürününün varyantları panelde birden fazla ürüne ait; yeni ürün açılmadı, mevcut kayıtlar korundu · {snapshot.Title}", 1000), cancellationToken);
                return FocusedPriceStockOutcome.Conflict;
            }
            product = owners.Count == 1 ? await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == owners[0], cancellationToken) : null;
        }
        if (product is null) return FocusedPriceStockOutcome.NotInPanel;

        var pricesBefore = run.PriceChangedVariantIds.Count;
        var stockBefore = run.StockChangedVariantIds.Count;
        var matched = await SyncCatalogInventoryForPreservedProduct(tenantId, connectionId, product, snapshot, inventoryPolicy, now, cancellationToken, updatePrices: updatePrices, updateStock: updateStock);
        run.UnchangedVariantCount += matched.Distinct().Count(id => !run.PriceChangedVariantIds.Contains(id) && !run.StockChangedVariantIds.Contains(id));

        var unadded = (await RemoteVariantsMissingFromPanel(tenantId, connectionId, snapshot, cancellationToken)).Count;
        var issueKey = $"product-new-sizes:{connectionId:D}:{Short(externalProductId, 200)}";
        if (unadded > 0)
        {
            run.ProductUnaddedSizeCount = unadded;
            run.UnaddedSizeCount += unadded;
            await RecordIssue(tenantId, issueKey, "PRODUCT_NEW_SIZES_NOT_IMPORTED", Short($"{product.Title}: Trendyol'da panelde olmayan {UnaddedSizesNote(unadded)}", 1000), cancellationToken);
        }
        else
            await ResolveIssue(tenantId, issueKey, cancellationToken);
        return run.PriceChangedVariantIds.Count > pricesBefore || run.StockChangedVariantIds.Count > stockBefore
            ? FocusedPriceStockOutcome.Changed
            : FocusedPriceStockOutcome.Unchanged;
    }

    private async Task NormalizeLegacyWebColorOptions(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var options = await db.ProductOptions
            .Where(x => x.TenantId == tenantId && x.ProductId == productId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var legacy = options.Where(x => IsWebColorOptionKey(x.Label) || IsWebColorOptionKey(x.NormalizedKey)).ToList();
        if (legacy.Count == 0) return;

        var canonical = options.FirstOrDefault(x => IsColorOptionKey(x.Label) && !IsWebColorOptionKey(x.Label))
            ?? options.FirstOrDefault(x => string.Equals(x.NormalizedKey, NormalizeCatalogKey("Renk", 160), StringComparison.Ordinal));
        if (canonical is null)
        {
            canonical = legacy[0];
            canonical.Label = "Renk";
            canonical.NormalizedKey = NormalizeCatalogKey("Renk", 160);
            legacy.RemoveAt(0);
        }

        foreach (var duplicate in legacy)
        {
            var assignments = await db.VariantOptionValues
                .Where(x => x.TenantId == tenantId && x.OptionId == duplicate.Id)
                .ToListAsync(cancellationToken);
            db.VariantOptionValues.RemoveRange(assignments);
            var values = await db.ProductOptionValues
                .Where(x => x.TenantId == tenantId && x.OptionId == duplicate.Id)
                .ToListAsync(cancellationToken);
            db.ProductOptionValues.RemoveRange(values);
            db.ProductOptions.Remove(duplicate);
        }
    }

    private async Task<bool> CatalogSnapshotAlreadyApplied(Guid tenantId, Guid productId, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null || !string.Equals(product.Title, ProductTitle(snapshot.Title, snapshot.ExternalProductId), StringComparison.Ordinal) || !string.Equals(product.Description, snapshot.Description ?? "", StringComparison.Ordinal)) return false;
        var variantCount = await db.ProductVariants.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.ProductId == productId, cancellationToken);
        if (variantCount < snapshot.Variants.Count) return false;
        // A gallery the user manages is not compared with the remote one; the import leaves it as it is.
        if (await MediaIsUserManagedAsync(tenantId, productId, null, cancellationToken)) return true;

        var expectedUrls = CatalogImageIdentity.DistinctUrls(snapshot.ImageUrls);
        var mediaRows = await (from media in db.ProductMedia.AsNoTracking()
                               join asset in db.FileAssets.AsNoTracking()
                                   on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                               where media.TenantId == tenantId
                                   && media.ProductId == productId
                                   && media.VariantId == null
                                   && media.Status == "ACTIVE"
                                   && asset.Status == "ACTIVE"
                                   && asset.ArchivedAt == null
                                   && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                               orderby media.SortOrder
                               select new { media.FileAssetId, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        var actualUrls = mediaRows
            .Select(row => row.Classification == "PRODUCT_MEDIA_URL"
                ? row.RelativePath
                : $"/api/v1/files/product-media/{row.FileAssetId:D}/content")
            .ToList();
        return actualUrls.SequenceEqual(expectedUrls, StringComparer.OrdinalIgnoreCase);
    }

    // The panel brand a product import gives the listing's brand (owner decision 12 Nov 2026), the way EnsureMappedCategory
    // settles its category, from what the listing carries alone (no marketplace call):
    //   1. the brand mapping on this connection for the listing's brand id (re-verified when it was not VERIFIED);
    //   2. else the panel brand the name names (PanelBrandByName, the rule the file imports share too), mapped now;
    //   3. else a new panel brand under the listing's name, mapped now (VERIFIED, Source IMPORT).
    // A mapping is written only where the marketplace has a brand list and the listing carries the brand's id
    // (byExternalId: Trendyol brand.id). Hepsiburada, n11, Pazarama, PTT AVM and Shopify listings carry a brand name only
    // (or a vendor with no list behind it), so for them the brand is matched by name in the panel and no mapping is made.
    // A panel brand the user switched off is not revived; two panel brands mapped to one marketplace brand are the user's
    // to settle; both leave the product without a brand and list the brand on the mapping page (BRAND_MAPPING_MISSING).
    // With create false (sizes added to a linked card) only an existing mapping is read and nothing is written.
    // KATALOG-14 phase D5b: the panel model of the listing's code when its brand is another than the listing's (an
    // unknown brand on either side is no conflict: unknown stays unknown).
    private async Task<(string Key, string Code, string Brand)?> OtherBrandModelAsync(Guid tenantId, RemoteCatalogProduct snapshot, Guid? brandId, CancellationToken cancellationToken)
    {
        var code = snapshot.Variants.Select(variant => variant.ModelCode?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (brandId is not Guid brand || ProductFamilyKeyPolicy.ModelCodeKey(code) is not { } key) return null;
        var model = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelCodeKey == key && x.BrandId != null && x.BrandId != brand)
            .Select(x => new { x.ModelCode, x.BrandId }).FirstOrDefaultAsync(cancellationToken);
        if (model is null) return null;
        var name = await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == model.BrandId).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken);
        return (key, string.IsNullOrWhiteSpace(model.ModelCode) ? code! : model.ModelCode, name ?? "markasız");
    }
    private async Task<Guid?> ImportBrandId(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, string platformName, bool byExternalId, bool create, CancellationToken cancellationToken)
    {
        var externalId = byExternalId && !string.IsNullOrWhiteSpace(snapshot.BrandExternalId) ? Short(snapshot.BrandExternalId, 256) : null;
        var brandName = Short(snapshot.BrandName, 160);
        var now = timeProvider.GetUtcNow();
        Task Unusable(string reason) => externalId is null
            ? Task.CompletedTask
            : RecordIssue(tenantId, ImportedBrandRule.IssueKey(connectionId, externalId), ImportedBrandRule.IssueCode, Short(ImportedBrandRule.UnusableSummary(platformName, externalId, snapshot.BrandName, reason), 1000), cancellationToken);

        if (externalId is not null)
        {
            var mappings = db.BrandMappings.Local
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == "" && x.ExternalId == externalId && db.Entry(x).State == EntityState.Added)
                .ToList();
            mappings.AddRange(await db.BrandMappings
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == "" && x.ExternalId == externalId)
                .ToListAsync(cancellationToken));
            if (mappings.Count > 0)
            {
                var localIds = mappings.Select(x => x.LocalId).Distinct().ToArray();
                var activeIds = db.Brands.Local.Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && x.IsActive).Select(x => x.Id)
                    .Concat(await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && x.IsActive).Select(x => x.Id).ToListAsync(cancellationToken))
                    .Distinct().ToArray();
                if (ImportedBrandRule.Resolve(activeIds) is { } mappedId && localIds.Length == 1)
                {
                    if (create)
                        foreach (var stale in mappings.Where(x => x.Status != "VERIFIED"))
                        {
                            stale.Status = "VERIFIED";
                            stale.VerifiedAt = now;
                            stale.Version++;
                        }
                    return mappedId;
                }
                if (create) await Unusable(localIds.Length > 1 ? "birden fazla panel markasına eşli" : "eşlendiği panel markası kapalı");
                return null;
            }
        }
        if (!create) return null;

        // By name: the rule every import shares (PanelBrandByName).
        var byName = await new PanelBrandByName(db, tenantId).FindOrCreateAsync(brandName, now, cancellationToken);
        if (byName.SwitchedOff is { } switchedOff)
        {
            await Unusable($"aynı adlı panel markası «{Short(switchedOff.Name, 120)}» kapalı");
            return null;
        }
        if (byName.Brand is not { } brand) return null;
        if (externalId is null) return brand.Id;

        // A panel brand maps to one marketplace brand per connection (unique index): one already mapped to another id
        // keeps its mapping; the product still takes it by name and the brand is listed for the user to check.
        var brandId = brand.Id;
        var alreadyMapped = db.BrandMappings.Local.Any(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == "" && x.LocalId == brandId && db.Entry(x).State != EntityState.Deleted)
            || await db.BrandMappings.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == "" && x.LocalId == brandId, cancellationToken);
        if (alreadyMapped)
        {
            await Unusable($"aynı adlı panel markası «{Short(brand.Name, 120)}» bu bağlantıda başka bir markaya eşli; ürünler bu panel markasıyla aktarıldı, eşleme yazılmadı");
            return brandId;
        }
        var brandSnapshotId = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.ScopeExternalId == "" && x.IsCurrent)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = brandSnapshotId ?? Guid.Empty, LocalId = brandId, ScopeExternalId = "", ExternalId = externalId, Status = "VERIFIED", VerifiedAt = now, Source = MappingSources.Import, Version = 1 });
        await ResolveIssue(tenantId, ImportedBrandRule.IssueKey(connectionId, externalId), cancellationToken);
        return brandId;
    }
}
