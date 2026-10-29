using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Imports;

public sealed class ImportJobProcessor(
    AppDbContext db,
    IPrivateFileStorage files,
    TimeProvider timeProvider,
    IConfiguration configuration) : IImportJobProcessor
{
    // Rows are written in batches of this size inside one transaction, so a large file neither keeps every
    // entity in the change tracker nor leaves half of the rows applied when a later batch fails.
    internal const int BatchSize = 500;
    private const int LookupChunk = 1000;
    private readonly long _maximumBytes = configuration.GetValue<long?>("Storage:MaxUploadBytes") ?? 10 * 1024 * 1024;

    public Task<bool> ProcessAsync(Guid tenantId, Guid sessionId, string operation, CancellationToken cancellationToken) =>
        operation switch
        {
            "PREVIEW" => PreviewAsync(tenantId, sessionId, cancellationToken),
            "APPLY" => ApplyAsync(tenantId, sessionId, cancellationToken),
            _ => Task.FromResult(false)
        };

    private async Task<bool> PreviewAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.ImportSessions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sessionId, cancellationToken);
        if (session is null || session.Status != ImportSessionStatus.Fetching || session.SourceAssetId is null || session.ColumnProfileId is null) return false;

        ImportStateMachine.Transition(session, ImportSessionStatus.Matching);
        session.UpdatedAt = timeProvider.GetUtcNow();
        session.Version++;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var asset = await db.FileAssets.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == session.SourceAssetId, cancellationToken);
            var mappings = await db.ImportColumnMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ProfileId == session.ColumnProfileId)
                .OrderBy(x => x.SortOrder)
                .ToDictionaryAsync(x => x.SourceColumn, x => x.TargetField, StringComparer.OrdinalIgnoreCase, cancellationToken);
            List<ParsedImportRow> parsed;
            await using (var source = await files.OpenReadAsync(tenantId, asset.RelativePath, cancellationToken))
            {
                parsed = (session.SourceType == ImportSourceType.Csv
                    ? ImportFileReader.ReadCsv(source, mappings)
                    : ImportFileReader.ReadXlsx(source, mappings, _maximumBytes)).ToList();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.ImportMatchCandidates.Where(x => x.TenantId == tenantId && x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
            await db.ImportStagingRecords.Where(x => x.TenantId == tenantId && x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);

            var total = 0;
            var valid = 0;
            var errors = 0;
            var reviews = 0;
            foreach (var batch in parsed.Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagingRows = new List<(ImportStagingRecord Staging, IReadOnlyDictionary<string, string> Values)>(batch.Length);
                foreach (var row in batch)
                {
                    total++;
                    var rawJson = JsonSerializer.Serialize(row.Values);
                    var safeValues = SanitizeImportedValues(row.Values);
                    var safeJson = JsonSerializer.Serialize(safeValues);
                    var staging = new ImportStagingRecord
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        SessionId = sessionId,
                        RowNumber = row.RowNumber,
                        ExternalRecordId = Value(safeValues, "externalId"),
                        RawJson = rawJson,
                        SafeValuesJson = safeJson,
                        ValidationErrorsJson = JsonSerializer.Serialize(row.Errors),
                        RowHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(safeJson))),
                        SkuNormalized = Normalize(Value(safeValues, "sku")),
                        BarcodeNormalized = Normalize(Value(safeValues, "barcode")),
                        ReviewStatus = row.Errors.Count == 0 ? "PENDING" : "INVALID"
                    };
                    db.ImportStagingRecords.Add(staging);
                    if (row.Errors.Count != 0) { errors++; continue; }
                    valid++;
                    stagingRows.Add((staging, safeValues));
                }

                var matches = await MatchAsync(tenantId, session.ConnectionId, stagingRows.Select(row => row.Staging).ToList(), cancellationToken);
                // A category or brand the apply step cannot use is shown on the row before the decision.
                var previewCategories = await LeafCategoriesAsync(tenantId, stagingRows.Select(row => Value(row.Values, "category")), cancellationToken);
                var previewBrands = await BrandsAsync(tenantId, stagingRows.Select(row => Value(row.Values, "brand")), cancellationToken);
                foreach (var (staging, values) in stagingRows)
                {
                    var match = matches[staging.Id];
                    var warnings = new List<string>();
                    if (Value(values, "category") is { } categoryName && previewCategories.Resolve(categoryName, out var categoryWarning) is null && categoryWarning is not null) warnings.Add(categoryWarning);
                    if (Value(values, "brand") is { } brandName && Normalize(brandName) is { } brandKey && previewBrands.TryGetValue(brandKey, out var previewBrand) && !previewBrand.IsActive)
                        warnings.Add($"\"{brandName}\" markası kapalı; ürün markasız oluşturulur.");
                    db.ImportMatchCandidates.Add(new ImportMatchCandidate
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        SessionId = sessionId,
                        StagingRecordId = staging.Id,
                        ProductId = match.ProductId,
                        VariantId = match.VariantId,
                        MatchRule = match.Rule,
                        Status = "REVIEW_REQUIRED",
                        SafeSummary = SafeSummary(values, warnings)
                    });
                    reviews++;
                }
                await db.SaveChangesAsync(cancellationToken);
                DetachAllExcept(session);
            }

            session.TotalRows = total;
            session.ValidRows = valid;
            session.ErrorRows = errors;
            session.ReviewRows = reviews;
            ImportStateMachine.Transition(session, reviews == 0 ? ImportSessionStatus.ReadyToApply : ImportSessionStatus.ReviewRequired);
            session.UpdatedAt = timeProvider.GetUtcNow();
            session.Version++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await MarkFailedAsync(tenantId, sessionId);
            throw;
        }
    }

    private async Task<bool> ApplyAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.ImportSessions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sessionId, cancellationToken);
        if (session is null || session.Status != ImportSessionStatus.ReadyToApply) return false;
        ImportStateMachine.Transition(session, ImportSessionStatus.Applying);
        session.UpdatedAt = timeProvider.GetUtcNow();
        session.Version++;
        await db.SaveChangesAsync(cancellationToken);

        var failed = session.ErrorRows;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var rows = await (from candidate in db.ImportMatchCandidates.AsNoTracking()
                              join staging in db.ImportStagingRecords.AsNoTracking() on new { candidate.TenantId, Id = candidate.StagingRecordId } equals new { staging.TenantId, staging.Id }
                              join decision in db.ImportDecisions.AsNoTracking() on new { candidate.TenantId, Id = candidate.Id } equals new { decision.TenantId, Id = decision.CandidateId }
                              where candidate.TenantId == tenantId && candidate.SessionId == sessionId
                              orderby staging.RowNumber
                              select new { candidate, staging, decision }).ToListAsync(cancellationToken);
            var valuesByStaging = rows.ToDictionary(row => row.staging.Id, row => (IReadOnlyDictionary<string, string>)(JsonSerializer.Deserialize<Dictionary<string, string>>(row.staging.SafeValuesJson) ?? []));
            var createRows = rows.Where(row => row.decision.Decision == ImportDecisionKind.Create).ToList();

            // One lookup per chunk instead of one query per row: codes already in the catalog (in either
            // stored key form), brands and leaf categories named by the file, and the connection.
            var existingSkuKeys = await ExistingKeysAsync(tenantId, createRows.Select(row => Value(valuesByStaging[row.staging.Id], "sku")), barcode: false, cancellationToken);
            var existingBarcodeKeys = await ExistingKeysAsync(tenantId, createRows.Select(row => Value(valuesByStaging[row.staging.Id], "barcode")), barcode: true, cancellationToken);
            var brands = await BrandsAsync(tenantId, createRows.Select(row => Value(valuesByStaging[row.staging.Id], "brand")), cancellationToken);
            var categories = await LeafCategoriesAsync(tenantId, createRows.Select(row => Value(valuesByStaging[row.staging.Id], "category")), cancellationToken);
            var connection = session.ConnectionId is Guid connectionId
                ? await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken)
                : null;

            var appliedSkus = new HashSet<string>(StringComparer.Ordinal);
            var appliedBarcodes = new HashSet<string>(StringComparer.Ordinal);
            var productsByGroup = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var optionsByProduct = new Dictionary<(Guid ProductId, string Key), ProductOption>();
            var optionValuesByOption = new Dictionary<(Guid OptionId, string Key), ProductOptionValue>();
            if (!await db.InventoryLocations.AnyAsync(x => x.TenantId == tenantId && x.Code == "MAIN", cancellationToken))
                db.InventoryLocations.Add(new InventoryLocation { Id = Guid.CreateVersion7(), TenantId = tenantId, Code = "MAIN", Name = "Ana Depo", Status = "ACTIVE", Priority = 1 });
            var pending = 0;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.decision.Decision == ImportDecisionKind.Skip) continue;
                var values = valuesByStaging[row.staging.Id];
                if (row.decision.Decision == ImportDecisionKind.Link)
                {
                    if (row.decision.LinkProductId is null || row.decision.LinkVariantId is null) { failed++; continue; }
                    await AddExternalLinksAsync(session, row.staging, row.decision.LinkProductId.Value, row.decision.LinkVariantId.Value, cancellationToken);
                    AddProvenance(tenantId, sessionId, row.staging, row.decision.LinkProductId.Value, row.decision.LinkVariantId, values);
                    // A linked variant takes the file's desi only when it has none; a desi already set stays.
                    if (ImportedDesi.From(ParseDecimal(Value(values, "desi"))) is { } linkedDesi
                        && await db.ProductVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == row.decision.LinkVariantId.Value && x.Desi == null, cancellationToken) is { } linkedVariant)
                    {
                        linkedVariant.Desi = linkedDesi;
                        linkedVariant.UpdatedAt = timeProvider.GetUtcNow();
                        linkedVariant.Version++;
                    }
                }
                else
                {
                    var sku = Value(values, "sku")!;
                    var barcode = Value(values, "barcode");
                    var skuNormalized = Normalize(sku)!;
                    var barcodeNormalized = Normalize(barcode);
                    var duplicate = appliedSkus.Contains(skuNormalized) || existingSkuKeys.Contains(skuNormalized)
                        || (barcodeNormalized is not null && (appliedBarcodes.Contains(barcodeNormalized) || existingBarcodeKeys.Contains(barcodeNormalized)));
                    if (duplicate) { failed++; continue; }
                    appliedSkus.Add(skuNormalized); if (barcodeNormalized is not null) appliedBarcodes.Add(barcodeNormalized);

                    var now = timeProvider.GetUtcNow();
                    var listPrice = ParsePrice(Value(values, "listPrice"));
                    var salePrice = ParsePrice(Value(values, "salePrice"));
                    if (listPrice is decimal list && salePrice is decimal sale && list < sale) listPrice = sale;
                    var groupKey = Normalize(Value(values, "variantGroupKey")) ?? row.staging.RowHash;
                    if (!productsByGroup.TryGetValue(groupKey, out var productId))
                    {
                        var product = new Product
                        {
                            Id = Guid.CreateVersion7(),
                            TenantId = tenantId,
                            Title = Value(values, "title")!,
                            Description = Value(values, "description") ?? string.Empty,
                            BrandId = Value(values, "brand") is { } brandName ? BrandFor(tenantId, brands, brandName, now)?.Id : null,
                            CategoryId = categories.Resolve(Value(values, "category")),
                            DefaultListPrice = listPrice,
                            DefaultSalePrice = salePrice,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        productId = product.Id;
                        productsByGroup.Add(groupKey, productId);
                        db.Products.Add(product);
                    }
                    var options = ImportOptions(values);
                    var variant = new ProductVariant
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        ProductId = productId,
                        Sku = sku,
                        SkuNormalized = skuNormalized,
                        Barcode = barcode,
                        BarcodeNormalized = barcodeNormalized,
                        ModelCode = Value(values, "modelCode"),
                        Desi = ImportedDesi.From(ParseDecimal(Value(values, "desi"))),
                        OptionSignature = VariantOptionSignature.Build(options, empty: "-"),
                        DefaultListPrice = listPrice,
                        DefaultSalePrice = salePrice,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    db.ProductVariants.Add(variant);
                    AddOptions(tenantId, productId, variant.Id, options, optionsByProduct, optionValuesByOption);
                    var stock = ParseNonNegative(Value(values, "stock"));
                    var item = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenantId, VariantId = variant.Id, LocationCode = "MAIN", OnHand = stock, Reserved = 0, Available = stock };
                    db.InventoryItems.Add(item);
                    if (stock != 0)
                    {
                        db.StockLedgerEntries.Add(new StockLedgerEntry
                        {
                            Id = Guid.CreateVersion7(),
                            TenantId = tenantId,
                            InventoryItemId = item.Id,
                            MovementType = "IMPORT_OPENING",
                            QuantityDelta = stock,
                            SourceType = "IMPORT",
                            SourceId = sessionId.ToString("D"),
                            SourceEventId = row.staging.RowHash,
                            IdempotencyKey = $"import:{sessionId:D}:{row.staging.RowHash}",
                            OccurredAt = now,
                            RecordedAt = now,
                            CorrelationId = $"import-{sessionId:N}"
                        });
                    }
                    AddOffer(tenantId, connection, variant.Id, values, listPrice, salePrice, now);
                    await AddExternalLinksAsync(session, row.staging, productId, variant.Id, cancellationToken);
                    AddProvenance(tenantId, sessionId, row.staging, productId, variant.Id, values);
                }

                if (++pending >= BatchSize)
                {
                    // Products, options and values of a group can continue in the next batch; their ids stay
                    // valid after the entities are detached.
                    await db.SaveChangesAsync(cancellationToken);
                    DetachAllExcept(session);
                    pending = 0;
                }
            }

            ImportStateMachine.Transition(session, failed > 0 ? ImportSessionStatus.PartiallyCompleted : ImportSessionStatus.Completed);
            session.UpdatedAt = timeProvider.GetUtcNow();
            session.Version++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await MarkFailedAsync(tenantId, sessionId);
            throw;
        }
    }

    // The failing entities are still tracked after an exception; saving the session through the same change
    // tracker would repeat the failure and leave the session in Matching/Applying. Start from a clean tracker,
    // reload the session and persist Failed.
    private async Task MarkFailedAsync(Guid tenantId, Guid sessionId)
    {
        db.ChangeTracker.Clear();
        var session = await db.ImportSessions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sessionId, CancellationToken.None);
        if (session is null || !ImportStateMachine.CanTransition(session.Status, ImportSessionStatus.Failed)) return;
        ImportStateMachine.Transition(session, ImportSessionStatus.Failed);
        session.UpdatedAt = timeProvider.GetUtcNow();
        session.Version++;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private void DetachAllExcept(ImportSession session)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => !ReferenceEquals(entry.Entity, session)).ToList())
            entry.State = EntityState.Detached;
    }

    private async Task<Dictionary<Guid, MatchResult>> MatchAsync(Guid tenantId, Guid? connectionId, IReadOnlyList<ImportStagingRecord> rows, CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, MatchResult>();
        if (rows.Count == 0) return results;
        var linkedVariants = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var aliasedVariants = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (connectionId is Guid connection)
        {
            var externalIds = rows.Select(row => row.ExternalRecordId).OfType<string>().Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var chunk in externalIds.Chunk(LookupChunk))
            {
                foreach (var link in await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connection && chunk.Contains(x.ExternalId)).Select(x => new { x.ExternalId, x.VariantId }).ToListAsync(cancellationToken))
                    linkedVariants.TryAdd(link.ExternalId, link.VariantId);
                foreach (var alias in await db.ExternalIdentifierAliases.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connection && x.EntityType == "VARIANT" && chunk.Contains(x.ExternalId)).Select(x => new { x.ExternalId, x.LocalId }).ToListAsync(cancellationToken))
                    aliasedVariants.TryAdd(alias.ExternalId, alias.LocalId);
            }
        }

        // Look up the codes as typed in the file: the stored legacy forms can only be derived from the raw value,
        // not from the shared key kept on the staging row.
        var rawValues = rows.ToDictionary(row => row.Id, row => (IReadOnlyDictionary<string, string>)(JsonSerializer.Deserialize<Dictionary<string, string>>(row.SafeValuesJson) ?? []));
        var byBarcode = await VariantsByKeyAsync(tenantId, rows.Select(row => Value(rawValues[row.Id], "barcode")), barcode: true, cancellationToken);
        var bySku = await VariantsByKeyAsync(tenantId, rows.Select(row => Value(rawValues[row.Id], "sku")), barcode: false, cancellationToken);
        var referencedIds = linkedVariants.Values.Concat(aliasedVariants.Values).Distinct().ToArray();
        var productByVariant = new Dictionary<Guid, Guid>();
        foreach (var chunk in referencedIds.Chunk(LookupChunk))
            foreach (var variant in await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && chunk.Contains(x.Id)).Select(x => new { x.Id, x.ProductId }).ToListAsync(cancellationToken))
                productByVariant[variant.Id] = variant.ProductId;

        foreach (var row in rows)
        {
            if (row.ExternalRecordId is { Length: > 0 } externalId)
            {
                if (linkedVariants.TryGetValue(externalId, out var linked) && productByVariant.TryGetValue(linked, out var linkedProduct)) { results[row.Id] = new("EXISTING_LINK", linkedProduct, linked); continue; }
                if (aliasedVariants.TryGetValue(externalId, out var aliased) && productByVariant.TryGetValue(aliased, out var aliasedProduct)) { results[row.Id] = new("EXTERNAL_ID_ALIAS", aliasedProduct, aliased); continue; }
            }
            if (row.BarcodeNormalized is { Length: > 0 } barcode && byBarcode.TryGetValue(barcode, out var barcodeMatches))
            {
                results[row.Id] = barcodeMatches.Count == 1 ? new("UNIQUE_BARCODE", barcodeMatches[0].ProductId, barcodeMatches[0].Id) : new("BARCODE_CONFLICT", null, null);
                continue;
            }
            if (row.SkuNormalized is { Length: > 0 } sku && bySku.TryGetValue(sku, out var skuMatches))
            {
                results[row.Id] = skuMatches.Count == 1 ? new("UNIQUE_SKU", skuMatches[0].ProductId, skuMatches[0].Id) : new("SKU_CONFLICT", null, null);
                continue;
            }
            results[row.Id] = new("NEW", null, null);
        }
        return results;
    }

    // Catalog variants grouped by the shared key of their stored code (either stored form).
    private async Task<Dictionary<string, List<(Guid Id, Guid ProductId)>>> VariantsByKeyAsync(Guid tenantId, IEnumerable<string?> keys, bool barcode, CancellationToken cancellationToken)
    {
        var lookup = CatalogKeyNormalizer.LookupKeys(keys);
        var result = new Dictionary<string, List<(Guid, Guid)>>(StringComparer.Ordinal);
        foreach (var chunk in lookup.Chunk(LookupChunk))
        {
            var rows = barcode
                ? await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.BarcodeNormalized != null && chunk.Contains(x.BarcodeNormalized)).Select(x => new { x.Id, x.ProductId, Code = x.BarcodeNormalized! }).ToListAsync(cancellationToken)
                : await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && chunk.Contains(x.SkuNormalized)).Select(x => new { x.Id, x.ProductId, Code = x.SkuNormalized }).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                var key = CatalogKeyNormalizer.Key(row.Code);
                if (!result.TryGetValue(key, out var list)) result[key] = list = [];
                if (!list.Any(item => item.Item1 == row.Id)) list.Add((row.Id, row.ProductId));
            }
        }
        return result;
    }

    private async Task<HashSet<string>> ExistingKeysAsync(Guid tenantId, IEnumerable<string?> values, bool barcode, CancellationToken cancellationToken) =>
        (await VariantsByKeyAsync(tenantId, values, barcode, cancellationToken)).Keys.ToHashSet(StringComparer.Ordinal);

    private async Task<Dictionary<string, Brand>> BrandsAsync(Guid tenantId, IEnumerable<string?> names, CancellationToken cancellationToken)
    {
        var keys = CatalogKeyNormalizer.LookupKeys(names);
        var brands = keys.Length == 0 ? [] : await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && keys.Contains(x.NormalizedName)).ToListAsync(cancellationToken);
        return brands.OrderByDescending(x => x.IsActive).GroupBy(x => CatalogKeyNormalizer.Key(x.NormalizedName)).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    // A brand named by the file is reused when it exists and created otherwise. Like the marketplace
    // import, a brand the user switched off is not revived: the product stays without a brand.
    private Brand? BrandFor(Guid tenantId, Dictionary<string, Brand> brands, string name, DateTimeOffset now)
    {
        var key = Normalize(name);
        if (key is null) return null;
        if (brands.TryGetValue(key, out var brand)) return brand.IsActive ? brand : null;
        brand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = name.Trim()[..Math.Min(name.Trim().Length, 160)], NormalizedName = key, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Brands.Add(brand);
        brands[key] = brand;
        return brand;
    }

    // Categories are a managed tree, so the file may only name an existing active leaf. Categories made
    // in the panel store a name key, marketplace categories a path key ("GIYIM-ALT-GIYIM-TAYT"); the file
    // may give the leaf name ("Tayt") or the full path ("Giyim / Alt Giyim / Tayt"), so both are matched
    // against the stored name and path, the full path first.
    private async Task<ImportCategoryLookup> LeafCategoriesAsync(Guid tenantId, IEnumerable<string?> names, CancellationToken cancellationToken)
    {
        var requested = names.ToList();
        if (requested.All(string.IsNullOrWhiteSpace)) return ImportCategoryLookup.Build([], requested);
        var categories = await db.Categories.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && x.IsLeaf).Select(x => new ImportCategoryLookup.Leaf(x.Id, x.Name, x.Path, x.NormalizedName)).ToListAsync(cancellationToken);
        return ImportCategoryLookup.Build(categories, requested);
    }

    // The active leaf categories a file names, indexed by the shared key of their full path and of their
    // name (the stored NormalizedName too, which is a name key for panel categories and a path key for
    // marketplace ones).
    internal sealed record ImportCategoryLookup(Dictionary<string, HashSet<Guid>> ByPath, Dictionary<string, HashSet<Guid>> ByName)
    {
        internal sealed record Leaf(Guid Id, string Name, string Path, string NormalizedName);

        public static ImportCategoryLookup Build(IEnumerable<Leaf> leaves, IEnumerable<string?> names)
        {
            var requested = names.Select(name => CatalogKeyNormalizer.Key(name, 1024)).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
            var byPath = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
            var byName = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
            void Add(Dictionary<string, HashSet<Guid>> index, string key, Guid id)
            {
                if (!requested.Contains(key)) return;
                if (!index.TryGetValue(key, out var ids)) index[key] = ids = [];
                ids.Add(id);
            }
            foreach (var leaf in leaves)
            {
                Add(byPath, CatalogKeyNormalizer.Key(leaf.Path, 1024), leaf.Id);
                Add(byName, CatalogKeyNormalizer.Key(leaf.Name), leaf.Id);
                Add(byName, CatalogKeyNormalizer.Key(leaf.NormalizedName), leaf.Id);
            }
            return new(byPath, byName);
        }

        public Guid? Resolve(string? value) => Resolve(value, out _);

        // The single active leaf the file names, or null with the reason shown on the preview row.
        public Guid? Resolve(string? value, out string? warning)
        {
            warning = null;
            var key = CatalogKeyNormalizer.Key(value, 1024);
            if (key.Length == 0) return null;
            if (ByPath.TryGetValue(key, out var pathIds) && pathIds.Count == 1) return pathIds.Single();
            if (ByName.TryGetValue(key, out var nameIds) && nameIds.Count == 1 && (pathIds is null || pathIds.SetEquals(nameIds))) return nameIds.Single();
            var candidates = (pathIds?.Count ?? 0) + (nameIds?.Count ?? 0);
            warning = candidates > 1
                ? $"\"{value!.Trim()}\" birden fazla kategoriyle eşleşiyor; ürün kategorisiz oluşturulur. Tam kategori yolunu yazın (ör. Giyim / Alt Giyim / Tayt)."
                : $"\"{value!.Trim()}\" adında etkin bir yaprak kategori yok; ürün kategorisiz oluşturulur.";
            return null;
        }
    }

    private static Dictionary<string, string> ImportOptions(IReadOnlyDictionary<string, string> values)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Value(values, "color") is { } color) options["Renk"] = color;
        if (Value(values, "size") is { } size) options["Beden"] = size;
        return options;
    }

    private void AddOptions(Guid tenantId, Guid productId, Guid variantId, IReadOnlyDictionary<string, string> options, Dictionary<(Guid, string), ProductOption> optionsByProduct, Dictionary<(Guid, string), ProductOptionValue> valuesByOption)
    {
        var order = 0;
        foreach (var (label, valueLabel) in options)
        {
            var optionKey = CatalogKeyNormalizer.Key(label);
            if (!optionsByProduct.TryGetValue((productId, optionKey), out var option))
            {
                option = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = productId, Label = label, NormalizedKey = optionKey, SortOrder = order };
                db.ProductOptions.Add(option);
                optionsByProduct[(productId, optionKey)] = option;
            }
            var valueKey = CatalogKeyNormalizer.Key(valueLabel);
            if (!valuesByOption.TryGetValue((option.Id, valueKey), out var value))
            {
                value = new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenantId, OptionId = option.Id, Label = valueLabel[..Math.Min(valueLabel.Length, 160)], NormalizedKey = valueKey, SortOrder = valuesByOption.Keys.Count(key => key.Item1 == option.Id) };
                db.ProductOptionValues.Add(value);
                valuesByOption[(option.Id, valueKey)] = value;
            }
            db.VariantOptionValues.Add(new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenantId, VariantId = variantId, OptionId = option.Id, OptionValueId = value.Id });
            order++;
        }
    }

    // An import for a connection also seeds the offer on that connection. VAT comes from the file or from
    // the connection default (K26); without either the offer is not created rather than sent with 0 % VAT.
    private void AddOffer(Guid tenantId, PlatformConnection? connection, Guid variantId, IReadOnlyDictionary<string, string> values, decimal? listPrice, decimal? salePrice, DateTimeOffset now)
    {
        if (connection is null || salePrice is not > 0) return;
        var vatRate = ParsePrice(Value(values, "vatRate")) ?? ConnectionDefaults.VatRate(connection.SettingsJson);
        if (vatRate is null) return;
        var currency = Value(values, "currency")?.ToUpperInvariant() is { Length: 3 } code ? code : "TRY";
        var offer = new ChannelOffer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connection.Id,
            VariantId = variantId,
            ListPrice = Math.Max(listPrice ?? salePrice.Value, salePrice.Value),
            SalePrice = salePrice.Value,
            Currency = currency,
            VatRate = vatRate.Value,
            VatInclusion = "INCLUDED",
            RoundingMode = "HALF_EVEN",
            SafetyStock = 0,
            Status = "ACTIVE",
            PriceVersion = 1,
            Version = 1
        };
        db.ChannelOffers.Add(offer);
        db.ChannelPriceHistory.Add(new ChannelPriceHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OfferId = offer.Id, PriceVersion = 1, ListPrice = offer.ListPrice, SalePrice = offer.SalePrice, Currency = offer.Currency, Reason = "IMPORT", ActorSource = "SYSTEM:IMPORT", EffectiveAt = now });
    }

    private async Task AddExternalLinksAsync(ImportSession session, ImportStagingRecord staging, Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        if (session.ConnectionId is not Guid connectionId || staging.ExternalRecordId is not { Length: > 0 } externalId) return;
        if (!db.MarketplaceVariantLinks.Local.Any(x => x.ConnectionId == connectionId && x.ExternalId == externalId) && !await db.MarketplaceVariantLinks.AnyAsync(x => x.TenantId == session.TenantId && x.ConnectionId == connectionId && x.ExternalId == externalId, cancellationToken))
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = session.TenantId, ConnectionId = connectionId, VariantId = variantId, ExternalId = externalId });
        if (!db.MarketplaceProductLinks.Local.Any(x => x.ConnectionId == connectionId && x.ProductId == productId) && !await db.MarketplaceProductLinks.AnyAsync(x => x.TenantId == session.TenantId && x.ConnectionId == connectionId && x.ProductId == productId, cancellationToken))
            db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = session.TenantId, ConnectionId = connectionId, ProductId = productId, ExternalId = externalId });
    }

    // Each staging record is applied once per session, so its provenance rows are new.
    private void AddProvenance(Guid tenantId, Guid sessionId, ImportStagingRecord staging, Guid productId, Guid? variantId, IReadOnlyDictionary<string, string> values)
    {
        foreach (var field in new[] { "title", "sku", "barcode" })
        {
            if (!values.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            db.FieldProvenance.Add(new FieldProvenance
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                SessionId = sessionId,
                ProductId = productId,
                VariantId = field == "title" ? null : variantId,
                FieldName = field,
                StagingRecordId = staging.Id,
                ValueHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))),
                AppliedAt = timeProvider.GetUtcNow()
            });
        }
    }

    private static decimal ParseNonNegative(string? value) => ParseDecimal(value) is decimal parsed ? Math.Max(0, decimal.Round(parsed, 4)) : 0;
    private static decimal? ParsePrice(string? value) => ParseDecimal(value) is decimal parsed && parsed >= 0 ? decimal.Round(parsed, 4) : null;

    // Accepts "1234.5", "1234,5", "1.234,50" and "1,234.50". A CSV "1.250" never gets here: ImportFileReader refuses
    // the row; an XLSX number cell is written with a decimal point.
    internal static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().Replace(" ", "", StringComparison.Ordinal);
        var lastComma = text.LastIndexOf(',');
        var lastDot = text.LastIndexOf('.');
        if (lastComma > lastDot) text = text.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        else if (lastDot > lastComma && lastComma >= 0) text = text.Replace(",", "", StringComparison.Ordinal);
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string? Normalize(string? value) => CatalogKeyNormalizer.Key(value) is { Length: > 0 } key ? key : null;
    private static string? Value(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    private static IReadOnlyDictionary<string, string> SanitizeImportedValues(IReadOnlyDictionary<string, string> values)
    {
        var safeValues = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        if (safeValues.TryGetValue("description", out var description)) safeValues["description"] = ImportedHtmlSanitizer.Sanitize(description);
        return safeValues;
    }
    private static string SafeSummary(IReadOnlyDictionary<string, string> values, IReadOnlyList<string> warnings) => warnings.Count == 0
        ? JsonSerializer.Serialize(new { title = Value(values, "title"), sku = Value(values, "sku"), barcode = Value(values, "barcode") })
        : JsonSerializer.Serialize(new { title = Value(values, "title"), sku = Value(values, "sku"), barcode = Value(values, "barcode"), warnings });
    private sealed record MatchResult(string Rule, Guid? ProductId, Guid? VariantId);
}
