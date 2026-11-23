using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// Y5/O7 (scan 23 Nov 2026): an open issue whose subject is gone, or whose problem the data proves no longer holds, is
// resolved. The owner deleted every product on 17 Nov and imported again under new ids; the issues keyed by the old variant
// ids were never named again, so about 1,350 of them stayed open beside the real work (1,041 Web Color, 309 PttAVM
// variants) until the 30-day stale pass. Nothing in the delete paths or the import settled them.
//
// A rule closes an issue only when the database proves the warning is no longer true; in doubt it stays open. Each rule
// names the issue codes it may touch (an issue of any other code is never resolved here, whatever its key) and reads its
// subject from the key the way the writer built it: the variant id the key names is no longer in the catalog, the model
// product a "variants on several cards" warning named has its product link now, the product whose import failed was
// imported after the failure, the observe-only period ended, the connection was deleted. A warning whose condition needs
// the marketplace's own data to judge (a variant linked to the wrong panel variant, a colour PttAVM does not list, a
// multi-code clash) is left to its producer, which resolves it on the next import, or to the 30-day stale pass (review
// 23 Nov 2026). History is kept: the issue is resolved (the reason added to its summary), not deleted; the retention
// removes it a month later as any resolved issue. A later occurrence under the same key reopens it as before.
public static class OrphanedOperationalIssues
{
    public const string ResolvedStatus = nameof(IssueStatus.Resolved);

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

    public const string MappingUnmatchedCode = "PRODUCT_MAPPING_UNMATCHED";
    public const string ImportFailedCode = "PRODUCT_IMPORT_FAILED";
    public const string ListingObserveCode = "LISTING_VERIFY_OBSERVE";

    public sealed record Rule(string Name, string Reason, IReadOnlyList<string> Codes, string Where)
    {
        // The whole match of the rule: one of its codes, its key condition.
        public string Match => $"""i."Code" IN ({string.Join(", ", Codes.Select(code => $"'{code}'"))}) AND ({Where})""";

