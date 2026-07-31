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
            await using var source = await files.OpenReadAsync(tenantId, asset.RelativePath, cancellationToken);
            var parsed = session.SourceType == ImportSourceType.Csv
                ? ImportFileReader.ReadCsv(source, mappings)
                : ImportFileReader.ReadXlsx(source, mappings, _maximumBytes);

            var oldCandidates = await db.ImportMatchCandidates.Where(x => x.TenantId == tenantId && x.SessionId == sessionId).ToListAsync(cancellationToken);
            var oldStaging = await db.ImportStagingRecords.Where(x => x.TenantId == tenantId && x.SessionId == sessionId).ToListAsync(cancellationToken);
            db.ImportMatchCandidates.RemoveRange(oldCandidates);
            db.ImportStagingRecords.RemoveRange(oldStaging);

            var total = 0;
            var valid = 0;
            var errors = 0;
            var reviews = 0;
            foreach (var row in parsed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;
                var safeJson = JsonSerializer.Serialize(row.Values);
                var rowHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(safeJson)));
                var staging = new ImportStagingRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    SessionId = sessionId,
                    RowNumber = row.RowNumber,
                    ExternalRecordId = Value(row.Values, "externalId"),
                    RawJson = safeJson,
                    SafeValuesJson = safeJson,
                    ValidationErrorsJson = JsonSerializer.Serialize(row.Errors),
                    RowHash = rowHash,
                    SkuNormalized = Normalize(Value(row.Values, "sku")),
                    BarcodeNormalized = Normalize(Value(row.Values, "barcode")),
                    ReviewStatus = row.Errors.Count == 0 ? "PENDING" : "INVALID"
                };
                db.ImportStagingRecords.Add(staging);
                if (row.Errors.Count != 0) { errors++; continue; }

                valid++;
                var match = await MatchAsync(tenantId, session.ConnectionId, staging, cancellationToken);
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
                    SafeSummary = SafeSummary(row.Values)
                });
                reviews++;
            }

            session.TotalRows = total;
            session.ValidRows = valid;
            session.ErrorRows = errors;
            session.ReviewRows = reviews;
            ImportStateMachine.Transition(session, reviews == 0 ? ImportSessionStatus.ReadyToApply : ImportSessionStatus.ReviewRequired);
            session.UpdatedAt = timeProvider.GetUtcNow();
            session.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch
        {
            ImportStateMachine.Transition(session, ImportSessionStatus.Failed);
            session.UpdatedAt = timeProvider.GetUtcNow();
            session.Version++;
            await db.SaveChangesAsync(CancellationToken.None);
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
            var rows = await (from candidate in db.ImportMatchCandidates
                              join staging in db.ImportStagingRecords on new { candidate.TenantId, Id = candidate.StagingRecordId } equals new { staging.TenantId, staging.Id }
                              join decision in db.ImportDecisions on new { candidate.TenantId, Id = candidate.Id } equals new { decision.TenantId, Id = decision.CandidateId }
                              where candidate.TenantId == tenantId && candidate.SessionId == sessionId
                              orderby staging.RowNumber
                              select new { candidate, staging, decision }).ToListAsync(cancellationToken);

            var appliedSkus = new HashSet<string>(StringComparer.Ordinal);
            var appliedBarcodes = new HashSet<string>(StringComparer.Ordinal);
            var productsByGroup = new Dictionary<string, Product>(StringComparer.Ordinal);
            if (!await db.InventoryLocations.AnyAsync(x => x.TenantId == tenantId && x.Code == "MAIN", cancellationToken))
                db.InventoryLocations.Add(new InventoryLocation { Id = Guid.CreateVersion7(), TenantId = tenantId, Code = "MAIN", Name = "Ana Depo", Status = "ACTIVE", Priority = 1 });
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.decision.Decision == ImportDecisionKind.Skip) continue;
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.staging.SafeValuesJson) ?? [];
                if (row.decision.Decision == ImportDecisionKind.Link)
                {
                    if (row.decision.LinkProductId is null || row.decision.LinkVariantId is null) { failed++; continue; }
                    await AddExternalLinksAsync(session, row.staging, row.decision.LinkProductId.Value, row.decision.LinkVariantId.Value, cancellationToken);
                    await AddProvenanceAsync(tenantId, sessionId, row.staging, row.decision.LinkProductId.Value, row.decision.LinkVariantId, values, cancellationToken);
                    continue;
                }

                var sku = Value(values, "sku")!;
                var barcode = Value(values, "barcode");
                var skuNormalized = Normalize(sku)!;
                var barcodeNormalized = Normalize(barcode);
                var duplicate = appliedSkus.Contains(skuNormalized) || (barcodeNormalized is not null && appliedBarcodes.Contains(barcodeNormalized)) || await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId &&
                    (x.SkuNormalized == skuNormalized || (barcodeNormalized != null && x.BarcodeNormalized == barcodeNormalized)), cancellationToken);
                if (duplicate) { failed++; continue; }
                appliedSkus.Add(skuNormalized); if (barcodeNormalized is not null) appliedBarcodes.Add(barcodeNormalized);

                var now = timeProvider.GetUtcNow();
                var groupKey = Normalize(Value(values, "variantGroupKey")) ?? row.staging.RowHash;
                if (!productsByGroup.TryGetValue(groupKey, out var product))
                {
                    product = new Product
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        Title = Value(values, "title")!,
                        Description = Value(values, "description") ?? string.Empty,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    productsByGroup.Add(groupKey, product);
                    db.Products.Add(product);
                }
                var variant = new ProductVariant
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ProductId = product.Id,
                    Sku = sku,
                    SkuNormalized = skuNormalized,
                    Barcode = barcode,
                    BarcodeNormalized = barcodeNormalized,
                    ModelCode = Value(values, "modelCode"),
                    OptionSignature = "DEFAULT",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.ProductVariants.Add(variant);
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
                await AddExternalLinksAsync(session, row.staging, product.Id, variant.Id, cancellationToken);
                await AddProvenanceAsync(tenantId, sessionId, row.staging, product.Id, variant.Id, values, cancellationToken);
            }

            ImportStateMachine.Transition(session, failed > 0 ? ImportSessionStatus.PartiallyCompleted : ImportSessionStatus.Completed);
            session.UpdatedAt = timeProvider.GetUtcNow();
            session.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch
        {
            ImportStateMachine.Transition(session, ImportSessionStatus.Failed);
            session.UpdatedAt = timeProvider.GetUtcNow();
            session.Version++;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<MatchResult> MatchAsync(Guid tenantId, Guid? connectionId, ImportStagingRecord row, CancellationToken cancellationToken)
    {
        if (connectionId is Guid connection && row.ExternalRecordId is { Length: > 0 } externalId)
        {
            var link = await db.MarketplaceVariantLinks.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connection && x.ExternalId == externalId, cancellationToken);
            if (link is not null)
            {
                var productId = await db.ProductVariants.Where(x => x.TenantId == tenantId && x.Id == link.VariantId).Select(x => x.ProductId).SingleAsync(cancellationToken);
                return new("EXISTING_LINK", productId, link.VariantId);
            }
            var alias = await db.ExternalIdentifierAliases.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connection && x.ExternalId == externalId && x.EntityType == "VARIANT", cancellationToken);
            if (alias is not null)
            {
                var productId = await db.ProductVariants.Where(x => x.TenantId == tenantId && x.Id == alias.LocalId).Select(x => x.ProductId).SingleAsync(cancellationToken);
                return new("EXTERNAL_ID_ALIAS", productId, alias.LocalId);
            }
        }

        if (row.BarcodeNormalized is { Length: > 0 } barcode)
        {
            var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.BarcodeNormalized == barcode).Take(2).ToListAsync(cancellationToken);
            if (variants.Count == 1) return new("UNIQUE_BARCODE", variants[0].ProductId, variants[0].Id);
            if (variants.Count > 1) return new("BARCODE_CONFLICT", null, null);
        }
        if (row.SkuNormalized is { Length: > 0 } sku)
        {
            var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.SkuNormalized == sku).Take(2).ToListAsync(cancellationToken);
            if (variants.Count == 1) return new("UNIQUE_SKU", variants[0].ProductId, variants[0].Id);
            if (variants.Count > 1) return new("SKU_CONFLICT", null, null);
        }
        return new("NEW", null, null);
    }

    private async Task AddExternalLinksAsync(ImportSession session, ImportStagingRecord staging, Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        if (session.ConnectionId is not Guid connectionId || staging.ExternalRecordId is not { Length: > 0 } externalId) return;
        if (!await db.MarketplaceVariantLinks.AnyAsync(x => x.TenantId == session.TenantId && x.ConnectionId == connectionId && x.ExternalId == externalId, cancellationToken))
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = session.TenantId, ConnectionId = connectionId, VariantId = variantId, ExternalId = externalId });
        if (!await db.MarketplaceProductLinks.AnyAsync(x => x.TenantId == session.TenantId && x.ConnectionId == connectionId && x.ProductId == productId, cancellationToken))
            db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = session.TenantId, ConnectionId = connectionId, ProductId = productId, ExternalId = externalId });
    }

    private async Task AddProvenanceAsync(Guid tenantId, Guid sessionId, ImportStagingRecord staging, Guid productId, Guid? variantId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        foreach (var field in new[] { "title", "sku", "barcode" })
        {
            if (!values.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            if (await db.FieldProvenance.AnyAsync(x => x.TenantId == tenantId && x.SessionId == sessionId && x.StagingRecordId == staging.Id && x.FieldName == field, cancellationToken)) continue;
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

    private static decimal ParseNonNegative(string? value) => decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? Math.Max(0, decimal.Round(parsed, 4)) : 0;
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    private static string? Value(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    private static string SafeSummary(IReadOnlyDictionary<string, string> values) => JsonSerializer.Serialize(new { title = Value(values, "title"), sku = Value(values, "sku"), barcode = Value(values, "barcode") });
    private sealed record MatchResult(string Rule, Guid? ProductId, Guid? VariantId);
}
