using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// Y5/O7 (scan 23 Nov 2026): an open issue whose subject is gone, or whose problem the data no longer holds, is resolved.
// The owner deleted every product on 17 Nov and imported again under new ids; the issues keyed by the old variant ids were
// never named again, so about 1,500 of them stayed open beside the real work (1,041 Web Color, 309 PttAVM variants) until
// the 30-day stale pass. Nothing in the delete paths or the import settled them.
//
// Each rule reads its subject from the issue key, the way the writer built it, and closes the issue only when the database
// answers for certain: the variant id the key names is no longer in the catalog, the marketplace listing it reported as
// unmatched is linked now, the product whose import failed is in the panel now, the code clash is gone, the observe-only
// period ended, the connection was deleted. A key the rule cannot read, or a subject that still exists, stays open
// (unknown stays unknown). History is kept: the issue is resolved (with the reason added to its summary), not deleted; the
// retention removes it a month later as any resolved issue. A later occurrence under the same key reopens it as before.
public static class OrphanedOperationalIssues
{
    public const string ResolvedStatus = nameof(IssueStatus.Resolved);
    public const string BarcodeConflictCode = "PRODUCT_BARCODE_CONFLICT";
    public const string BarcodeConflictPrefix = "product-sync-barcode-conflict:";

    // A uuid in the D or N form (the writers use both), else NULL: the CASE keeps a key that does not hold one from the cast.
    private static string Uuid(string text) =>
        $$"""(CASE WHEN {{text}} ~ '^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$' THEN CAST({{text}} AS uuid) END)""";

    private static string Part(int index) => $"""split_part(i."DedupeKey", ':', {index})""";

    // What follows "<prefix><connection>:" in a key: the marketplace id the writer put there (it may hold ':' itself).
    private static string AfterConnection(string prefix) => $"""substring(i."DedupeKey" from {prefix.Length + 2} + length({Part(2)}))""";

    private static readonly string Connection = Uuid(Part(2));
    private static readonly string VariantAtThree = Uuid(Part(3));
    private const string Hex32AtThreeAlone = """split_part(i."DedupeKey", ':', 3) ~ '^[0-9a-f]{32}$' AND split_part(i."DedupeKey", ':', 4) = ''""";
    private const string Unmatched = "product-mapping-unmatched:";
    private const string ImportFailed = "product-sync-import:";
    private static readonly string UnmatchedListing = AfterConnection(Unmatched);
    private static readonly string FailedProduct = AfterConnection(ImportFailed);
    private static readonly string VariantGone = $"""NOT EXISTS (SELECT 1 FROM catalog.product_variants v WHERE v."TenantId" = i."TenantId" AND v."Id" = {VariantAtThree})""";
    // PttAVM alone keys an unmatched issue by the panel variant (product-mapping-unmatched:{connection}:{variant:N}).
    private static readonly string PttAvmVariantKey = $"""
        i."DedupeKey" LIKE '{Unmatched}%' AND {Hex32AtThreeAlone}
        AND EXISTS (SELECT 1 FROM integration.platform_connections c WHERE c."TenantId" = i."TenantId" AND c."Id" = {Connection} AND c."PlatformCode" = 'PTTAVM')
        """;

    public sealed record Rule(string Name, string Reason, string Where)
    {
        // One bounded batch: the oldest open issues the rule matches (@tenant: one tenant, NULL: every tenant), resolved
        // with the reason added to the summary; the codes of those it resolved come back. Built from constants only. EF
        // formats raw SQL as a composite format string, so the regex quantifiers' braces are doubled.
        public string UpdateSql => $"""
            WITH doomed AS (
                SELECT i."Id" FROM ops.operational_issues i
                WHERE i."Status" <> '{ResolvedStatus}' AND i."TenantId" IS NOT NULL
                  AND (CAST(@tenant AS uuid) IS NULL OR i."TenantId" = @tenant)
                  AND ({Where.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal)})
                ORDER BY i."Id"
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            UPDATE ops.operational_issues i SET "Status" = '{ResolvedStatus}', "ResolvedAt" = @now, "Summary" = i."Summary" || ' · ' || @reason
            FROM doomed WHERE i."Id" = doomed."Id"
            RETURNING i."Code" AS "Value"
            """;
    }