        // One bounded batch: the oldest open issues the rule matches (@tenant: one tenant, NULL: every tenant), resolved
        // with the reason added to the summary; the codes of those it resolved come back. Built from constants only. EF
        // formats raw SQL as a composite format string, so the regex quantifiers' braces are doubled.
        public string UpdateSql => $"""
            WITH doomed AS (
                SELECT i."Id" FROM ops.operational_issues i
                WHERE i."Status" <> '{ResolvedStatus}' AND i."TenantId" IS NOT NULL
                  AND (CAST(@tenant AS uuid) IS NULL OR i."TenantId" = @tenant)
                  AND {Match.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal)}
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
        new("variant-gone", "Otomatik kapatıldı: kaydın konusu olan panel varyantı artık yok.",
            ["PRODUCT_WEBCOLOR_MAPPING_REQUIRED", "PRODUCT_WEBCOLOR_VALUE_UNMAPPED", "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED"], $"""
            (i."DedupeKey" LIKE 'product-web-color-mapping:%' OR i."DedupeKey" LIKE 'product-web-color-value:%' OR i."DedupeKey" LIKE 'product-attribute-value:%')
            AND {VariantAtThree} IS NOT NULL AND {VariantGone}
            """),
        // stock-projection-offer-invalid:{connection:N}:{variant:N} (the offer codes), product-sync-model-code-conflict:
        // {connection}:{variant:N}, and PttAVM's product-mapping-unmatched:{connection}:{variant:N}.
        new("variant-gone-by-connection", "Otomatik kapatıldı: kaydın konusu olan panel varyantı artık yok.",
            [.. PriceStockOfferIssueCodes.All, ModelCodeUniquenessPolicy.ChangeConflictCode, MappingUnmatchedCode], $"""
            (((i."DedupeKey" LIKE 'stock-projection-offer-invalid:%' OR i."DedupeKey" LIKE 'product-sync-model-code-conflict:%') AND {Hex32AtThreeAlone})
              OR (i."Code" = '{MappingUnmatchedCode}' AND i."DedupeKey" LIKE '{Unmatched}%' AND {Hex32AtThreeAlone}
                AND EXISTS (SELECT 1 FROM integration.platform_connections c WHERE c."TenantId" = i."TenantId" AND c."Id" = {Connection} AND c."PlatformCode" = 'PTTAVM')))
            AND {VariantAtThree} IS NOT NULL AND {VariantGone}
            """),
        // product-mapping-unmatched:{connection}:main:… / group:…: the N11 / Pazarama model product whose variants fell on
        // several panel cards (CatalogImportService.MapExistingCatalogProduct, written only while it has no product link;
        // a variant id never starts with main: or group:). Its product link exists now, so the warning cannot hold.
        new("model-product-linked", "Otomatik kapatıldı: pazaryeri ürünü panelde artık bağlı.", [MappingUnmatchedCode], $"""
            i."DedupeKey" LIKE '{Unmatched}%' AND {Connection} IS NOT NULL AND ({UnmatchedListing} LIKE 'main:_%' OR {UnmatchedListing} LIKE 'group:_%')
            AND EXISTS (SELECT 1 FROM catalog.marketplace_product_links l WHERE l."TenantId" = i."TenantId" AND l."ConnectionId" = {Connection} AND l."ExternalId" = {UnmatchedListing})
            """),
        // product-sync-import:{connection}:{marketplace product}: a later import stored the product (its link was imported
        // after the failure was last seen; a failed update of a product already linked keeps its older import time). A
        // catalog import does not resolve the failure itself, only the retry run does.
        new("imported-after-failure", "Otomatik kapatıldı: ürün hatadan sonra panele alındı.", [ImportFailedCode], $"""
            i."DedupeKey" LIKE '{ImportFailed}%' AND {Connection} IS NOT NULL
            AND {FailedProduct} <> '' AND {FailedProduct} NOT LIKE 'missing:%'
            AND EXISTS (SELECT 1 FROM catalog.marketplace_product_links l WHERE l."TenantId" = i."TenantId" AND l."ConnectionId" = {Connection} AND l."ExternalId" = {FailedProduct}
              AND l."LastImportedAt" > i."LastSeenAt")
            """),
        // listing-verify-observe:{connection}: the observe-only runs are over and at least one run after them marked states.
        new("observe-period-over", "Otomatik kapatıldı: gözlem dönemi bitti; tarama artık durumları kendisi işaretliyor.", [ListingObserveCode], $$"""
            i."DedupeKey" LIKE 'listing-verify-observe:%' AND {{Connection}} IS NOT NULL
            AND EXISTS (SELECT 1 FROM integration.sync_cursors s WHERE s."TenantId" = i."TenantId" AND s."ConnectionId" = {{Connection}}
              AND s."ResourceType" = '{{ListingVerificationPolicy.StateCursorResource}}'
              AND COALESCE(CAST(substring(s."OpaqueCursor" from '"completedRuns":([0-9]{1,9})') AS int), 0) > {{ListingVerificationPolicy.ObserveOnlyRuns}})
            """),
        // An issue about one connection's listings whose connection was deleted (Bağlantıyı Sil keeps the row as DELETED).
        new("connection-deleted", "Otomatik kapatıldı: bağlantı silindi.",
            [MappingUnmatchedCode, ImportFailedCode, "PRODUCT_BARCODE_CONFLICT", ListingObserveCode, .. PriceStockOfferIssueCodes.All, ModelCodeUniquenessPolicy.ChangeConflictCode, "PTTAVM_PRODUCT_CODE_NOT_MODEL"], $"""
            (i."DedupeKey" LIKE '{Unmatched}%' OR i."DedupeKey" LIKE '{ImportFailed}%' OR i."DedupeKey" LIKE 'product-sync-barcode-conflict:%'
              OR i."DedupeKey" LIKE 'listing-verify-observe:%' OR i."DedupeKey" LIKE 'stock-projection-offer-invalid:%'
              OR i."DedupeKey" LIKE 'product-sync-model-code-conflict:%' OR i."DedupeKey" LIKE 'pttavm-product-code:%' OR i."DedupeKey" LIKE 'pttavm-product-link:%')
            AND {Connection} IS NOT NULL
            AND EXISTS (SELECT 1 FROM integration.platform_connections c WHERE c."TenantId" = i."TenantId" AND c."Id" = {Connection} AND c."Status" = 'DELETED')
            """)
    ];

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
        return counts;
    }
}
