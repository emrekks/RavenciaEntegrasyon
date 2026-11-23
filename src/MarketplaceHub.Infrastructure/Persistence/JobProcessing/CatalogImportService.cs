using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CategoryAttributeContext = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.CategoryAttributeContext;
using LocalCategoryAttribute = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.LocalCategoryAttribute;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 2 (16 Nov 2026): the catalog import, moved out of the job processor as it was: the product
// sync (SyncProducts with its model, variant, option, offer and link upserts and its retries), the catalog structure
// import, and the product and variant link writes. It works for one run of one job (run) on the job's AppDbContext,
// with the run's catalog attribute service, variant link bookkeeping and order ingestion (for the platform code and the
// catalog stock source); the processor's methods of the same names call into it. Product pulls only read the
// marketplace; brands and categories are mapped by the user, never looked up by name.
internal sealed partial class CatalogImportService(
    AppDbContext db,
    IssueRecorder issues,
    SyncCursorStore cursors,
    IProductPort products,
    TimeProvider timeProvider,
    ILogger? logger,
    CatalogAttributeService catalogAttributes,
    VariantLinkBookkeeping variantLinks,
    OrderIngestionService orderIngestion,
    JobRunContext run)
{
    public JobRunContext Run => run;

    private enum FocusedPriceStockOutcome { NotInPanel, Conflict, Unchanged, Changed }

    // What the moved code used from the processor: the same classes and helpers the processor calls.
    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken)
    {
        foreach (var open in openListingIssueKeys.Values) if (ListingIssuePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))) open.Add(key);
        return issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);
    }

    private Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) =>
        issues.ResolveAsync(tenantId, key, timeProvider.GetUtcNow(), cancellationToken);

    // Y5 review (23 Nov 2026): a per-listing check that finds its warning's condition gone resolves the warning under the
    // writer's own key. The open keys of those checks are read once per connection (a run reads thousands of listings and
    // holds a few hundred open warnings), so a listing without one costs no query. A key missing from the set is left as
    // it is (it stays open until a later run), never resolved by guess.
    private static readonly string[] ListingIssuePrefixes = ["product-sync-barcode-conflict:", "product-mapping-unmatched:"];
    private readonly Dictionary<Guid, HashSet<string>> openListingIssueKeys = [];

    private async Task ResolveOpenListingIssue(Guid tenantId, Guid connectionId, string key, CancellationToken cancellationToken, Func<OperationalIssue, bool>? onlyIf = null)
    {
        if (!openListingIssueKeys.TryGetValue(connectionId, out var open))
        {
            var conflicts = $"{ListingIssuePrefixes[0]}{connectionId}:";
            var unmatched = $"{ListingIssuePrefixes[1]}{connectionId}:";
            open = (await db.OperationalIssues.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && (x.DedupeKey.StartsWith(conflicts) || x.DedupeKey.StartsWith(unmatched)))
                    .Select(x => x.DedupeKey)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
            openListingIssueKeys[connectionId] = open;
        }
        if (!open.Contains(key)) return;
        if (await issues.ResolveIfOpenAsync(tenantId, key, timeProvider, cancellationToken, onlyIf)) open.Remove(key);
    }

    private Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) =>
        cursors.GetAsync(tenantId, connectionId, resource, cancellationToken);

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));

    private void TrackRequest() => run.TrackRequest();

    private void TrackResultFailure(AdapterError? error) => run.TrackResultFailure(error);

    private static string Hash(string value) => TextValues.Sha256Hex(value);

    private static bool ReadBoolean(string payloadJson, string propertyName) => JobPayloadReader.Boolean(payloadJson, propertyName);

    private static bool ReadBooleanOrDefault(string payloadJson, string propertyName, bool fallback) => JobPayloadReader.BooleanOrDefault(payloadJson, propertyName, fallback);

    private static string? ReadText(string payloadJson, string propertyName) => JobPayloadReader.Text(payloadJson, propertyName);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

    private static StockLedgerSource CatalogStockSource(string movementType, Guid connectionId, string eventId, DateTimeOffset occurredAt, DateTimeOffset now) =>
        OrderIngestionService.CatalogStockSource(movementType, connectionId, eventId, occurredAt, now);

    // The catalog attribute machinery and the variant link bookkeeping, by the names the moved code calls them (the
    // option-key and catalog-key helpers are in the moved ProductImportOptions part).
    private Task<ReferenceSnapshot?> EnsureReferenceSnapshot(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken) =>
        catalogAttributes.EnsureReferenceSnapshot(tenantId, connectionId, resourceType, parentExternalId, correlationId, cancellationToken);

    private Task<CategoryAttributeContext?> EnsureCategoryAttributeContext(Guid tenantId, Guid connectionId, ReferenceSnapshot categorySnapshot, RemoteCatalogProduct snapshot, IReadOnlyList<ReferenceItem> categoryItems, IDictionary<string, AttributeDefinition> importedAttributeLibrary, IDictionary<string, CategoryAttributeContext> cache, string correlationId, CancellationToken cancellationToken) =>
        catalogAttributes.EnsureCategoryAttributeContext(tenantId, connectionId, categorySnapshot, snapshot, categoryItems, importedAttributeLibrary, cache, correlationId, cancellationToken);

    private Task EnsureObservedVariantAttributeValues(Guid tenantId, CategoryAttributeContext categoryContext, RemoteCatalogProduct snapshot, CancellationToken cancellationToken) =>
        catalogAttributes.EnsureObservedVariantAttributeValues(tenantId, categoryContext, snapshot, cancellationToken);

    private Task<LocalCategoryAttribute> EnsureMappedAttribute(Guid tenantId, Guid connectionId, Category category, ReferenceItem remoteCategory, ReferenceSnapshot attributeSnapshot, ReferenceItem remoteAttribute, IReadOnlyList<ReferenceItem> values, IDictionary<string, AttributeDefinition> importedAttributeLibrary, CancellationToken cancellationToken, string codePrefix = "TRD") =>
        catalogAttributes.EnsureMappedAttribute(tenantId, connectionId, category, remoteCategory, attributeSnapshot, remoteAttribute, values, importedAttributeLibrary, cancellationToken, codePrefix);

    private Task EnsureExactWebColorValueMappings(Guid tenantId, Guid connectionId, string categoryExternalId, AttributeDefinition localAttribute, ReferenceItem remoteAttribute, IReadOnlyList<ReferenceItem> remoteValues, CategoryAttributeContext categoryContext, CancellationToken cancellationToken) =>
        catalogAttributes.EnsureExactWebColorValueMappings(tenantId, connectionId, categoryExternalId, localAttribute, remoteAttribute, remoteValues, categoryContext, cancellationToken);

    private Task EnsureExactWebColorValueMappings(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, CancellationToken cancellationToken) =>
        catalogAttributes.EnsureExactWebColorValueMappings(tenantId, connectionId, categoryContext, cancellationToken);

    private Task UpsertProductAttributeAssignments(Guid tenantId, Guid connectionId, Product product, ProductVariant variant, IReadOnlyDictionary<string, string> options, CategoryAttributeContext categoryContext, CancellationToken cancellationToken) =>
        catalogAttributes.UpsertProductAttributeAssignments(tenantId, connectionId, product, variant, options, categoryContext, cancellationToken);

    private Task PromoteCommonImportedAttributes(Guid tenantId, Product product, IReadOnlyList<ProductVariant> importedVariants, CategoryAttributeContext categoryContext, CancellationToken cancellationToken) =>
        catalogAttributes.PromoteCommonImportedAttributes(tenantId, product, importedVariants, categoryContext, cancellationToken);

    private Task<AttributeValue?> ResolveMappedAttributeValue(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, LocalCategoryAttribute mapped, ReferenceItem? remoteValue, string remoteValueText, CancellationToken cancellationToken) =>
        catalogAttributes.ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, mapped, remoteValue, remoteValueText, cancellationToken);

    private Task<bool> ExternalIdTaken(Guid tenantId, Guid connectionId, string externalId, Guid? exceptLinkId, CancellationToken cancellationToken) =>
        variantLinks.ExternalIdTaken(tenantId, connectionId, externalId, exceptLinkId, cancellationToken);

    private Task RekeyHepsiburadaVariantLinks(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken) =>
        variantLinks.RekeyHepsiburadaVariantLinks(tenantId, connectionId, snapshots, cancellationToken);

    private Task MarkVariantLinksSeen(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> page, CancellationToken cancellationToken, bool heal = true) =>
        variantLinks.MarkVariantLinksSeen(tenantId, connectionId, page, cancellationToken, heal);

    private Task MarkMissingVariantLinks(Guid tenantId, Guid connectionId, string platformCode, DateTimeOffset scanStartedAt, CancellationToken cancellationToken) =>
        variantLinks.MarkMissingVariantLinks(tenantId, connectionId, platformCode, scanStartedAt, cancellationToken);

    private Task RecordVariantLinkCodes(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken, bool onlyAdded = false) =>
        variantLinks.RecordVariantLinkCodes(tenantId, connectionId, snapshots, cancellationToken, onlyAdded);

    private static string MarketplacePlatformName(string? platformCode) => VariantLinkBookkeeping.MarketplacePlatformName(platformCode);

    private static (string? State, string? Reason) PttAvmModelSaleState(RemoteCatalogProduct product) => ListingVerificationService.PttAvmModelSaleState(product);

    // A product the catalog read pulled: its model code (or the marketplace's product id), its first barcode and how many
    // variants came with it. A failed batch marks each of its products with the error.
    private void RecordProductRead(RemoteCatalogProduct snapshot, string outcome, string? reason)
    {
        if (!run.ReadRecordingEnabled || run.ReadRecordingKind != "PRODUCT") return;
        var reference = string.IsNullOrWhiteSpace(snapshot.ProductMainId) ? snapshot.ExternalProductId : snapshot.ProductMainId!;
        var barcode = snapshot.Variants.Select(x => x.Barcode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var secondary = barcode is null ? null : snapshot.Variants.Count > 1 ? $"{barcode} +{snapshot.Variants.Count - 1}" : barcode;
        run.ReadRecorder.Add("PRODUCT", new JobReadRecord(reference, secondary, $"{snapshot.Variants.Count} varyant", outcome, reason));
    }

    // An offer the import seeded from PTT AVM already holds PTT AVM's price and stock: it is marked as sent, so the
    // first price/stock send carries only what changed in the panel instead of the whole catalog.
    private async Task MarkPttAvmOfferInSyncAsync(Guid tenantId, ChannelOffer offer, RemoteCatalogVariant remote, decimal salePrice, decimal listPrice, CancellationToken cancellationToken)
    {
        var changed = false;
        if (offer.LastPriceHash is null && offer.PriceVersion == 1 && offer.SalePrice == salePrice && offer.ListPrice == listPrice)
        {
            offer.LastPriceHash = Adapters.PttAvm.PttAvmPriceInventoryRules.PriceHash(
                Adapters.PttAvm.PttAvmPriceInventoryRules.Price(PriceInventoryComposer.OutboundSalePrice(offer)), Adapters.PttAvm.PttAvmPriceInventoryRules.VatRate(offer.VatRate), offer.PriceVersion);
            changed = true;
        }
        if (offer.LastStockProjectionVersion is null && remote.StockQuantity is { } remoteStock)
        {
            var inventory = await db.InventoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == offer.VariantId && x.LocationCode == "MAIN", cancellationToken);
            if (inventory is not null && Adapters.PttAvm.PttAvmPriceInventoryRules.Quantity(inventory.Available - offer.SafetyStock) == Adapters.PttAvm.PttAvmPriceInventoryRules.Quantity(remoteStock))
            {
                offer.LastStockProjectionVersion = inventory.ProjectionVersion;
                changed = true;
            }
        }
        if (changed && db.Entry(offer).State != EntityState.Added) offer.Version++;
    }
}