    public static readonly IReadOnlyList<Rule> Rules =
    [
        // product-web-color-mapping / product-web-color-value / product-attribute-value:{product}:{variant}:… (CatalogAttributeService).
        new("variant-gone", "Otomatik kapatıldı: kaydın konusu olan panel varyantı artık yok.", $"""
            (i."DedupeKey" LIKE 'product-web-color-mapping:%' OR i."DedupeKey" LIKE 'product-web-color-value:%' OR i."DedupeKey" LIKE 'product-attribute-value:%')
            AND {VariantAtThree} IS NOT NULL AND {VariantGone}
            """),
        // stock-projection-offer-invalid:{connection:N}:{variant:N}, product-sync-model-code-conflict:{connection}:{variant:N},
        // and PttAVM's product-mapping-unmatched:{connection}:{variant:N}.
        new("variant-gone-by-connection", "Otomatik kapatıldı: kaydın konusu olan panel varyantı artık yok.", $"""
            (((i."DedupeKey" LIKE 'stock-projection-offer-invalid:%' OR i."DedupeKey" LIKE 'product-sync-model-code-conflict:%') AND {Hex32AtThreeAlone})
              OR ({PttAvmVariantKey}))
            AND {VariantAtThree} IS NOT NULL AND {VariantGone}
            """),
        // The PttAVM variant the issue named is linked on that connection now.
        new("pttavm-variant-linked", "Otomatik kapatıldı: panel varyantı pazaryerinde artık bağlı.", $"""
            {PttAvmVariantKey}
            AND EXISTS (SELECT 1 FROM catalog.marketplace_variant_links l WHERE l."TenantId" = i."TenantId" AND l."ConnectionId" = {Connection} AND l."VariantId" = {VariantAtThree})
            """),
        // product-mapping-unmatched:{connection}:{marketplace id}: the listing is linked now. A variant link answers for any
        // key; a product link only for a key that names a product (N11 main:, Pazarama group:, PttAVM ptt:, and Shopify and
        // Hepsiburada, whose unmatched keys are their product ids), so a variant id that equals a product id is not taken.
        new("listing-linked", "Otomatik kapatıldı: pazaryeri ürünü panelde artık bağlı.", $"""
            i."DedupeKey" LIKE '{Unmatched}%' AND {Connection} IS NOT NULL AND NOT ({Hex32AtThreeAlone}) AND {UnmatchedListing} <> ''
            AND (EXISTS (SELECT 1 FROM catalog.marketplace_variant_links l WHERE l."TenantId" = i."TenantId" AND l."ConnectionId" = {Connection} AND l."ExternalId" = {UnmatchedListing})
              OR (EXISTS (SELECT 1 FROM catalog.marketplace_product_links l WHERE l."TenantId" = i."TenantId" AND l."ConnectionId" = {Connection} AND l."ExternalId" = regexp_replace({UnmatchedListing}, '^ptt:', ''))
                AND ({UnmatchedListing} LIKE 'main:%' OR {UnmatchedListing} LIKE 'group:%' OR {UnmatchedListing} LIKE 'ptt:%'
                  OR EXISTS (SELECT 1 FROM integration.platform_connections c WHERE c."TenantId" = i."TenantId" AND c."Id" = {Connection} AND c."PlatformCode" IN ('SHOPIFY', 'HEPSIBURADA')))))
            """),
        // product-sync-import:{connection}:{marketplace product}: a later import stored it (a catalog import does not resolve
        // the failure itself, only the retry run does).
        new("import-stored", "Otomatik kapatıldı: ürün sonradan panele alındı.", $"""
            i."Code" = 'PRODUCT_IMPORT_FAILED' AND i."DedupeKey" LIKE '{ImportFailed}%' AND {Connection} IS NOT NULL
            AND {FailedProduct} <> '' AND {FailedProduct} NOT LIKE 'missing:%'
            AND EXISTS (SELECT 1 FROM catalog.marketplace_product_links l WHERE l."TenantId" = i."TenantId" AND l."ConnectionId" = {Connection} AND l."ExternalId" = {FailedProduct})
            """),
        // listing-verify-observe:{connection}: the observe-only runs are over and at least one run after them marked states.
        new("observe-period-over", "Otomatik kapatıldı: gözlem dönemi bitti; tarama artık durumları kendisi işaretliyor.", $$"""
            i."Code" = 'LISTING_VERIFY_OBSERVE' AND i."DedupeKey" LIKE 'listing-verify-observe:%' AND {{Connection}} IS NOT NULL
            AND EXISTS (SELECT 1 FROM integration.sync_cursors s WHERE s."TenantId" = i."TenantId" AND s."ConnectionId" = {{Connection}}
              AND s."ResourceType" = '{{ListingVerificationPolicy.StateCursorResource}}'
              AND COALESCE(CAST(substring(s."OpaqueCursor" from '"completedRuns":([0-9]{1,9})') AS int), 0) > {{ListingVerificationPolicy.ObserveOnlyRuns}})
            """),
        // An issue about one connection's listings whose connection was deleted (Bağlantıyı Sil keeps the row as DELETED).
        new("connection-deleted", "Otomatik kapatıldı: bağlantı silindi.", $"""
            (i."DedupeKey" LIKE '{Unmatched}%' OR i."DedupeKey" LIKE '{ImportFailed}%' OR i."DedupeKey" LIKE '{BarcodeConflictPrefix}%'
              OR i."DedupeKey" LIKE 'listing-verify-observe:%' OR i."DedupeKey" LIKE 'stock-projection-offer-invalid:%'
              OR i."DedupeKey" LIKE 'product-sync-model-code-conflict:%' OR i."DedupeKey" LIKE 'pttavm-product-code:%' OR i."DedupeKey" LIKE 'pttavm-product-link:%')
            AND {Connection} IS NOT NULL
            AND EXISTS (SELECT 1 FROM integration.platform_connections c WHERE c."TenantId" = i."TenantId" AND c."Id" = {Connection} AND c."Status" = 'DELETED')
            """)
    ];

