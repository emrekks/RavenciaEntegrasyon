using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // crossMatchCodes (n11): the seller stock code may be the panel barcode
    // and the n11 barcode the panel SKU. Model-code candidates are narrowed
    // by colour and size on every marketplace (Y5). reportUnmatched records every remote
    // variant that found no local variant so it can be fixed by hand. seedOffers (n11)
    // opens the channel offer of a linked variant from the remote price when
    // the variant has none yet; existing offers are never changed.
    private async Task<bool> MapExistingCatalogProduct(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, bool preferBarcode, CancellationToken cancellationToken, bool crossMatchCodes = false, bool reportUnmatched = false, bool seedOffers = false, ConnectionInventoryPolicy? inventoryPolicy = null, string seedSource = "N11", bool pttAvm = false)
    {
        if (pttAvm) return await MapPttAvmProduct(tenantId, connectionId, snapshot, reportUnmatched, seedOffers, inventoryPolicy, cancellationToken);
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        if (string.IsNullOrWhiteSpace(externalProductId)) return false;
        var remoteVariants = snapshot.Variants
            .Where(variant => !string.IsNullOrWhiteSpace(variant.ExternalVariantId))
            .ToList();
        if (remoteVariants.Count == 0) return false;
        // The run's result line counts variants: Eşlenen (a link made now), Zaten eşli, Panelde karşılığı yok, Çakışma.
        bool NoCounterpart() { telemetryNoCounterpartCount += remoteVariants.Count; return false; }
        bool Conflict() { telemetryMappingConflictCount += remoteVariants.Count; return false; }

        var now = timeProvider.GetUtcNow();
        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        var modelCodes = !preferBarcode
            ? remoteVariants.Select(variant => variant.ModelCode?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        // Both stored key forms (shared key and legacy panel form) are looked up until the recompute.
        var barcodes = CatalogKeyNormalizer.LookupKeys(remoteVariants.Select(variant => variant.Barcode));
        var skus = CatalogKeyNormalizer.LookupKeys(remoteVariants.Select(variant => variant.Sku));
        var rawSkus = remoteVariants.Select(variant => variant.Sku?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (modelCodes.Length == 0 && barcodes.Length == 0 && skus.Length == 0) return NoCounterpart();

        // A Shopify product can contain several colour variants while the
        // panel stores each colour as its own Product. Load every candidate
        // variant first; requiring one ProductId here incorrectly rejected
        // valid multi-product matches.
        var localVariants = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId
                && ((variant.BarcodeNormalized != null && barcodes.Contains(variant.BarcodeNormalized))
                    || (variant.SkuNormalized != null && skus.Contains(variant.SkuNormalized))
                    || (crossMatchCodes && variant.BarcodeNormalized != null && skus.Contains(variant.BarcodeNormalized))
                    || (crossMatchCodes && variant.SkuNormalized != null && barcodes.Contains(variant.SkuNormalized))
                    || (preferBarcode && variant.ModelCode != null && rawSkus.Contains(variant.ModelCode))
                    || (!preferBarcode && variant.ModelCode != null && modelCodes.Contains(variant.ModelCode))))
            .ToListAsync(cancellationToken);
        if (localVariants.Count == 0 && link is null)
        {
            if (reportUnmatched)
                foreach (var remote in remoteVariants)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{Short(remote.ExternalVariantId, 256)}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Pazar yeri varyantı eşleşmedi: stok kodu {remote.Sku}, barkod {remote.Barcode ?? "yok"}, model kodu {remote.ModelCode ?? "yok"} · {snapshot.Title}. Panelde aynı stok kodu, barkod veya model kodu yok.", 1000), cancellationToken);
            return NoCounterpart();
        }

        var localVariantIds = localVariants.Select(variant => variant.Id).ToArray();
        var externalVariantIds = remoteVariants.Select(variant => Short(variant.ExternalVariantId, 256)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existingVariantLinks = await db.MarketplaceVariantLinks
            .Where(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && (localVariantIds.Contains(existing.VariantId) || externalVariantIds.Contains(existing.ExternalId)))
            .ToListAsync(cancellationToken);
        var claimedVariantIds = existingVariantLinks
            .Where(existing => !externalVariantIds.Contains(existing.ExternalId, StringComparer.OrdinalIgnoreCase))
            .Select(existing => existing.VariantId)
            .ToHashSet();
        var matchedVariantCount = 0;

        // Each remote variant is resolved before anything is linked: a barcode or stock code names one panel variant,
        // a model code names every colour and size of the model, so a model-code candidate must also carry the remote
        // variant's colour and size (Y5, on every marketplace; Trendyol and Hepsiburada bound by the model code alone).
        var resolved = remoteVariants.Select(remote =>
        {
            var remoteBarcode = NormalizeCatalogKey(remote.Barcode, 160);
            var remoteSku = NormalizeCatalogKey(remote.Sku, 160);
            var remoteModelCode = NormalizeCatalogKey(remote.ModelCode, 160);
            var barcodeMatches = string.IsNullOrWhiteSpace(remoteBarcode)
                ? []
                : localVariants.Where(variant => CatalogKeyNormalizer.SameKey(variant.BarcodeNormalized, remoteBarcode)
                    || crossMatchCodes && CatalogKeyNormalizer.SameKey(variant.SkuNormalized, remoteBarcode)).ToList();
            var skuMatches = string.IsNullOrWhiteSpace(remoteSku)
                ? []
                : localVariants.Where(variant => CatalogKeyNormalizer.SameKey(variant.SkuNormalized, remoteSku)
                    || crossMatchCodes && CatalogKeyNormalizer.SameKey(variant.BarcodeNormalized, remoteSku)
                    || preferBarcode && string.Equals(NormalizeCatalogKey(variant.ModelCode, 160), remoteSku, StringComparison.Ordinal)).ToList();
            var modelCandidates = preferBarcode || string.IsNullOrWhiteSpace(remoteModelCode)
                ? []
                : localVariants.Where(variant => string.Equals(NormalizeCatalogKey(variant.ModelCode, 160), remoteModelCode, StringComparison.Ordinal)).ToList();
            var modelMatches = NarrowModelCodeCandidates(modelCandidates, variant => variant.OptionSignature, remote.Options, snapshot.Title);
            var byModelCode = barcodeMatches.Count == 0 && skuMatches.Count == 0;
            var matches = !byModelCode ? barcodeMatches.Count > 0 ? barcodeMatches : skuMatches : modelMatches;
            return new
            {
                Remote = remote,
                ExternalVariantId = Short(remote.ExternalVariantId, 256),
                Matches = matches,
                Candidate = matches.Count == 1 ? matches[0] : null,
                // Model-code candidates whose colour and size could not single one out: never picked, left for review.
                ModelCodeUndecided = byModelCode && modelCandidates.Count > 0 && matches.Count != 1,
                ModelCandidateCount = modelCandidates.Count
            };
        }).ToList();

        // The panel product follows the variants the remote ones resolved to, never the first candidate a query
        // returned. Variants that land on several panel products (a model kept as one card per colour) decide none:
        // the product link is not made and the product is put up for review.
        // Shopify (preferBarcode) keeps a model as one product with every colour while the panel keeps one card per
        // colour: that is its normal shape, not a doubt. Its variants are paired by barcode or stock code, one panel
        // variant each, so they are linked one by one without a product link and nothing is put up for review
        // (owner, 14 Nov 2026: MZ035YNC, 8 cards x 3 sizes in one Shopify product, was never linked).
        var linkProduct = true;
        Guid? productId = link?.ProductId;
        if (productId is null)
        {
            var resolvedProductIds = resolved
                .Where(item => item.Candidate is not null && !claimedVariantIds.Contains(item.Candidate.Id))
                .Select(item => item.Candidate!.ProductId)
                .Distinct()
                .ToList();
            // Only the colour cards of one model (one linked model, or one brand and model code) make that normal shape;
            // cards of different models fall to the conflict handling below (audit 14 Nov 2026).
            if (resolvedProductIds.Count > 1 && preferBarcode && await ProductModelLinks.OneModelAsync(db, tenantId, resolvedProductIds, cancellationToken))
            {
                await ResolveIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{externalProductId}", cancellationToken);
                linkProduct = false;
            }
            else if (resolvedProductIds.Count > 1)
            {
                await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{externalProductId}", "PRODUCT_MAPPING_UNMATCHED",
                    Short($"Pazar yeri ürününün varyantları panelde {resolvedProductIds.Count} ayrı ürüne (renk kartlarına) düştü; ürün bağlantısı seçilmedi, eşleşmeyi kontrol edin · {snapshot.Title}.", 1000), cancellationToken);
                if (!crossMatchCodes) return Conflict();
                linkProduct = false;
            }
            else if (resolvedProductIds.Count == 1)
            {
                productId = resolvedProductIds[0];
                if (await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && existing.ProductId == productId.Value && existing.ExternalId != externalProductId, cancellationToken))
                {
                    if (!crossMatchCodes) return Conflict();
                    linkProduct = false;
                }
            }
            else linkProduct = false;
        }
        if (productId is { } chosenProductId && !await db.Products.AsNoTracking().AnyAsync(product => product.TenantId == tenantId && product.Id == chosenProductId, cancellationToken)) return NoCounterpart();

        foreach (var item in resolved)
        {
            var remote = item.Remote;
            var externalVariantId = item.ExternalVariantId;
            var matches = item.Matches;
            var candidate = item.Candidate;
            if (candidate is null || claimedVariantIds.Contains(candidate.Id))
            {
                if (candidate is not null || matches.Count > 1 || item.ModelCodeUndecided) telemetryMappingConflictCount++;
                else telemetryNoCounterpartCount++;
                if (reportUnmatched || item.ModelCodeUndecided)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{externalVariantId}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Pazar yeri varyantı eşleşmedi: stok kodu {remote.Sku}, barkod {remote.Barcode ?? "yok"}, model kodu {remote.ModelCode ?? "yok"}, {string.Join(", ", remote.Options.Where(option => IsRealColorOptionKey(option.Key) || IsSizeOptionKey(option.Key)).Select(option => $"{option.Key}: {option.Value}"))} · {snapshot.Title}. {(candidate is not null ? "Aday panel varyantı başka bir pazar yeri varyantına bağlı." : matches.Count > 1 ? $"{matches.Count} panel varyantı aday çıktı." : item.ModelCodeUndecided ? $"Model kodu {item.ModelCandidateCount} panel varyantında var ama hiçbirinin rengi ve bedeni bu varyantınki değil." : "Panelde aynı stok kodu, barkod veya model kodu yok.")}", 1000),
                        cancellationToken);
                continue;
            }

            var externalLink = existingVariantLinks.FirstOrDefault(existing => string.Equals(existing.ExternalId, externalVariantId, StringComparison.OrdinalIgnoreCase));
            if (externalLink is not null)
            {
                if (externalLink.VariantId == candidate.Id) { matchedVariantCount++; telemetryAlreadyMappedCount++; if (seedOffers) await SeedChannelOfferAsync(tenantId, connectionId, candidate.Id, remote, inventoryPolicy, now, cancellationToken, seedSource); }
                else telemetryMappingConflictCount++;
                if (externalLink.VariantId != candidate.Id && reportUnmatched)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{externalVariantId}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Pazar yeri varyantı {remote.Sku} daha önce başka bir panel varyantına bağlanmış; şimdiki eşleşme {candidate.Sku}. Bağlantı değiştirilmedi · {snapshot.Title}.", 1000), cancellationToken);
                claimedVariantIds.Add(externalLink.VariantId);
                continue;
            }
            var localLink = existingVariantLinks.FirstOrDefault(existing => existing.VariantId == candidate.Id);
            if (localLink is not null)
            {
                telemetryMappingConflictCount++;
                if (reportUnmatched)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{externalVariantId}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Pazar yeri varyantı eşleşmedi: stok kodu {remote.Sku}, panel varyantı {candidate.Sku} zaten {localLink.ExternalId} stok koduna bağlı · {snapshot.Title}.", 1000), cancellationToken);
                continue;
            }

            var mappedLink = new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = candidate.Id, ExternalId = externalVariantId, Version = 1 };
            ListingSaleStateReads.ApplyToNewLink(mappedLink, remote.SaleState, remote.SaleStateReason, now);
            db.MarketplaceVariantLinks.Add(mappedLink);
            existingVariantLinks.Add(new MarketplaceVariantLink { TenantId = tenantId, ConnectionId = connectionId, VariantId = candidate.Id, ExternalId = externalVariantId, Version = 1 });
            claimedVariantIds.Add(candidate.Id);
            matchedVariantCount++;
            telemetryMappedCount++;
            telemetryInsertedCount++;
            if (seedOffers) await SeedChannelOfferAsync(tenantId, connectionId, candidate.Id, remote, inventoryPolicy, now, cancellationToken, seedSource);
        }

        if (matchedVariantCount == 0) return false;
        if (!linkProduct || productId is null) return true;
        if (link is null)
        {
            link = new MarketplaceProductLink
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                ProductId = productId.Value,
                ExternalId = externalProductId,
                SyncStatus = "MAPPED",
                LastImportedAt = now,
                Version = 1
            };
            db.MarketplaceProductLinks.Add(link);
            telemetryInsertedCount++;
        }
        // A product link that already exists stays as it is (owner, 12 Nov 2026: mapping only links): until then it was
        // re-marked MAPPED, which wiped its sync state (SYNCED, LOCAL_CHANGES_PENDING) and its import time.

        return true;
    }

    // PTT AVM keeps a product by its barcode, which is also its seller stock code: the model code for a product with
    // variants, the product's own code for one without. Its variants are colour and size combinations, each with a
    // variant stock code equal to the panel variant's stock code; the SOAP service lists only the colour and size
    // values of a product ("FK4530/7101" = "Lacivert"), never the combinations or their codes. So the product is
    // found by its barcode (a panel model code, else a panel variant's code), every active panel variant of that
    // model is linked by its own stock code, and a variant whose colour or size PTT AVM does not list is left out.
    private async Task<bool> MapPttAvmProduct(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, bool reportUnmatched, bool seedOffers, ConnectionInventoryPolicy? inventoryPolicy, CancellationToken cancellationToken)
    {
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        var productCode = snapshot.ProductMainId?.Trim();
        var codeKey = NormalizeCatalogKey(productCode, 160);
        if (string.IsNullOrWhiteSpace(externalProductId) || codeKey.Length == 0) return false;
        var remoteVariants = snapshot.Variants.Where(variant => !string.IsNullOrWhiteSpace(variant.ExternalVariantId)).ToList();
        if (remoteVariants.Count == 0) return false;
        var now = timeProvider.GetUtcNow();
        var lookupKeys = CatalogKeyNormalizer.LookupKeys(productCode);

        // A product with variants: the panel model whose code is the product barcode; else the model of the panel
        // variant whose own code it is (a product kept on PTT AVM under one variant's code).
        var byModel = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId && variant.Status != ProductStatus.Archived && variant.ModelCode != null && variant.ModelCode == productCode)
            .ToListAsync(cancellationToken);
        var byCode = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId && variant.Status != ProductStatus.Archived
                && ((variant.SkuNormalized != null && lookupKeys.Contains(variant.SkuNormalized)) || (variant.BarcodeNormalized != null && lookupKeys.Contains(variant.BarcodeNormalized))))
            .ToListAsync(cancellationToken);
        var optionValues = PttAvmOptionValues(remoteVariants);
        List<ProductVariant> modelVariants;
        var compliant = true;
        if (byModel.Count > 0)
        {
            modelVariants = byModel;
        }
        else if (byCode.Count == 1 && (optionValues.Count == 0 || string.IsNullOrWhiteSpace(byCode[0].ModelCode)))
        {
            // A product without variants: its barcode is the panel variant's own code.
            modelVariants = byCode;
        }
        else if (byCode.Count == 1)
        {
            var modelCode = byCode[0].ModelCode;
            modelVariants = await db.ProductVariants.AsNoTracking()
                .Where(variant => variant.TenantId == tenantId && variant.Status != ProductStatus.Archived && variant.ModelCode == modelCode)
                .ToListAsync(cancellationToken);
            compliant = false;
        }
        else
        {
            if (reportUnmatched)
                await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:ptt:{externalProductId}", "PRODUCT_MAPPING_UNMATCHED",
                    Short(byCode.Count > 1
                        ? $"PttAVM ürün kodu {productCode} panelde {byCode.Count} varyantın kodu; hangisi olduğu seçilemedi · {snapshot.Title}."
                        : $"PttAVM ürün kodu {productCode} panelde yok: aynı model kodu veya stok kodu bulunamadı · {snapshot.Title}.", 1000), cancellationToken);
            return false;
        }

        // The panel may keep one model as several products (one per colour, as Trendyol lists it): every variant is
        // linked, and the PTT AVM product links to the product of the model's first variant; the others reach the
        // product's barcode through the model code when price and stock are sent.
        var productId = modelVariants.OrderBy(variant => variant.Sku, StringComparer.Ordinal).ThenBy(variant => variant.Id).First().ProductId;
        if (!compliant && reportUnmatched)
            await RecordIssue(tenantId, $"pttavm-product-code:{connectionId}:{externalProductId}", "PTTAVM_PRODUCT_CODE_NOT_MODEL",
                Short($"PttAVM'de varyantlı ürünün barkodu/satıcı stok kodu {productCode}, panel model kodu {modelVariants[0].ModelCode}. Kurala göre PttAVM'de model koduna çevirin · {snapshot.Title}.", 1000), cancellationToken);

        var variantIds = modelVariants.Select(variant => variant.Id).ToArray();
        var codes = modelVariants.Select(PttAvmVariantCode).Where(code => code is not null).Select(code => code!).ToArray();
        var existingVariantLinks = await db.MarketplaceVariantLinks
            .Where(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && (variantIds.Contains(existing.VariantId) || codes.Contains(existing.ExternalId)))
            .ToListAsync(cancellationToken);
        var matched = 0;
        foreach (var variant in modelVariants.OrderBy(variant => variant.SortOrder).ThenBy(variant => variant.Id))
        {
            var code = PttAvmVariantCode(variant);
            var own = existingVariantLinks.FirstOrDefault(existing => existing.VariantId == variant.Id);
            var missing = code is null ? [] : PttAvmMissingOptionValues(variant.OptionSignature, optionValues);
            if (code is null || missing.Count > 0)
            {
                // A stale link (an old "MODEL/123" option id) of a variant PTT AVM does not sell is removed, so price and
                // stock are never sent for it.
                if (own is not null && PttAvmVariantMapper.IsModelVariantId(own.ExternalId)) db.MarketplaceVariantLinks.Remove(own);
                if (reportUnmatched)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{variant.Id:N}", "PRODUCT_MAPPING_UNMATCHED",
                        Short(code is null
                            ? $"PttAVM varyantı bağlanmadı: panel varyantının stok kodu ve barkodu yok · {snapshot.Title}."
                            : $"PttAVM'de bu renk/beden yok: {string.Join(", ", missing)} ({code}); ürün {productCode} · {snapshot.Title}.", 1000), cancellationToken);
                continue;
            }
            var taken = existingVariantLinks.FirstOrDefault(existing => existing.VariantId != variant.Id && string.Equals(existing.ExternalId, code, StringComparison.OrdinalIgnoreCase));
            if (taken is not null)
            {
                if (reportUnmatched)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{variant.Id:N}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"PttAVM kodu {code} başka bir panel varyantına bağlı; {variant.Sku} bağlanmadı · {snapshot.Title}.", 1000), cancellationToken);
                continue;
            }
            if (own is null)
            {
                own = new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variant.Id, ExternalId = code, ExternalBarcode = variant.Barcode?.Trim(), ExternalSku = variant.Sku.Trim(), Version = 1 };
                // The model's sale state, as MarkPttAvmModelLinksSeen reads it for a link that already existed.
                var (modelSaleState, modelSaleReason) = PttAvmModelSaleState(snapshot);
                ListingSaleStateReads.ApplyToNewLink(own, modelSaleState, modelSaleReason, timeProvider.GetUtcNow());
                db.MarketplaceVariantLinks.Add(own);
                existingVariantLinks.Add(own);
                telemetryInsertedCount++;
            }
            else if (own.ExternalId != code || own.ExternalBarcode != variant.Barcode?.Trim() || own.ExternalSku != variant.Sku.Trim())
            {
                own.ExternalId = code;
                own.ExternalBarcode = variant.Barcode?.Trim();
                own.ExternalSku = variant.Sku.Trim();
                own.Version++;
            }
            // PTT AVM listed this product with the variant's colour/size: its code holds there.
            MarketplaceVariantLinkHealth.MarkSeen(own, now);
            matched++;
            if (seedOffers)
            {
                var seed = remoteVariants.FirstOrDefault(remote => HasOptionValues(variant.OptionSignature, remote.Options)) ?? remoteVariants[0];
                // A colour or size row's stock is not the variant's, so it never marks the panel stock as already sent.
                if (PttAvmVariantMapper.IsModelVariantId(seed.ExternalVariantId)) seed = seed with { StockQuantity = null };
                await SeedChannelOfferAsync(tenantId, connectionId, variant.Id, seed, inventoryPolicy, now, cancellationToken, "PTTAVM");
            }
        }
        if (matched == 0) return false;

        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        if (link is null)
        {
            if (await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && existing.ProductId == productId, cancellationToken))
            {
                if (reportUnmatched)
                    await RecordIssue(tenantId, $"pttavm-product-link:{connectionId}:{externalProductId}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Panel ürünü başka bir PttAVM ürününe bağlı; {productCode} varyantları bağlandı, ürün bağı kurulmadı · {snapshot.Title}.", 1000), cancellationToken);
                return true;
            }
            db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ProductId = productId, ExternalId = externalProductId, ExternalCode = productCode, SyncStatus = "MAPPED", LastImportedAt = now, Version = 1 });
            telemetryInsertedCount++;
        }
        else
        {
            link.ExternalCode = productCode;
            link.LastImportedAt = now;
            link.SyncStatus = "MAPPED";
            link.Version++;
        }
        return true;
    }

    // The code PTT AVM keeps a panel variant by: its stock code (PTT AVM's variant stock code), else its barcode.
    private static string? PttAvmVariantCode(ProductVariant variant) =>
        !string.IsNullOrWhiteSpace(variant.Sku) ? variant.Sku.Trim() : string.IsNullOrWhiteSpace(variant.Barcode) ? null : variant.Barcode.Trim();

    // The colour and size values PTT AVM lists for a product (its "variants" are these values, one per row).
    internal static IReadOnlySet<string> PttAvmOptionValues(IEnumerable<RemoteCatalogVariant> remoteVariants) =>
        remoteVariants
            .Where(variant => PttAvmVariantMapper.IsModelVariantId(variant.ExternalVariantId))
            .SelectMany(variant => variant.Options.Values)
            .Select(value => NormalizeCatalogKey(CleanCatalogOptionValue(value), 120))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    // The panel variant's colour and size values PTT AVM does not list for the product; none when it lists no values.
    internal static IReadOnlyList<string> PttAvmMissingOptionValues(string? localSignature, IReadOnlySet<string> optionValues)
    {
        if (optionValues.Count == 0 || string.IsNullOrWhiteSpace(localSignature)) return [];
        return VariantOptionSignature.ParseKeyed(localSignature)
            .Where(option => IsRealColorOptionKey(option.Key) || IsSizeOptionKey(option.Key))
            .Select(option => option.Value)
            .Where(value => value.Length > 0 && !optionValues.Contains(value))
            .ToList();
    }

    private async Task SeedChannelOfferAsync(Guid tenantId, Guid connectionId, Guid variantId, RemoteCatalogVariant remote, ConnectionInventoryPolicy? inventoryPolicy, DateTimeOffset now, CancellationToken cancellationToken, string source = "N11")
    {
        if (remote.SalePrice is null && remote.ListPrice is null) return;
        var salePrice = decimal.Round(Math.Max(0m, remote.SalePrice ?? remote.ListPrice ?? 0m), 2, MidpointRounding.AwayFromZero);
        var listPrice = decimal.Round(Math.Max(salePrice, Math.Max(0m, remote.ListPrice ?? salePrice)), 2, MidpointRounding.AwayFromZero);
        var existing = db.ChannelOffers.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId)
            ?? await db.ChannelOffers.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId, cancellationToken);
        if (existing is not null)
        {
            // Pazarama refuses an update that repeats the values it already holds. An offer that is still the
            // untouched copy of the remote price and stock counts as sent, so only real changes go out later.
            if (source == "PAZARAMA") await MarkPazaramaOfferInSyncAsync(tenantId, existing, remote, salePrice, listPrice, cancellationToken);
            if (source == "PTTAVM") await MarkPttAvmOfferInSyncAsync(tenantId, existing, remote, salePrice, listPrice, cancellationToken);
            return;
        }
        if (salePrice <= 0) return;
        var vatRate = await NewImportedOfferVatRateAsync(tenantId, connectionId, variantId, remote.VatRate, cancellationToken);
        var offer = new ChannelOffer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            VariantId = variantId,
            ListPrice = listPrice,
            SalePrice = salePrice,
            Currency = NormalizeCurrency(remote.Currency),
            VatRate = vatRate,
            VatInclusion = "INCLUDED",
            RoundingMode = "HALF_EVEN",
            SafetyStock = decimal.Round(Math.Max(0m, inventoryPolicy?.DefaultSafetyStock ?? 0m), 4, MidpointRounding.ToEven),
            Status = remote.Archived ? "INACTIVE" : "ACTIVE",
            PriceVersion = 1,
            Version = 1
        };
        db.ChannelOffers.Add(offer);
        if (source == "PAZARAMA") await MarkPazaramaOfferInSyncAsync(tenantId, offer, remote, salePrice, listPrice, cancellationToken);
        if (source == "PTTAVM") await MarkPttAvmOfferInSyncAsync(tenantId, offer, remote, salePrice, listPrice, cancellationToken);
        db.ChannelPriceHistory.Add(new ChannelPriceHistory
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OfferId = offer.Id,
            PriceVersion = offer.PriceVersion,
            ListPrice = offer.ListPrice,
            SalePrice = offer.SalePrice,
            Currency = offer.Currency,
            Reason = $"{source}_CATALOG_MAPPING",
            ActorSource = $"SYSTEM:{source}",
            EffectiveAt = now
        });
        telemetryInsertedCount++;
    }

    private async Task MarkPazaramaOfferInSyncAsync(Guid tenantId, ChannelOffer offer, RemoteCatalogVariant remote, decimal salePrice, decimal listPrice, CancellationToken cancellationToken)
    {
        var changed = false;
        if (offer.LastPriceHash is null && offer.PriceVersion == 1 && offer.SalePrice == salePrice && offer.ListPrice == listPrice)
        {
            offer.LastPriceHash = PazaramaPriceInventoryRules.PriceHash(offer.ListPrice, offer.SalePrice, offer.PriceVersion);
            changed = true;
        }
        if (offer.LastStockProjectionVersion is null && remote.StockQuantity is { } remoteStock)
        {
            var inventory = await db.InventoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == offer.VariantId && x.LocationCode == "MAIN", cancellationToken);
            if (inventory is not null && PazaramaPriceInventoryRules.Quantity(inventory.Available - offer.SafetyStock) == PazaramaPriceInventoryRules.Quantity(remoteStock))
            {
                offer.LastStockProjectionVersion = inventory.ProjectionVersion;
                changed = true;
            }
        }
        if (changed && db.Entry(offer).State != EntityState.Added) offer.Version++;
    }
}