    public const string BarcodeConflictReason = "Otomatik kapatıldı: kod artık birden fazla panel ürününde değil.";

    // Resolves the orphaned open issues of one tenant (null: every tenant), at most batchSize x maxBatches per rule, and
    // returns how many it resolved per issue code. Idempotent: a second run finds nothing.
    public static async Task<IReadOnlyDictionary<string, int>> ResolveAsync(AppDbContext db, Guid? tenantId, DateTimeOffset now, int batchSize, int maxBatches, CancellationToken cancellationToken)
    {
        var resolvedAt = now.ToUniversalTime();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            for (var batch = 0; batch < maxBatches; batch++)
            {
                var codes = await db.Database.SqlQueryRaw<string>(rule.UpdateSql,
                    new NpgsqlParameter("tenant", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = tenantId is { } id ? (object)id : DBNull.Value },
                    new NpgsqlParameter<int>("batch", batchSize),
                    new NpgsqlParameter<DateTimeOffset>("now", resolvedAt),
                    new NpgsqlParameter<string>("reason", rule.Reason)).ToListAsync(cancellationToken);
                foreach (var code in codes) counts[code] = counts.GetValueOrDefault(code) + 1;
                if (codes.Count < batchSize) break;
            }
        }
        var barcode = await ResolveSettledBarcodeConflictsAsync(db, tenantId, resolvedAt, batchSize * maxBatches, cancellationToken);
        if (barcode > 0) counts[BarcodeConflictCode] = counts.GetValueOrDefault(BarcodeConflictCode) + barcode;
        return counts;
    }

    // product-sync-barcode-conflict:{connection}:{code}: the code (a stored key form, CatalogImportService) was held by the
    // variants of more than one panel product. Resolved when fewer than two products hold it now, by stock code or barcode
    // in any stored form (CatalogKeyNormalizer.LookupKeys), which counts at least every holder the import counted.
    private static async Task<int> ResolveSettledBarcodeConflictsAsync(AppDbContext db, Guid? tenantId, DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var open = await db.OperationalIssues
            .Where(x => x.TenantId != null && (tenantId == null || x.TenantId == tenantId) && x.Status != IssueStatus.Resolved
                && x.Code == BarcodeConflictCode && x.DedupeKey.StartsWith(BarcodeConflictPrefix))
            .OrderBy(x => x.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var resolved = 0;
        foreach (var issue in open)
        {
            var rest = issue.DedupeKey[BarcodeConflictPrefix.Length..];
            var separator = rest.IndexOf(':');
            if (separator <= 0 || !Guid.TryParse(rest[..separator], out _)) continue;
            var keys = CatalogKeyNormalizer.LookupKeys(rest[(separator + 1)..]);
            if (keys.Length == 0) continue;
            var issueTenant = issue.TenantId!.Value;
            var holders = await db.ProductVariants.AsNoTracking()
                .Where(x => x.TenantId == issueTenant && ((x.SkuNormalized != null && keys.Contains(x.SkuNormalized)) || (x.BarcodeNormalized != null && keys.Contains(x.BarcodeNormalized))))
                .Select(x => x.ProductId)
                .Distinct()
                .OrderBy(id => id)
                .Take(2)
                .CountAsync(cancellationToken);
            if (holders >= 2) continue;
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
            issue.Summary = $"{issue.Summary} · {BarcodeConflictReason}";
            resolved++;
        }
        if (resolved > 0) await db.SaveChangesAsync(cancellationToken);
        return resolved;
    }
}
